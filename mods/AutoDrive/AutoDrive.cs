// AutoDrive - untitledloader mod. Drives the car you sit in along the game's roads.
//
//  Route    : finds the nearest generated road, follows its centre line (road bones) in the direction
//             the car faces and continues onto the next road at junctions (main road first or the
//             straightest way). The route is kept ~250 m ahead and grows as the game generates roads.
//  Obstacles: every 0.1 s a corridor along the route is scanned with physics boxes (from just above
//             the road surface, so small rocks count). Each obstacle becomes a blocked lateral interval;
//             a smooth sideways offset is planned around them. Fully blocked -> stop in front of it.
//  Steering : pure pursuit on the offset path, using the car's real wheelbase and max steer angle.
//  Speed    : per-point limit from curvature (v = sqrt(grip / curvature)), a braking profile so every
//             limit ahead is reached in time, plus traction control, ABS and a sideways-slide guard.
//  Control  : Start/Stop in the untitledloader Manager (F10) or the AutoDrive key (O) in game.
//             Braking or steering hard yourself takes over (optional).
//  Register : optional. "Register car" remembers the car you sit in; AutoDrive then drives THAT car
//             even when you sit in another seat, another car or walk. With nobody in its driver's seat
//             the game ignores a car's controls, so AutoDrive marks it as driven (carscript.driving /
//             driving2) while it is in charge and gives it back (parked, handbrake on) when it stops.
//  Starting : releases the handbrake and cranks the engine itself (a few tries) when it is off.
//             At a standstill it holds the car with the handbrake: in the automatic gearbox the brake
//             pedal at 0 km/h means "reverse" (carscript.AutoShift).
using System;
using System.Collections.Generic;
using UnityEngine;
using UntitledLoader;

namespace AutoDrive
{
    public class AutoDriveMod : Mod
    {
        public override string ID { get { return "autodrive"; } }
        public override string Name { get { return "AutoDrive"; } }
        public override string Version { get { return "1.0.1"; } }
        public override string Author { get { return "UntitledTLD"; } }
        public override string MinLoaderVersion { get { return "1.1.0"; } }

        // ---------------- settings (Manager F10)
        BoolSetting sOn, sAvoid, sHud, sDebug, sTakeover, sAutoStart;
        KeySetting sKey;
        FloatSetting sMaxSpeed, sGrip, sBrake, sAccel, sAvoidWidth, sLane, sLookahead, sDetour, sOffroadSpeed, sMaxDistance;
        const float RoadHalfWidth = 3.2f;   // beyond this the car is on sand / grass
        ChoiceSetting sJunction;

        // ---------------- state
        bool active;                 // actually driving right now
        carscript car;
        carscript registered;        // optional: the car AutoDrive drives wherever you are
        bool remote;                 // driving the registered car (you may be anywhere)
        bool forcedSeat;             // we set car.driving/driving2 because nobody sits in its driver's seat
        int ctrlFrame = -1;          // controls are computed once per frame, written up to twice
        float outSteer, outThrottle, outBrake, outHandbrake;
        bool outIgnition, releasedHandbrake;
        float crankStart = -1f, crankPauseUntil; int crankTries;
        bool tooFar;
        bool savedForceAuto;
        string status = "";
        float statusTime;
        readonly Route route = new Route();
        readonly List<Obstacle> obstacles = new List<Obstacle>();
        float nextScan;
        float throttle, brake, steer;
        float speedInt;
        float wheelbase = 2.5f, steerMax = 30f;
        float bodyHalfWidth = 0.9f, bodyHalfLength = 2.2f;   // measured from the car's colliders
        float targetSpeed;           // m/s
        float blockedAt = -1f;       // route distance of a full blockage, -1 = none
        float tightAt = -1f;         // too fast to swerve around what is here, -1 = fine
        string slowReason = "";      // what is holding the speed below MaxSpeed (shown on the HUD)
        // recovery: hit something / stuck -> reverse, remember the spot, try again
        bool recovering; float recoverStart, recoverSteer, stuckFor; Vector3 recoverFrom;
        readonly List<float> recoverTimes = new List<float>();
        readonly List<KeyValuePair<Vector3, float>> remembered = new List<KeyValuePair<Vector3, float>>();   // world point, time
        BumpSensor bump;
        float lastAbs;
        LineRenderer pathLine;
        float[] offsets = new float[0];   // planned lateral offset per route sample
        readonly Collider[] hits = new Collider[64];   // reused by the obstacle scan (no garbage every 0.1 s)
        const float SampleStep = 2f;      // metres between planning samples
        const int Samples = 100;          // 200 m of planning horizon (enough to stop from 140 km/h)

        public override void OnLoad()
        {
            // settings update from 1.0: the old cornering default (3.2) braked far too much in turns
            if (Config.GetString("SettingsVersion", "", "internal, don't change") != "2")
            {
                if (Config.GetString("CorneringGrip", "4.5") == "3.2") Config.Set("CorneringGrip", "4.5");
                Config.Set("SettingsVersion", "2");
            }
            sOn = Settings.Toggle("AutoDriveOn", false, "Start / stop AutoDrive. Drives the registered car if you registered one, otherwise the car whose driver's seat you sit in.");
            Settings.Button("Start AutoDrive", "Starts driving the registered car, or the car you sit in.", () => SetOn(true));
            Settings.Button("Stop AutoDrive", "Stops and gives the car back to you.", () => SetOn(false));
            Settings.Button("Register car", "Optional. Sit in a car (any seat) and click: AutoDrive will drive this car even when you sit in another seat, another car or walk next to it.", Register);
            Settings.Button("Forget registered car", "Back to normal: AutoDrive drives the car whose driver's seat you sit in.", Forget);
            sAutoStart = Settings.Toggle("AutoStartEngine", true, "Release the handbrake and start the engine by itself when needed.");
            sMaxDistance = Settings.Slider("MaxDistanceFromYou", 300f, 100f, 1000f, "Registered car only: if it gets this far (m) from you it stops and waits, because the game only builds roads and ground near you.");
            sKey = Settings.Key("ToggleKey", KeyCode.O, "Key that starts / stops AutoDrive in game.");
            sMaxSpeed = Settings.Slider("MaxSpeed", 70f, 15f, 140f, "Top speed in km/h. AutoDrive is slower in corners and before obstacles.");
            sGrip = Settings.Slider("CorneringGrip", 4.5f, 1.5f, 8f, "Sideways grip used for corner speed (m/s²). Higher = faster through corners. 4-5 suits asphalt, lower it if the car slides.");
            sBrake = Settings.Slider("Braking", 3.0f, 1f, 7f, "How hard it plans to brake (m/s²). Lower = brakes earlier and gentler.");
            sAccel = Settings.Slider("Acceleration", 0.6f, 0.2f, 1f, "Maximum throttle (0.2 - 1). Lower = gentler starts, less wheelspin.");
            sAvoid = Settings.Toggle("AvoidObstacles", true, "Steer around rocks and other things on the road (stops if the road is fully blocked).");
            sAvoidWidth = Settings.Slider("AvoidWidth", 2.6f, 0.5f, 6f, "How far (m) it may move sideways ON the road to get around obstacles.");
            sDetour = Settings.Slider("MaxDetour", 10f, 0f, 20f, "If something blocks the road, how far (m) it may leave the road onto sand / grass to go around it. 0 = never leave the road, stop instead.");
            sOffroadSpeed = Settings.Slider("OffroadSpeed", 35f, 10f, 80f, "Top speed (km/h) while driving off the road around an obstacle.");
            sLane = Settings.Slider("LaneOffset", 0f, -2.5f, 2.5f, "Drive this many metres right (+) or left (-) of the road centre.");
            sLookahead = Settings.Slider("SteeringLookahead", 1f, 0.5f, 2f, "Steering look-ahead multiplier. Higher = smoother but cuts corners more.");
            sJunction = Settings.Choice("Junctions", "Main road", new[] { "Main road", "Straightest", "Random" }, "Which way to go at junctions.");
            sTakeover = Settings.Toggle("TakeOverOnInput", true, "Braking or steering hard yourself stops AutoDrive.");
            sHud = Settings.Toggle("ShowHud", true, "Show the AutoDrive status bar.");
            sDebug = Settings.Toggle("ShowPath", false, "Draw the planned path in the world.");

            // carscript.Update: ResetInput() -> [postCarResetInput] -> if (driving) GetPlayerInput() -> [postCarGetPlayerInput]
            // After the reset: mark a driverless car as driven so the game uses its controls, and write ours.
            GameEvents.On(GameEvents.PostCarResetInput, sender =>
            {
                var c = sender as carscript;
                if (!active || c == null || c != car) return;
                if (!c.isPlayerDriving && (!c.driving || !c.driving2)) { c.driving = true; c.driving2 = true; forcedSeat = true; }
                if (ComputeControls(c)) WriteControls(c);
            });
            // After the player's input was read (only when the player is in the driver's seat): overwrite it.
            GameEvents.On(GameEvents.PostCarGetPlayerInput, sender =>
            {
                var c = sender as carscript;
                if (active && c != null && c == car && ComputeControls(c)) WriteControls(c);
            });
        }

