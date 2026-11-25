// CheatMenu - untitledloader mod (game side).
//
// Press F2 in game (or the key set in Config/cheatmenu.cfg) to open the external Cheat Menu app
// (UntitledLoader/Mods/CheatMenu/CheatMenuApp.exe). The app talks to this mod over a local-only
// TCP connection (127.0.0.1) and the mod runs each command on the game's main thread.
//
// Features: spawn any item (with condition / full random), random vehicle, godmode, fly (noclip),
// refuel everything nearby (fuel with each engine's own mix, oil, coolant), 5x liquid pouring.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UntitledLoader;

namespace CheatMenu
{
    public class CheatMenuMod : Mod
    {
        public override string ID { get { return "cheatmenu"; } }
        public override string Name { get { return "Cheat Menu"; } }
        public override string Version { get { return "1.0.1"; } }
        public override string MinLoaderVersion { get { return "1.1.0"; } }
        public override string Author { get { return "UntitledTLD"; } }

        public const int Port = 47811;

        // keys the game never reads from code (checked against the decompiled game); the live
        // keybinds are checked on top of this when the game is ready
        static readonly KeyCode[] Fallbacks = { KeyCode.F2, KeyCode.F3, KeyCode.K, KeyCode.O, KeyCode.F11 };
        static readonly KeyCode[] HardcodedByGame = { KeyCode.F1, KeyCode.F4, KeyCode.F8, KeyCode.F9, KeyCode.F10,
            KeyCode.End, KeyCode.CapsLock, KeyCode.X, KeyCode.Escape, KeyCode.Alpha0, KeyCode.BackQuote };

        KeyCode menuKey;
        KeySetting keySetting;
        FloatSetting radiusSetting, flySetting, pourSetting;

        void ApplySettings()
        {
            menuKey = keySetting.Value;
            refuelRadius = radiusSetting.Value;
            flySpeed = flySetting.Value;
            pourMultiplier = pourSetting.Value;
        }

        public override void OnSettingsChanged()
        {
            ApplySettings();
            keyChecked = false; // re-check the new key against the game's keybinds
            basePourSpeed = -1f; pourOwner = null;
        }
        bool keyChecked;
        float refuelRadius, flySpeed, pourMultiplier;
        string appPath;

        bool god, fly, fastPour;
        volatile bool appOpen;      // the external app is visible: game gives up mouse/keyboard
        float nextProcCheck;

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        float basePourSpeed = -1f;
        mainscript pourOwner;

        // --- command server
        class Job { public string cmd; public string result; public readonly ManualResetEvent done = new ManualResetEvent(false); }
        readonly Queue<Job> jobs = new Queue<Job>();
        TcpListener listener;
        Thread serverThread;
        volatile bool running;

        public override void OnLoad()
        {
            keySetting = Settings.Key("MenuKey", KeyCode.F2, "Opens/closes the Cheat Menu app. Checked against the game's keybinds at start (falls back to F3/K/O/F11).");
            radiusSetting = Settings.Slider("RefuelRadius", 30f, 5f, 200f, "Refuel everything near me: reach in metres.");
            flySetting = Settings.Slider("FlySpeed", 12f, 2f, 100f, "Fly speed in m/s (hold Shift for 5x).");
            pourSetting = Settings.Slider("FastPourMultiplier", 5f, 1f, 20f, "How much faster liquids pour with Fast pour on.");
            ApplySettings();
            appPath = Path.Combine(Folder, "CheatMenuApp.exe");

            // keep the game updating while the external app has focus
            Application.runInBackground = true;
            StartServer();
        }

        // ------------------------------------------------------------------ keybind
        void CheckKey()
        {
            keyChecked = true;
            var used = new HashSet<KeyCode>(HardcodedByGame);
            try
            {
                var inp = settingsscript.s != null ? settingsscript.s.S : null;
                var list = inp != null ? inp.inputs : null;
                if (list != null)
                    foreach (var a in list)
                        if (a != null && a.keys != null)
                            foreach (var key in a.keys)
                                if (key != null && !key.axis && key.key != KeyCode.None) used.Add(key.key);
            }
            catch (Exception e) { Log.Warn("could not read the game's keybinds: " + e.Message); }

            if (!used.Contains(menuKey)) { Log.Info("menu key " + menuKey + " is free"); return; }
            var old = menuKey;
            foreach (var f in Fallbacks) if (!used.Contains(f)) { menuKey = f; break; }
            Log.Warn("menu key " + old + " is used by the game, using " + menuKey + " instead");
            ModLoader.Banner("Cheat Menu: " + old + " is taken by the game, press " + menuKey + " instead", 8f);
        }