        // ------------------------------------------------------------------ register (optional)
        void Register()
        {
            if (!Game.IsReady) { Say("Load a save first"); return; }
            var c = Game.PlayerCar;
            if (c == null) { Say("Sit in the car you want to register first"); return; }
            if (active && car != c) Deactivate("");
            registered = c;
            Say("registered " + Cars.Model(c) + ". AutoDrive now drives this car wherever you are");
        }

        void Forget()
        {
            if (registered == null) { Say("no car is registered"); return; }
            if (active && remote) Deactivate("");
            registered = null;
            Say("registered car forgotten");
        }

        bool RegisteredAlive { get { return registered != null && registered.gameObject != null && registered.Engine != null; } }

        public override void OnSettingsChanged()
        {
            if (sOn.Value != active) SetOn(sOn.Value);
        }

        public override void OnGameExit() { Deactivate(""); registered = null; }

        // ------------------------------------------------------------------ on / off
        // keep the Manager's switch in step with what is really happening
        void SyncSwitch(bool on)
        {
            sOn.Value = on;
            try { Config.Set("AutoDriveOn", on ? "true" : "false"); } catch { }
        }

        bool SetOn(bool on)
        {
            if (!on) { Deactivate("AutoDrive off"); return false; }
            if (!Game.IsReady) { Say("Load a save first"); SyncSwitch(false); return false; }
            if (registered != null && !RegisteredAlive) { registered = null; Say("the registered car is gone"); }
            carscript c;
            if (registered != null) c = registered;
            else
            {
                c = Game.PlayerCar;
                if (c == null || !c.isPlayerDriving) { Say("Sit in the driver's seat (or register a car) to start AutoDrive"); SyncSwitch(false); return false; }
            }
            if (c.Engine == null) { Say("this car has no engine"); SyncSwitch(false); return false; }
            remote = registered != null;
            car = c;
            forcedSeat = false; releasedHandbrake = false; crankStart = -1f; crankTries = 0; crankPauseUntil = 0f; ctrlFrame = -1;
            recovering = false; stuckFor = 0f; recoverTimes.Clear(); remembered.Clear();
            // feel bumps: a small sensor on the car's rigidbody records real hits (not the road / ground)
            bump = null;
            if (c.RB != null)
            {
                bump = c.RB.gameObject.GetComponent<BumpSensor>() ?? c.RB.gameObject.AddComponent<BumpSensor>();
                bump.time = -10f;
                bump.isIgnored = IsIgnored;
            }
            MeasureCar();
            route.Reset();
            if (!route.Build(car, sJunction.Value)) { Say("No road found near the car"); car = null; SyncSwitch(false); return false; }
            SyncSwitch(true);
            savedForceAuto = car.forceAutomaticTransmission;
            car.forceAutomaticTransmission = true;   // the game's automatic gearbox does the shifting
            throttle = brake = speedInt = 0f; steer = car.isteer;
            active = true;
            Say(remote ? "AutoDrive on (driving the registered " + Cars.Model(car) + ")" : "AutoDrive on");
            Log.Info("started on " + Cars.Model(car) + (remote ? " (registered car)" : ""));
            return true;
        }

        void Deactivate(string why)
        {
            if (car != null)
            {
                try
                {
                    car.forceAutomaticTransmission = savedForceAuto;
                    if (forcedSeat && !car.isPlayerDriving)
                    {
                        // nobody in the driver's seat: give the seat back and park it so it doesn't roll away
                        car.driving = false;
                        car.driving2 = car.isPlayerDriving2;
                        car.BhandBrake = true;
                        car.AutoShiftNeutralOnGetOut();
                    }
                }
                catch { }
            }
            forcedSeat = false; remote = false;
            bool was = active;
            active = false; car = null;
            if (sOn != null && sOn.Value) SyncSwitch(false);
            if (pathLine != null) pathLine.enabled = false;
            if (was && why.Length > 0) Say(why);
        }

        void Say(string s) { status = s; statusTime = Time.time; ModLoader.Banner("AutoDrive: " + s, 3f); }