        // ------------------------------------------------------------------ update
        public override void OnGameExit()
        {
            god = fly = fastPour = false;
            basePourSpeed = -1f; pourOwner = null;
        }

        public override void OnUpdate()
        {
            ProcessJobs();
            if (!Game.IsReady) return;
            if (!keyChecked) CheckKey();
            if (ModInput.GetKeyDown(menuKey)) ToggleApp();
            UpdateInputLock();

            var p = Game.Player;
            if (god) ApplyGod(p);
            if (fly) ApplyFly(p);
            ApplyPour();
        }

        static bool AppRunning() { return Process.GetProcessesByName("CheatMenuApp").Length > 0; }

        void ToggleApp()
        {
            try
            {
                AllowSetForegroundWindow(-1); // let the app take focus from the game
                if (AppRunning())
                {
                    Send("toggle"); // app shows/hides itself and reports back with "menu 1/0"
                    Log.Info("menu key: toggle");
                    return;
                }
                if (!File.Exists(appPath)) { ModLoader.Banner("Cheat Menu app missing: " + appPath, 6f); Log.Error("missing " + appPath); return; }
                Process.Start(new ProcessStartInfo(appPath, Port.ToString()) { UseShellExecute = true, WorkingDirectory = Folder });
                appOpen = true;
                Log.Info("menu key: started " + appPath);
            }
            catch (Exception e) { Log.Error("could not start the app: " + e); }
        }