        void MeasureCar()
        {
            steerMax = car.steerAngle > 1f ? car.steerAngle : 30f;
            // wheelbase = distance between the steered axle and the other axle (in the car's frame)
            float steerZ = 0, otherZ = 0; int ns = 0, no = 0;
            foreach (var w in car.allWH != null && car.allWH.Count > 0 ? car.allWH : car.WHDriven)
            {
                if (w == null) continue;
                var z = car.transform.InverseTransformPoint(w.transform.position).z;
                if (car.WHSteer.Contains(w)) { steerZ += z; ns++; } else { otherZ += z; no++; }
            }
            wheelbase = ns > 0 && no > 0 ? Mathf.Clamp(Mathf.Abs(steerZ / ns - otherZ / no), 1.5f, 8f) : 2.5f;

            // the real body: every solid collider of the car (body, doors, bumpers, mirrors, mounted parts),
            // in the car's own frame. Not the player or loose things riding in it.
            float minX = -0.8f, maxX = 0.8f, minZ = -2f, maxZ = 2f; bool any = false;
            var pl = Game.Player;
            try
            {
                foreach (var col in car.transform.root.GetComponentsInChildren<Collider>())
                {
                    if (col == null || col.isTrigger || !col.enabled || col is WheelCollider) continue;
                    if (pl != null && col.transform.IsChildOf(pl.transform)) continue;
                    // the collider's OWN box (not the world-aligned one, which grows when the car is turned)
                    Bounds lb;
                    var box = col as BoxCollider;
                    var mc = col as MeshCollider;
                    if (box != null) lb = new Bounds(box.center, box.size);
                    else if (mc != null && mc.sharedMesh != null) lb = mc.sharedMesh.bounds;
                    else continue;
                    for (int i = 0; i < 8; i++)
                    {
                        var cl = new Vector3((i & 1) == 0 ? lb.min.x : lb.max.x, (i & 2) == 0 ? lb.min.y : lb.max.y, (i & 4) == 0 ? lb.min.z : lb.max.z);
                        var l = car.transform.InverseTransformPoint(col.transform.TransformPoint(cl));
                        if (!any) { minX = maxX = l.x; minZ = maxZ = l.z; any = true; }
                        minX = Mathf.Min(minX, l.x); maxX = Mathf.Max(maxX, l.x);
                        minZ = Mathf.Min(minZ, l.z); maxZ = Mathf.Max(maxZ, l.z);
                    }
                }
            }
            catch { }
            // sanity: a car body is at most ~0.45 m wider than its wheels on each side
            float wheelHalf = 0.7f;
            try { foreach (var wh in car.allWH) if (wh != null) wheelHalf = Mathf.Max(wheelHalf, Mathf.Abs(car.transform.InverseTransformPoint(wh.transform.position).x) + wh.radius * 0.3f); } catch { }
            bodyHalfWidth = Mathf.Clamp(Mathf.Max(-minX, maxX), wheelHalf, wheelHalf + 0.45f);
            bodyHalfLength = Mathf.Clamp((maxZ - minZ) * 0.5f, 1.5f, 10f);
            Log.Info("car body: " + (bodyHalfWidth * 2f).ToString("0.00") + " m wide, " + (bodyHalfLength * 2f).ToString("0.00") + " m long, wheelbase " + wheelbase.ToString("0.00") + " m");
        }

        // ------------------------------------------------------------------ every frame
        public override void OnUpdate()
        {
            if (!Game.IsReady) { if (active) Deactivate(""); return; }
            if (sKey.Down) SetOn(!active);
            if (!active) { if (pathLine != null) pathLine.enabled = false; return; }

            if (car == null || car.gameObject == null) { Deactivate("the car is gone"); return; }
            if (!remote && (!car.isPlayerDriving || Game.PlayerCar != car)) { Deactivate("you left the driver's seat"); return; }
            if (car.Engine == null) { Deactivate("this car has no engine"); return; }
            // registered car: the world (roads, ground) only exists near the player, so wait when too far away
            var pl = Game.Player;
            tooFar = remote && pl != null && (pl.transform.position - car.transform.position).magnitude > sMaxDistance.Value;

            var pos = car.transform.position;
            if (!route.Update(car, sJunction.Value, SideLimit + 8f)) { Deactivate("lost the road"); return; }

            if (Time.time >= nextScan)
            {
                nextScan = Time.time + 0.1f;
                if (sAvoid.Value) ScanObstacles(); else obstacles.Clear();
                PlanOffsets();
            }
            PlanSpeed();
            DrawDebug();
        }

        // ------------------------------------------------------------------ obstacles
        // s = centre along the route; sMin..sMax = the stretch of route where the car would touch it
        // soft = rough ground beside the road: avoided if possible, but never a reason to brake or stop
        struct Obstacle { public float s, sMin, sMax, dMin, dMax; public string name; public bool soft; }

        float SideLimit { get { return Mathf.Max(sAvoidWidth.Value, sDetour.Value) + Mathf.Abs(sLane.Value); } }

        void ScanObstacles()
        {
            obstacles.Clear();
            float halfCar = CarHalfWidth();
            float vNowScan = car.RB != null ? Mathf.Abs(Vector3.Dot(car.RB.velocity, car.transform.forward)) : 0f;
            float corridor = SideLimit + halfCar + 1.5f;
            float s0 = route.CarS;
            float halfLen = bodyHalfLength + 0.5f;     // the car touches it while its body passes
            float gap = 0.5f + 0.012f * vNowScan;      // sideways safety gap, ~0.85 m at 100 km/h
            var found = new HashSet<Collider>();
            for (float s = s0 + 1.5f; s < s0 + Samples * SampleStep && s < route.Length; s += 4f)
            {
                Vector3 p, t; route.At(s + 2f, out p, out t);
                var rot = Quaternion.LookRotation(t, Vector3.up);
                var right = Vector3.Cross(Vector3.up, t).normalized;
                // 1) anything in this 4 m slice of the corridor, from 0.2 m above the road to 2 m
                //    (small rocks and big ones; not the road, not wires high above it)
                var center = p + Vector3.up * 1.1f;
                found.Clear();
                int n = Physics.OverlapBoxNonAlloc(center, new Vector3(corridor, 0.9f, 2.1f), hits, rot, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    var col = hits[i];
                    if (col == null || IsIgnored(col) || col.bounds.max.y < p.y + 0.18f) continue;
                    found.Add(col);
                }
                if (found.Count == 0) continue;
                // 2) where across the road is it REALLY? Thin upright boxes every 35 cm across the
                //    corridor. The world-aligned bounding box of a slanted pole or power line can be
                //    many metres wide; this measures only the space the object actually takes.
                float occStart = 0f; bool inOcc = false; string what = null;
                const float step = 0.35f;
                for (float d = -corridor; d <= corridor + 0.001f; d += step)
                {
                    bool occ = false;
                    int m = Physics.OverlapBoxNonAlloc(center + right * d, new Vector3(step * 0.5f, 0.9f, 2.0f), hits, rot, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < m && !occ; i++) if (found.Contains(hits[i])) { occ = true; what = hits[i].name; }
                    if (occ && !inOcc) { occStart = d; inOcc = true; }
                    if ((!occ || d + step > corridor + 0.001f) && inOcc)
                    {
                        float occEnd = occ ? d : d - step;
                        inOcc = false;
                        obstacles.Add(new Obstacle { s = s + 2f, sMin = s - halfLen, sMax = s + 4f + halfLen,
                            dMin = occStart - step * 0.5f - halfCar - gap, dMax = occEnd + step * 0.5f + halfCar + gap, name = what });
                    }
                }
            }
            // things it hit earlier (and didn't see coming): a 1 m block at that spot for 40 s
            remembered.RemoveAll(m => Time.time - m.Value > 40f);
            foreach (var m in remembered)
            {
                float ms, md;
                route.Project(m.Key, s0 - 10f, s0 + Samples * SampleStep, out ms, out md);
                if (ms < s0 - 5f) continue;
                obstacles.Add(new Obstacle { s = ms, sMin = ms - 1f - halfLen, sMax = ms + 1f + halfLen,
                    dMin = md - 1f - halfCar - gap, dMax = md + 1f + halfCar + gap, name = "hit earlier" });
            }
            if (obstacles.Count > 0 && sDetour.Value > 0.1f) ScanGround(halfCar);
        }