        // tell the running app something (it listens on Port+1)
        static void Send(string msg)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    c.Connect(IPAddress.Loopback, Port + 1);
                    var b = Encoding.UTF8.GetBytes(msg + "\n");
                    c.GetStream().Write(b, 0, b.Length);
                }
            }
            catch { }
        }

        // While the app is open the game frees the cursor and ignores mouse look - like its own dev
        // menu does - without pausing, so you still see the world. Released as soon as it closes.
        void UpdateInputLock()
        {
            if (appOpen && Time.time > nextProcCheck)
            {
                nextProcCheck = Time.time + 1f;
                if (!AppRunning()) appOpen = false; // app was killed / crashed
            }
            // shared with the untitledloader Manager so the two apps never fight over the mouse
            GameInput.Release("cheatmenu.app", appOpen);
        }

        void ApplyGod(fpscontroller p)
        {
            if (kaposztaleves.s != null) kaposztaleves.s.settings.god = true; // the game's own "never die" flag
            if (p == null || p.died || p.survival == null) return;
            var s = p.survival;
            if (s.blood != null && s.blood.F != null && s.blood.F.GetAmount() < s.blood.F.maxC)
            {
                s.blood.F.ChangeOne(s.blood.F.maxC, mainscript.fluidenum.blood);
                s.UpdHp(false);
            }
            if (s.hp < s.maxHp) s.HealInstant(s.maxHp);
            if (s.food < s.maxFood) s.food = s.maxFood;
            if (s.water < s.maxWater) s.water = s.maxWater;
            if (s.toHp < 0f) s.toHp = 0f;
        }

        void ApplyFly(fpscontroller p)
        {
            if (p == null || p.died || p.seat != null) return;
            if (kaposztaleves.s != null) kaposztaleves.s.settings.fly = true;
            if (p.RB != null) { p.RB.useGravity = false; p.RB.velocity = Vector3.zero; }
            if (!Application.isFocused) return;
            float sp = flySpeed * (Input.GetButton("run") ? 5f : 1f) * Time.deltaTime;
            var fwd = Vector3.ProjectOnPlane(p.BodyRot.forward, Vector3.up).normalized;
            var right = Vector3.ProjectOnPlane(p.BodyRot.right, Vector3.up).normalized;
            var move = Vector3.zero;
            if (Input.GetButton("forward")) move += fwd;
            if (Input.GetButton("backward")) move -= fwd;
            if (Input.GetButton("right")) move += right;
            if (Input.GetButton("left")) move -= right;
            if (Input.GetButton("jump")) move += Vector3.up;
            if (Input.GetButton("duck")) move -= Vector3.up;
            p.transform.root.position += move * sp;
        }

        void SetFly(bool v)
        {
            fly = v;
            if (v) return;
            var p = Game.Player;
            if (kaposztaleves.s != null) kaposztaleves.s.settings.fly = false;
            if (p != null && p.RB != null) p.RB.useGravity = true;
            if (p != null) p.godModeTillGrounded = true; // no fall death when dropping out of fly
        }

        void SetGod(bool v)
        {
            god = v;
            if (!v && kaposztaleves.s != null) kaposztaleves.s.settings.god = false;
        }

        void ApplyPour()
        {
            var m = mainscript.s;
            if (m == null) return;
            if (pourOwner != m) { pourOwner = m; basePourSpeed = m.pourSpeed; }
            float want = fastPour ? basePourSpeed * pourMultiplier : basePourSpeed;
            if (Mathf.Abs(m.pourSpeed - want) > 1e-6f)
            {
                if (Mathf.Abs(m.pourSpeed - basePourSpeed) > 1e-6f && Mathf.Abs(m.pourSpeed - basePourSpeed * pourMultiplier) > 1e-6f)
                    basePourSpeed = m.pourSpeed; // something else changed it: new base
                m.pourSpeed = fastPour ? basePourSpeed * pourMultiplier : basePourSpeed;
            }
        }

        // ------------------------------------------------------------------ items
        static string Category(GameObject g, string name)
        {
            if (g.GetComponentInChildren<carscript>(true) != null) return name.ToLowerInvariant().Contains("full") ? "Full cars" : "Stripped cars";
            if (g.GetComponentInChildren<enginescript>(true) != null) return "Engines";
            if (g.GetComponentInChildren<coolantTankscript>(true) != null) return "Coolant";
            var n = name.ToLowerInvariant();
            if (g.GetComponentInChildren<meterscript>(true) != null || n.Contains("kmh") || n.Contains("tacho") || n.Contains("gauge") || n.Contains("meter")) return "Gauges";
            var a = g.GetComponentInChildren<attachablescript>(true);
            return a != null && a.isPart ? "Parts" : "Other";
        }

        string ItemList()
        {
            var sb = new StringBuilder();
            var items = itemdatabase.s.items;
            var err = itemdatabase.s.Item(0);
            for (int i = 1; i < items.Length; i++)
            {
                var g = items[i];
                if (g == null || g == err) continue;
                var name = Game.ItemName(i);
                sb.Append(i).Append('\t').Append(Category(g, name)).Append('\t').Append(ItemIndex.Name(i)).Append('\t').Append(name).Append('\n');
            }
            sb.Append("end");
            return sb.ToString();
        }

        string Spawn(int id, bool fullRandom, int condition)
        {
            var prefab = itemdatabase.s.Item(id);
            if (prefab == null || prefab == itemdatabase.s.Item(0)) return "err no item " + id;
            // the game's own spawn: where you look, with parts/condition/saving set up
            if (fullRandom) mainscript.s.Spawn(prefab, true, -1, -1);
            else mainscript.s.Spawn(prefab, false, Mathf.Clamp(condition, 0, 4), -1);
            return "ok spawned " + ItemIndex.Name(id) + (fullRandom ? " (full random)" : "");
        }

        string RandomVehicle()
        {
            var cars = new List<int>();
            var all = new List<int>();
            var items = itemdatabase.s.items;
            for (int i = 1; i < items.Length; i++)
            {
                if (items[i] == null || items[i].GetComponentInChildren<carscript>(true) == null) continue;
                all.Add(i);
                if (Game.ItemName(i).ToLowerInvariant().Contains("full")) cars.Add(i);
            }
            if (cars.Count == 0) cars = all;
            if (cars.Count == 0) return "err no vehicles";
            return Spawn(cars[UnityEngine.Random.Range(0, cars.Count)], true, 0);
        }

        // ------------------------------------------------------------------ refuel
        string RefuelNearby()
        {
            var p = Game.Player;
            var pos = p.transform.position;
            float r2 = refuelRadius * refuelRadius;
            int engines = 0, cars = 0;
            var done = new HashSet<tankscript>();
            foreach (var eng in UnityEngine.Object.FindObjectsOfType<enginescript>())
            {
                if (eng == null || (eng.transform.position - pos).sqrMagnitude > r2) continue;
                var car = eng.GetComponentInParent<carscript>();
                if (car != null && car.Engine != eng) car = null;
                var fuel = eng.Fuel != null ? eng.Fuel : (car != null ? car.Tank : null);
                if (fuel != null && done.Add(fuel)) FillWithMix(fuel, eng.FuelConsumption);
                if (eng.T != null && done.Add(eng.T)) Fill(eng.T, eng.Oilfluid);
                engines++;
            }
            foreach (var car in Game.Cars)
            {
                if (car == null || (car.transform.position - pos).sqrMagnitude > r2) continue;
                if (car.Engine != null && car.Tank != null && done.Add(car.Tank)) FillWithMix(car.Tank, car.Engine.FuelConsumption);
                if (car.coolant != null && car.coolant.coolant != null && done.Add(car.coolant.coolant)) Fill(car.coolant.coolant, mainscript.fluidenum.water);
                if (car.Engine != null) car.Engine.temp = Mathf.Min(car.Engine.temp, car.Engine.minOptimalTemp1);
                cars++;
            }
            return "ok refuelled " + engines + " engine(s), " + cars + " car(s) within " + refuelRadius + " m";
        }

        static void Fill(tankscript t, mainscript.fluidenum type)
        {
            if (t == null || t.F == null) return;
            if (t.F.GetAmount() > 0f && t.F.GetPercent(type) < 0.99f) t.F.fluids.Clear();
            t.F.ChangeOne(t.F.maxC, type);
            t.Upd();
        }

        static void FillWithMix(tankscript t, mainscript.fluidcontainer mix)
        {
            if (t == null || t.F == null) return;
            if (mix == null || mix.fluids == null || mix.fluids.Count == 0) { t.F.ChangeWithoutMix(t.F.maxC); t.Upd(); return; }
            float total = 0f;
            foreach (var f in mix.fluids) total += f.amount;
            if (total <= 0f) return;
            t.F.fluids.Clear(); // drain wrong/old fuel, refill with the engine's exact mix
            foreach (var f in mix.fluids) t.F.ChangeOne(t.F.maxC * f.amount / total, f.type);
            t.Upd();
        }

        // ------------------------------------------------------------------ commands (main thread)
        string Execute(string line)
        {
            var a = line.Trim().Split(' ');
            var cmd = a[0].ToLowerInvariant();
            if (cmd == "ping") return "ok pong";
            if (cmd == "menu") { appOpen = a.Length > 1 && a[1] == "1"; return "ok menu " + (appOpen ? "open" : "closed"); }
            if (cmd == "state")
                return "ok ready=" + (Game.IsReady ? 1 : 0) + " god=" + (god ? 1 : 0) + " fly=" + (fly ? 1 : 0) + " fastpour=" + (fastPour ? 1 : 0) + " key=" + menuKey;
            if (!Game.IsReady) return "err load a save first";
            switch (cmd)
            {
                case "items": return ItemList();
                case "spawn": return Spawn(int.Parse(a[1]), false, a.Length > 2 ? int.Parse(a[2]) : 0);
                case "spawnrandom": return Spawn(int.Parse(a[1]), true, 0);
                case "randomvehicle": return RandomVehicle();
                case "god": SetGod(a[1] == "1"); return "ok godmode " + (god ? "on" : "off");
                case "fly": SetFly(a[1] == "1"); return "ok fly " + (fly ? "on" : "off");
                case "fastpour": fastPour = a[1] == "1"; return "ok fast pouring " + (fastPour ? "on (" + pourMultiplier + "x)" : "off");
                case "refuel": return RefuelNearby();
            }
            return "err unknown command " + cmd;
        }

        void ProcessJobs()
        {
            while (true)
            {
                Job j;
                lock (jobs) { if (jobs.Count == 0) return; j = jobs.Dequeue(); }
                try { j.result = Execute(j.cmd); }
                catch (Exception e) { j.result = "err " + e.Message; Log.Error("command '" + j.cmd + "': " + e); }
                j.done.Set();
            }
        }

        // ------------------------------------------------------------------ server (background thread)
        void StartServer()
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
                running = true;
                serverThread = new Thread(ServerLoop) { IsBackground = true, Name = "CheatMenuServer" };
                serverThread.Start();
                Log.Info("command server on 127.0.0.1:" + Port);
            }
            catch (Exception e) { Log.Error("could not start command server: " + e.Message); }
        }

        void ServerLoop()
        {
            while (running)
            {
                TcpClient c = null;
                try
                {
                    c = listener.AcceptTcpClient();
                    c.ReceiveTimeout = 5000; c.SendTimeout = 5000;
                    var stream = c.GetStream();
                    var reader = new StreamReader(stream, Encoding.UTF8);
                    var line = reader.ReadLine();
                    if (line == null) continue;
                    var job = new Job { cmd = line };
                    lock (jobs) jobs.Enqueue(job);
                    string result = job.done.WaitOne(8000) ? job.result : "err game did not answer (paused or loading?)";
                    var bytes = Encoding.UTF8.GetBytes(result + "\n");
                    stream.Write(bytes, 0, bytes.Length);
                }
                catch (Exception e) { if (running) Log.Warn("server: " + e.Message); }
                finally { if (c != null) c.Close(); }
            }
        }
    }
}