        // Only when something is on the road: is the ground beside it drivable for a detour?
        // No ground, a big step up/down or a steep slope = blocked, so the detour plan avoids it.
        void ScanGround(float halfCar)
        {
            float lim = SideLimit, s0 = route.CarS;
            var found = new List<Obstacle>();
            for (float s = s0 + 2f; s < s0 + Samples * SampleStep && s < route.Length; s += 4f)
            {
                Vector3 p, t; route.At(s, out p, out t);
                var right = Vector3.Cross(Vector3.up, t).normalized;
                for (float d = RoadHalfWidth; d <= lim + halfCar; d += 1.5f)
                {
                    foreach (var side in new[] { -1f, 1f })
                    {
                        var probe = p + right * (d * side) + Vector3.up * 6f;
                        RaycastHit hit;
                        bool bad;
                        if (!Physics.Raycast(probe, Vector3.down, out hit, 14f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) bad = true;
                        else if (!IsIgnored(hit.collider)) continue;               // an obstacle: the box scan has it
                        else bad = Mathf.Abs(hit.point.y - p.y) > 1.3f || hit.normal.y < 0.82f;
                        if (bad) found.Add(new Obstacle { s = s, sMin = s - 2.5f, sMax = s + 2.5f, dMin = d * side - 0.8f - halfCar, dMax = d * side + 0.8f + halfCar, name = "rough ground", soft = true });
                    }
                }
            }
            obstacles.AddRange(found);
        }

        bool IsIgnored(Collider col)
        {
            if (col is TerrainCollider) return true;
            var t = col.transform;
            if (car != null && t.IsChildOf(car.transform.root)) return true;
            if (col.GetComponentInParent<roadscript>() != null) return true;
            // the player only doesn't count while riding in this car; on foot in front of it they are an obstacle
            var pl = Game.Player;
            if (pl != null && (t == pl.transform || t.IsChildOf(pl.transform)) && pl.transform.IsChildOf(car.transform.root)) return true;
            // loose small things (cans, parts, bottles): just drive over / push them
            var rb = col.attachedRigidbody;
            if (rb != null && !rb.isKinematic && rb.mass < 60f) return true;
            var size = col.bounds.size;
            if (size.x < 0.25f && size.z < 0.25f) return true;
            // giant colliders are terrain tiles / the ground, not things standing on the road
            if (size.x > 40f || size.z > 40f) return true;
            // the ground itself (terrain meshes, big flat colliders)
            if (size.y < 0.15f) return true;
            if (col.GetComponentInParent<terrainscript>() != null) return true;
            return false;
        }

        // half the real body width (mirrors included), never less than the wheels
        float CarHalfWidth()
        {
            float w = bodyHalfWidth;
            try
            {
                foreach (var wh in car.allWH) if (wh != null) w = Mathf.Max(w, Mathf.Abs(car.transform.InverseTransformPoint(wh.transform.position).x) + wh.radius * 0.5f);
            }
            catch { }
            return Mathf.Clamp(w, 0.7f, 1.6f);
        }

        // Lateral offset per sample. 1) where the car must be to clear each obstacle (over the obstacle's
        // whole length, plus a margin), 2) turned into a long, gentle S-curve, 3) rounded off with a wide
        // window so it starts and ends softly. The swerve is designed for the cruise speed (not the current
        // speed), so slowing down never makes it sharper. Only when a real object (not rough ground) can't
        // be cleared in time does tightAt ask for less speed.
        void PlanOffsets()
        {
            if (offsets.Length != Samples) offsets = new float[Samples];
            float lane = sLane.Value, lim = SideLimit;
            float s0 = route.CarS;
            blockedAt = -1f; tightAt = -1f;
            // design speed: at least the cruise speed, so the curve stays long and gentle
            float v = car != null && car.RB != null ? Mathf.Abs(Vector3.Dot(car.RB.velocity, car.transform.forward)) : 0f;
            float vDesign = Mathf.Max(v, Mathf.Min(sMaxSpeed.Value / 3.6f, 25f), 10f);
            // sideways per metre ~1.5 / speed: 100 km/h -> 6 m sideways over ~110 m (about 0.2 g)
            float step = Mathf.Clamp(1.5f / vDesign, 0.04f, 0.3f) * SampleStep;
            // rounding window: about 0.8 s of driving each way; the offset is held that much longer around obstacles
            int h = Mathf.Clamp(Mathf.RoundToInt(vDesign * 0.8f / SampleStep), 2, 12);
            float pad = h * SampleStep + 2f;
            var hardPer = new List<Vector2>[Samples];   // where the car would really touch an object
            float prev = route.CarD;
            for (int k = 0; k < Samples; k++)
            {
                float s = s0 + k * SampleStep;
                var all = new List<Vector2>();
                var hard = new List<Vector2>();
                var real = new List<Vector2>();
                foreach (var o in obstacles)
                {
                    if (s >= o.sMin - pad && s <= o.sMax + pad) { all.Add(new Vector2(o.dMin, o.dMax)); if (!o.soft) hard.Add(new Vector2(o.dMin, o.dMax)); }
                    if (!o.soft && s >= o.sMin && s <= o.sMax) real.Add(new Vector2(o.dMin, o.dMax));
                }
                hardPer[k] = real;
                float want = k == 0 ? prev : Mathf.Lerp(prev, lane, 0.15f);
                float best;
                // avoid rough ground too if there is room; if not, ignore it (it's only sand / a ditch edge)
                if (!Free(all, want, -lim, lim, out best) && !Free(hard, want, -lim, lim, out best))
                {
                    if (blockedAt < 0f) blockedAt = s;
                    best = prev;
                }
                offsets[k] = best;
                prev = best;
            }
            // gentlest curve first; only if it can't clear a real object in time, try steeper ones
            var raw = (float[])offsets.Clone();
            float[] factors = { 1f, 1.6f, 2.5f, 4f };
            for (int f = 0; f < factors.Length; f++)
            {
                float st = Mathf.Min(step * factors[f], 0.6f);
                int hh = Mathf.Max(2, Mathf.RoundToInt(h / factors[f]));
                float hit = ShapeOffsets(raw, st, hh, hardPer, s0);
                if (hit < 0f) { tightAt = -1f; return; }
                tightAt = hit;   // still tight with the steepest one: PlanSpeed slows down for it
            }
        }

        // Turns the raw "must be here" offsets into a smooth path. Returns the first route distance where
        // the car would still touch a real object, or -1 if it clears everything.
        float ShapeOffsets(float[] raw, float step, int h, List<Vector2>[] hardPer, float s0)
        {
            for (int k = 0; k < Samples; k++) offsets[k] = raw[k];
            // start swerving early enough for what is ahead...
            for (int k = Samples - 2; k >= 1; k--) offsets[k] = Mathf.Clamp(offsets[k], offsets[k + 1] - step, offsets[k + 1] + step);
            // ...but begin from where the car really is
            offsets[0] = route.CarD;
            for (int k = 1; k < Samples; k++) offsets[k] = Mathf.Clamp(offsets[k], offsets[k - 1] - step, offsets[k - 1] + step);
            // round the start and end of the swerve (moving average over +-h samples); the car's own spot stays fixed
            var tmp = (float[])offsets.Clone();
            for (int k = 1; k < Samples; k++)
            {
                float sum = 0f; int n = 0;
                for (int j = k - h; j <= k + h; j++) if (j >= 0 && j < Samples) { sum += tmp[j]; n++; }
                offsets[k] = sum / n;
            }
            // would it still touch a real object? Only look further ahead than the car's own length
            // (whatever is beside the car right now, it is already passing).
            for (int k = 1; k < Samples; k++)
            {
                if (k * SampleStep < bodyHalfLength + 3f) continue;
                foreach (var b in hardPer[k]) if (offsets[k] > b.x + 0.15f && offsets[k] < b.y - 0.15f) return s0 + k * SampleStep;
            }
            return -1f;
        }

        static bool Free(List<Vector2> blocked, float want, float lo, float hi, out float result)
        {
            result = Mathf.Clamp(want, lo, hi);
            if (blocked.Count == 0) return true;
            // candidates: the wish, and just outside every blocked interval
            var cands = new List<float> { result };
            foreach (var b in blocked) { cands.Add(b.x - 0.05f); cands.Add(b.y + 0.05f); }
            float bestDist = float.MaxValue; bool found = false;
            foreach (var c in cands)
            {
                if (c < lo || c > hi) continue;
                bool inside = false;
                foreach (var b in blocked) if (c > b.x && c < b.y) { inside = true; break; }
                if (inside) continue;
                float d = Mathf.Abs(c - want);
                if (d < bestDist) { bestDist = d; result = c; found = true; }
            }
            return found;
        }

        float OffsetAt(float s)
        {
            if (offsets.Length == 0) return sLane.Value;
            float k = (s - route.CarS) / SampleStep;
            if (k <= 0) return offsets[0];
            int i = Mathf.Min((int)k, offsets.Length - 2);
            if (i >= offsets.Length - 1) return offsets[offsets.Length - 1];
            return Mathf.Lerp(offsets[i], offsets[i + 1], k - i);
        }

        Vector3 PathPoint(float s)
        {
            Vector3 p, t; route.At(s, out p, out t);
            var right = Vector3.Cross(Vector3.up, t).normalized;
            return p + right * OffsetAt(s);
        }

        // ------------------------------------------------------------------ speed planning
        void PlanSpeed()
        {
            float vmax = sMaxSpeed.Value / 3.6f;
            float grip = sGrip.Value, decel = sBrake.Value;
            float s0 = route.CarS;
            float best = vmax;
            float horizon = Mathf.Min(route.Length - s0, Samples * SampleStep);
            float vNow = car.RB != null ? Mathf.Abs(Vector3.Dot(car.RB.velocity, car.transform.forward)) : 0f;
            float window = Mathf.Clamp(0.6f * Mathf.Max(vNow, vmax * 0.5f), 6f, 25f);
            float offroadMax = sOffroadSpeed.Value / 3.6f;
            string why = "";
            for (float d = 2f; d < horizon - 2f; d += 2f)
            {
                float k = Curvature(s0 + d, window);
                float vk = Mathf.Sqrt(grip / Mathf.Max(k, 1e-4f));
                string r = "corner";
                if (vk > vmax) { vk = vmax; r = ""; }
                if (Mathf.Abs(OffsetAt(s0 + d)) > RoadHalfWidth && offroadMax < vk) { vk = offroadMax; r = "off-road detour"; }   // on sand / grass
                float reach = Mathf.Sqrt(vk * vk + 2f * decel * Mathf.Max(0f, d - 3f));
                if (reach < best) { best = reach; why = r; }
            }
            // end of known road: stop at it unless more road shows up
            float toEnd = route.Length - s0;
            if (!route.EndIsOpen) Limit(ref best, ref why, Mathf.Sqrt(2f * decel * Mathf.Max(0f, toEnd - 6f)), "road ends");
            // full blockage: stop 5 m before it
            if (blockedAt >= 0f) Limit(ref best, ref why, Mathf.Sqrt(2f * decel * Mathf.Max(0f, blockedAt - s0 - 5f)), "road blocked");
            // even the steepest swerve can't clear it at this speed: brake towards it
            if (tightAt >= 0f) Limit(ref best, ref why, Mathf.Max(5f, Mathf.Sqrt(2f * decel * Mathf.Max(0f, tightAt - s0 - 3f))), "obstacle seen late");
            // sliding sideways: back off
            var vel = car.RB != null ? car.RB.velocity : Vector3.zero;
            float vf = Vector3.Dot(vel, car.transform.forward), vl = Vector3.Dot(vel, car.transform.right);
            float slip = Mathf.Abs(Mathf.Atan2(vl, Mathf.Max(Mathf.Abs(vf), 1f))) * Mathf.Rad2Deg;
            if (Mathf.Abs(vf) > 8f && slip > 10f) Limit(ref best, ref why, Mathf.Abs(vf) * 0.9f, "sliding");
            if (tooFar) { best = 0f; why = "too far from you"; }
            targetSpeed = Mathf.Max(0f, best);
            slowReason = targetSpeed < vmax - 0.5f ? why : "";
        }

        static void Limit(ref float best, ref string why, float v, string reason)
        {
            if (v < best) { best = v; why = reason; }
        }

        // How sharp the path bends at s (1 / radius). Measured as the change of heading over a long
        // window (grows with speed), so the small kinks between the road's points don't count as corners.
        float Curvature(float s, float window)
        {
            var a = PathPoint(s - window); var b = PathPoint(s); var c = PathPoint(s + window);
            var h1 = b - a; var h2 = c - b; h1.y = 0f; h2.y = 0f;
            if (h1.sqrMagnitude < 0.01f || h2.sqrMagnitude < 0.01f) return 0f;
            float angle = Vector3.Angle(h1, h2) * Mathf.Deg2Rad;
            return angle / window;
        }

        // ------------------------------------------------------------------ the controls
        // Computes this frame's controls once (both game events call it). False = AutoDrive just stopped.
        bool ComputeControls(carscript c)
        {
            if (ctrlFrame == Time.frameCount) return active;
            ctrlFrame = Time.frameCount;
            // the player takes over (only from the driver's seat of this car)
            if (sTakeover.Value && c.isPlayerDriving && (inputscript.i.brake > 0.5f || Mathf.Abs(inputscript.i.steer) > 0.6f))
            {
                Deactivate("you took over");
                return false;
            }
            float dt = Mathf.Max(Time.deltaTime, 0.001f);
            var rb = c.RB;
            var vel = rb != null ? rb.velocity : Vector3.zero;
            float v = Vector3.Dot(vel, c.transform.forward);   // m/s, signed

            // --- hit something / stuck: back up, then try again around it
            if (recovering) return Recover(c, v, dt);
            bool wantsToGo = c.Engine.running && targetSpeed > 1.5f && !tooFar;
            if (wantsToGo && throttle > 0.25f && Mathf.Abs(v) < 0.4f) stuckFor += dt; else stuckFor = 0f;
            bool justHit = bump != null && Time.time - bump.time < 0.6f && Mathf.Abs(v) < 1.5f && wantsToGo;
            if (stuckFor > 2.5f || justHit) { StartRecovery(c, justHit); return Recover(c, v, dt); }

            // --- steering: pure pursuit on the offset path
            // Look-ahead: long on straight road (smooth), shortened until the straight line from the car to
            // the aim point stays within 0.25 m of the planned path. A long aim point across an S-curve
            // would cut straight through the inside of it, i.e. into the rock the curve goes around.
            float ld = Mathf.Clamp((3.5f + 0.85f * Mathf.Abs(v)) * sLookahead.Value, 5f, 30f);
            var here = c.transform.position;
            Vector3 target = PathPoint(route.CarS + ld);
            // never aim closer than ~0.6 s ahead: a short aim point at speed makes the car weave
            float ldMin = Mathf.Clamp(0.6f * Mathf.Abs(v), 5f, ld);
            for (int tries = 0; tries < 10 && ld * 0.8f >= ldMin; tries++)
            {
                target = PathPoint(route.CarS + ld);
                float worst = 0f;
                for (int q = 1; q <= 4; q++)
                {
                    var onPath = PathPoint(route.CarS + ld * q / 5f);
                    worst = Mathf.Max(worst, FlatDistanceToSegment(onPath, here, target));
                }
                if (worst <= 0.25f) break;
                ld *= 0.8f;
            }
            var local = c.transform.InverseTransformPoint(target);
            float alpha = Mathf.Atan2(local.x, Mathf.Max(local.z, 0.5f));
            float dist = Mathf.Max(new Vector2(local.x, local.z).magnitude, 1f);
            float delta = Mathf.Atan(2f * wheelbase * Mathf.Sin(alpha) / dist) * Mathf.Rad2Deg;
            float wantSteer = Mathf.Clamp(delta / steerMax, -1f, 1f);
            float rate = 2.2f * dt;
            steer = Mathf.Clamp(wantSteer, steer - rate, steer + rate);

            // --- speed: PI throttle, proportional brake, never both
            float err = targetSpeed - v;
            float wantThrottle = 0f, wantBrake = 0f;
            if (err > 0f)
            {
                speedInt = Mathf.Clamp(speedInt + err * dt * 0.08f, 0f, 0.5f);
                wantThrottle = Mathf.Clamp(0.18f * err + speedInt + 0.08f, 0f, sAccel.Value);
            }
            else
            {
                speedInt = Mathf.Max(0f, speedInt - dt * 0.3f);
                if (err < -0.6f) wantBrake = Mathf.Clamp(-0.22f * err, 0f, 1f);
            }
            bool hold = targetSpeed < 0.3f && Mathf.Abs(v) < 0.6f;   // stand still
            if (hold) { wantThrottle = 0f; wantBrake = 0f; }

            // traction control: driven wheels much faster than the car -> less throttle
            float wheel = DrivenWheelSpeed(c);
            if (wantThrottle > 0f && wheel > Mathf.Abs(v) + 1.5f && wheel > Mathf.Abs(v) * 1.18f) wantThrottle *= 0.45f;
            // ABS: wheels locking while braking -> release for a moment
            if (wantBrake > 0f && Mathf.Abs(v) > 3f && wheel < Mathf.Abs(v) * 0.6f) lastAbs = Time.time;
            if (Time.time - lastAbs < 0.12f) wantBrake *= 0.25f;
            // steering hard at speed: ease off
            wantThrottle *= Mathf.Lerp(1f, 0.6f, Mathf.Abs(steer));

            throttle = Mathf.MoveTowards(throttle, wantThrottle, 1.6f * dt);
            brake = Mathf.MoveTowards(brake, wantBrake, 3.5f * dt);
            if (wantThrottle > 0f) brake = 0f;
            if (wantBrake > 0f) throttle = 0f;

            // Automatic gearbox: below walking pace the brake pedal selects reverse (carscript.AutoShift),
            // so the last bit of stopping and standing still is done with the handbrake instead.
            float hand = 0f;
            if (hold || (brake > 0f && Mathf.Abs(v) < 1.2f)) { hand = 1f; brake = 0f; throttle = 0f; }

            // engine off: crank it (a few tries), standing on the handbrake meanwhile
            bool ign = false;
            if (!c.Engine.running)
            {
                throttle = 0f; brake = 0f; hand = 1f;
                if (!sAutoStart.Value) { if (Time.time - statusTime > 3f) Say("the engine is off: start it, or turn on AutoStartEngine"); }
                else if (Time.time >= crankPauseUntil)
                {
                    if (crankStart < 0f) { crankStart = Time.time; if (crankTries == 0) Say("starting the engine"); }
                    if (Time.time - crankStart < 3.5f) ign = true;               // hold the key on "start"
                    else
                    {
                        crankStart = -1f; crankPauseUntil = Time.time + 1.5f;
                        if (++crankTries >= 4) { Deactivate("the engine won't start (fuel, battery, ignition?)"); return false; }
                    }
                }
            }
            else { crankStart = -1f; crankTries = 0; }

            outSteer = steer; outThrottle = throttle; outBrake = brake; outHandbrake = hand; outIgnition = ign;
            return true;
        }

        void WriteControls(carscript c)
        {
            c.isteer = outSteer;
            c.ithrottle = outThrottle;
            c.ibrake = outBrake;
            c.ihandbrake = outHandbrake;
            c.ihandbrakeBDown = false;
            c.iignition = outIgnition;
            // pulled handbrake (the toggle kind): let it off when we want to move
            if (c.BhandBrake && outHandbrake < 0.5f && sAutoStart.Value)
            {
                c.BhandBrake = false;
                if (!releasedHandbrake) { releasedHandbrake = true; Say("released the handbrake"); }
            }
        }

        // ------------------------------------------------------------------ recovery (reverse out)
        // In the automatic gearbox, the brake pedal at a standstill means "reverse" (carscript.AutoShift),
        // so backing up is done with ibrake and stopping the reverse with ithrottle.
        void StartRecovery(carscript c, bool hit)
        {
            // too many in a short time: give up instead of ramming forever
            recoverTimes.RemoveAll(t => Time.time - t > 40f);
            recoverTimes.Add(Time.time);
            if (recoverTimes.Count > 4) { Deactivate("stuck: it hit something several times. Drive it out yourself"); return; }
            recovering = true;
            recoverStart = Time.time;
            recoverFrom = c.transform.position;
            stuckFor = 0f;
            // where was it hit? remember it as an obstacle for a while so the next try goes around it
            Vector3 p = hit && bump != null ? bump.point : c.transform.position + c.transform.forward * (bodyHalfLength + 0.5f);
            remembered.Add(new KeyValuePair<Vector3, float>(p, Time.time));
            float side = c.transform.InverseTransformPoint(p).x;
            // reversing: steering towards the hit side swings the nose away from it
            recoverSteer = Mathf.Abs(side) < 0.2f ? 0f : Mathf.Sign(side) * 0.7f;
            Say(hit ? "hit something, backing up" : "stuck, backing up");
        }

        bool Recover(carscript c, float v, float dt)
        {
            if (!active) return false;
            float t = Time.time - recoverStart;
            float moved = (c.transform.position - recoverFrom).magnitude;
            bool done = moved > 5f || t > 5f || (t > 2f && Mathf.Abs(v) < 0.2f && moved < 0.5f);
            if (done)
            {
                recovering = false;
                throttle = brake = speedInt = 0f;
                nextScan = 0f;   // re-plan right away, with the remembered obstacle
                Say("trying again");
            }
            steer = Mathf.MoveTowards(steer, done ? 0f : recoverSteer, 2.2f * dt);
            outSteer = steer;
            // brake pedal = reverse at a standstill; throttle = stop the reverse when done
            outThrottle = done && v < -0.5f ? 0.5f : 0f;
            outBrake = done ? 0f : 0.55f;
            outHandbrake = 0f;
            outIgnition = false;
            return true;
        }

        static float FlatDistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            p.y = a.y = b.y = 0f;
            var ab = b - a; float len2 = ab.sqrMagnitude;
            if (len2 < 1e-6f) return (p - a).magnitude;
            float f = Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2);
            return (p - (a + ab * f)).magnitude;
        }

        static float DrivenWheelSpeed(carscript c)
        {
            try
            {
                float sum = 0; int n = 0;
                foreach (var w in c.WHDriven) if (w != null) { sum += Mathf.Abs(w.rpm) * 2f * Mathf.PI * w.radius / 60f; n++; }
                return n > 0 ? sum / n : 0f;
            }
            catch { return 0f; }
        }

        // ------------------------------------------------------------------ HUD + debug path
        public override void OnGUI()
        {
            if (!sHud.Value || !Game.IsReady) return;
            if (!active && Time.time - statusTime > 3f) return;
            string text;
            if (active)
            {
                float kmh = car != null && car.RB != null ? Vector3.Dot(car.RB.velocity, car.transform.forward) * 3.6f : 0f;
                bool offroad = Mathf.Abs(OffsetAt(route.CarS + 8f)) > RoadHalfWidth || Mathf.Abs(route.CarD) > RoadHalfWidth;
                string obs = recovering ? "backing up, then trying again around it" :
                             tooFar ? "waiting: you are more than " + Mathf.RoundToInt(sMaxDistance.Value) + " m away" :
                             car != null && car.Engine != null && !car.Engine.running ? (crankStart >= 0f ? "starting the engine" : "engine off") :
                             blockedAt >= 0f ? "no way around in " + Mathf.RoundToInt(blockedAt - route.CarS) + " m, stopping" :
                             tightAt >= 0f ? "slowing to get around" :
                             offroad ? "going around off the road" :
                             obstacles.Exists(o => !o.soft) ? "avoiding obstacles" : "road clear";
                string who = remote && car != null ? Cars.Model(car) + " " : "";
                string slow = slowReason.Length > 0 ? "   ·   slower because: " + slowReason : "";
                text = "AUTODRIVE " + who + "  " + Mathf.RoundToInt(kmh) + " / " + Mathf.RoundToInt(targetSpeed * 3.6f) + " km/h   ·   " + obs + slow + "   ·   " + sKey.Value + " = stop";
            }
            else text = "AutoDrive: " + status;
            var style = new GUIStyle(GUI.skin.box) { fontSize = 14, alignment = TextAnchor.MiddleCenter };
            style.normal.textColor = active ? new Color(1f, 0.72f, 0.4f) : Color.white;
            GUI.Box(new Rect(Screen.width / 2f - 420, Screen.height - 70, 840, 30), text, style);
        }

        void DrawDebug()
        {
            if (!sDebug.Value) { if (pathLine != null) pathLine.enabled = false; return; }
            if (pathLine == null)
            {
                var go = new GameObject("AutoDrivePath");
                UnityEngine.Object.DontDestroyOnLoad(go);
                pathLine = go.AddComponent<LineRenderer>();
                pathLine.material = new Material(Shader.Find("Hidden/Internal-Colored"));
                pathLine.startColor = pathLine.endColor = new Color(1f, 0.55f, 0.2f, 0.9f);
                pathLine.startWidth = pathLine.endWidth = 0.15f;
            }
            pathLine.enabled = true;
            int n = 40;
            pathLine.positionCount = n;
            for (int i = 0; i < n; i++) pathLine.SetPosition(i, PathPoint(route.CarS + i * 2.5f) + Vector3.up * 0.3f);
        }
    }

    // ====================================================================== bump sensor
    /// <summary>Sits on the car's rigidbody and records the last real hit (sideways or head-on, not the road).</summary>
    public class BumpSensor : MonoBehaviour
    {
        public float time = -10f;
        public Vector3 point;
        public Func<Collider, bool> isIgnored;

        void OnCollisionEnter(Collision c)
        {
            try
            {
                if (c.relativeVelocity.magnitude < 1.2f || c.contactCount == 0) return;
                var ct = c.GetContact(0);
                if (Mathf.Abs(ct.normal.y) > 0.6f) return;                 // ground / road surface under the wheels
                if (isIgnored != null && isIgnored(c.collider)) return;   // road, terrain, small loose things
                time = Time.time;
                point = ct.point;
            }
            catch { }
        }
    }

    // ====================================================================== the road route
    /// <summary>A polyline (in global coordinates) along the roads ahead of the car.</summary>
    class Route
    {
        readonly List<Vector3d> pts = new List<Vector3d>();
        readonly List<float> cum = new List<float>();     // distance along the route at each point
        roadGenScript.roadClass lastRoad;
        bool lastForward;
        roadGenScript rg;
        readonly System.Random rnd = new System.Random();
        public bool EndIsOpen;     // false = known dead end
        public float CarS, CarD;   // car position along the route and sideways (+right)
        public float Length { get { return cum.Count > 0 ? cum[cum.Count - 1] : 0f; } }

        public void Reset() { pts.Clear(); cum.Clear(); lastRoad = null; CarS = CarD = 0f; EndIsOpen = true; }

        roadGenScript Gen()
        {
            if (rg == null) rg = UnityEngine.Object.FindObjectOfType<roadGenScript>();
            return rg;
        }

        // all generated roads in the 5x5 chunks around a point
        List<roadGenScript.roadClass> NearRoads(Vector3d g)
        {
            var list = new List<roadGenScript.roadClass>();
            var gen = Gen(); if (gen == null) return list;
            for (int cx = -2; cx <= 2; cx++)
                for (int cy = -2; cy <= 2; cy++)
                {
                    roadGenScript.dotChunkClass ch;
                    if (!gen.chunks.TryGetValue(mainscript.GetChunkPos(g, cx, cy, gen.chunkSize), out ch) || ch == null) continue;
                    foreach (var r in ch.roads) if (r != null && r.generated && r.roadElements.Count > 0 && !list.Contains(r)) list.Add(r);
                }
            return list;
        }

        static List<Vector3d> Points(roadGenScript.roadClass r)
        {
            var l = new List<Vector3d>();
            foreach (var e in r.roadElements)
                if (e != null && e.bonePos != null)
                    foreach (var p in e.bonePos)
                        if (l.Count == 0 || (p - l[l.Count - 1]).sqrMagnitude > 0.09) l.Add(p);
            return l;
        }

        bool IsMain(roadGenScript.roadClass r)
        {
            var gen = Gen(); roadGenScript.dotChunkClass ch;
            return gen != null && gen.chunks.TryGetValue(r.selfChunk, out ch) && ch.mainRoad.Contains(r.selfID);
        }

        /// <summary>Start: nearest road to the car, in the direction the car faces.</summary>
        public bool Build(carscript car, string junction)
        {
            Reset();
            var g = mainscript.GlobalFromUnityPos(car.transform.position);
            roadGenScript.roadClass bestRoad = null; int bestI = -1; double bestD = 35 * 35;
            List<Vector3d> bestPts = null;
            foreach (var r in NearRoads(g))
            {
                var p = Points(r);
                for (int i = 0; i < p.Count; i++)
                {
                    var dx = p[i].x - g.x; var dz = p[i].z - g.z;
                    var d = dx * dx + dz * dz;
                    if (d < bestD) { bestD = d; bestRoad = r; bestI = i; bestPts = p; }
                }
            }
            if (bestRoad == null || bestPts.Count < 2) return false;
            // which way along the road matches the car's heading?
            int j = Mathf.Min(bestI + 1, bestPts.Count - 1), h = Mathf.Max(bestI - 1, 0);
            var tan = (Vector3)(bestPts[j] - bestPts[h]); tan.y = 0;
            var fwd = car.transform.forward; fwd.y = 0;
            bool forward = Vector3.Dot(tan, fwd) >= 0f;
            int from = Mathf.Max(0, forward ? bestI - 3 : bestI + 3);
            AppendRoad(bestRoad, bestPts, forward, Mathf.Min(from, bestPts.Count - 1));
            Extend(junction);
            return pts.Count >= 2;
        }

        void AppendRoad(roadGenScript.roadClass r, List<Vector3d> p, bool forward, int startIndex)
        {
            if (forward) for (int i = startIndex; i < p.Count; i++) Add(p[i]);
            else for (int i = startIndex; i >= 0; i--) Add(p[i]);
            lastRoad = r; lastForward = forward;
        }

        void Add(Vector3d p)
        {
            if (pts.Count > 0 && (p - pts[pts.Count - 1]).sqrMagnitude < 0.09) return;
            cum.Add(pts.Count == 0 ? 0f : cum[cum.Count - 1] + (float)(p - pts[pts.Count - 1]).magnitude);
            pts.Add(p);
        }

        /// <summary>Keep ~250 m of road ahead: continue onto the next road at the end.</summary>
        void Extend(string junction)
        {
            int guard = 0;
            while (Length - CarS < 250f && guard++ < 6)
            {
                var end = pts[pts.Count - 1];
                var prevDir = (Vector3)(end - pts[Mathf.Max(0, pts.Count - 4)]); prevDir.y = 0; prevDir.Normalize();
                roadGenScript.roadClass pick = null; List<Vector3d> pickPts = null; bool pickFwd = true; float pickScore = float.MaxValue;
                foreach (var r in NearRoads(end))
                {
                    if (r == lastRoad) continue;
                    var p = Points(r);
                    if (p.Count < 2) continue;
                    bool fromStart = (p[0] - end).sqrMagnitude < 10 * 10;
                    bool fromEnd = (p[p.Count - 1] - end).sqrMagnitude < 10 * 10;
                    if (!fromStart && !fromEnd) continue;
                    bool fwd = fromStart && (!fromEnd || (p[0] - end).sqrMagnitude <= (p[p.Count - 1] - end).sqrMagnitude);
                    var a = fwd ? p[0] : p[p.Count - 1]; var b = fwd ? p[Mathf.Min(3, p.Count - 1)] : p[Mathf.Max(0, p.Count - 4)];
                    var dir = (Vector3)(b - a); dir.y = 0; dir.Normalize();
                    float turn = Vector3.Angle(prevDir, dir);
                    if (turn > 120f) continue;                                  // that's going back
                    float score = junction == "Random" ? (float)rnd.NextDouble() * 100f
                                : turn + (junction == "Main road" && IsMain(r) ? -1000f : 0f);
                    if (score < pickScore) { pickScore = score; pick = r; pickPts = p; pickFwd = fwd; }
                }
                if (pick == null) { EndIsOpen = NearRoads(end).Count == 0 || !AllGenerated(end); return; }
                AppendRoad(pick, pickPts, pickFwd, pickFwd ? 0 : pickPts.Count - 1);
            }
            EndIsOpen = true;
        }

        bool AllGenerated(Vector3d g)
        {
            var gen = Gen(); if (gen == null) return false;
            roadGenScript.dotChunkClass ch;
            return gen.chunks.TryGetValue(mainscript.GetChunkPos(g, 0, 0, gen.chunkSize), out ch) && ch.Done();
        }

        /// <summary>Track the car, drop road behind it, grow road ahead. False = lost the road.</summary>
        public bool Update(carscript car, string junction, float lostDistance)
        {
            if (pts.Count < 2) return false;
            float s, d;
            Project(car.transform.position, CarS - 10f, CarS + 40f, out s, out d);
            CarS = s; CarD = d;
            if (Mathf.Abs(d) > Mathf.Max(18f, lostDistance)) return Build(car, junction);   // far off: start over from the nearest road
            // forget what's well behind
            int drop = 0;
            while (drop < cum.Count - 2 && cum[drop + 1] < CarS - 25f) drop++;
            if (drop > 0)
            {
                float off = cum[drop];
                pts.RemoveRange(0, drop); cum.RemoveRange(0, drop);
                for (int i = 0; i < cum.Count; i++) cum[i] -= off;
                CarS -= off;
            }
            if (Length - CarS < 250f) Extend(junction);
            return true;
        }

        /// <summary>Point and direction at distance s along the route (Unity coordinates).</summary>
        public void At(float s, out Vector3 p, out Vector3 t)
        {
            int n = pts.Count;
            if (n < 2) { p = Vector3.zero; t = Vector3.forward; return; }
            if (s <= 0f) { p = U(0); t = (U(1) - U(0)).normalized; return; }
            if (s >= cum[n - 1]) { t = (U(n - 1) - U(n - 2)).normalized; p = U(n - 1) + t * (s - cum[n - 1]); return; }
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (cum[mid] <= s) lo = mid; else hi = mid; }
            float f = (s - cum[lo]) / Mathf.Max(cum[hi] - cum[lo], 1e-4f);
            var a = U(lo); var b = U(hi);
            p = Vector3.Lerp(a, b, f);
            t = b - a; t.y = 0f; t = t.sqrMagnitude > 1e-6f ? t.normalized : Vector3.forward;
        }

        Vector3 U(int i) { return mainscript.UnityPosFromGlobal(pts[i]); }

        /// <summary>Closest route position to a world point, searched between sFrom and sTo.</summary>
        public void Project(Vector3 world, float sFrom, float sTo, out float sOut, out float dOut)
        {
            sOut = CarS; dOut = 0f;
            int n = pts.Count; if (n < 2) return;
            float best = float.MaxValue;
            for (int i = 0; i < n - 1; i++)
            {
                if (cum[i + 1] < sFrom || cum[i] > sTo) continue;
                var a = U(i); var b = U(i + 1);
                var ab = b - a; ab.y = 0; var aw = world - a; aw.y = 0;
                float len2 = ab.sqrMagnitude; if (len2 < 1e-6f) continue;
                float f = Mathf.Clamp01(Vector3.Dot(aw, ab) / len2);
                var closest = a + ab * f; closest.y = world.y;
                float dist = (world - new Vector3(closest.x, world.y, closest.z)).sqrMagnitude;
                if (dist < best)
                {
                    best = dist;
                    sOut = cum[i] + f * (cum[i + 1] - cum[i]);
                    var right = Vector3.Cross(Vector3.up, ab.normalized);
                    dOut = Vector3.Dot(world - closest, right);
                }
            }
        }
    }
}
