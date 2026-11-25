// DashboardHUD - a customizable dashboard overlay while you drive.
//
// Shows speed, RPM, gear, fuel, engine temperature, odometer and trip distance in one of several
// styles (Box, Analog, Race, Retro LCD, Neon, Minimal, Bars). Everything is set in the Manager (F10):
// style, units, position, size, opacity, color and which readouts are shown. F7 hides/shows it.
//
// Game data used (carscript): speed (km/h, from the rigidbody), gear (-1 reverse, 0 neutral, 1.. forward;
// gears[gear + 1].ratio < 0 = reverse, == 0 = neutral), distance (km driven since the car was loaded),
// Engine.rpm / maxRpm / temp / minOptimalTemp2 (above it power drops), Tank.F (fuel).
// The game does not save car.distance, so this mod keeps its own odometer per car in the save.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UntitledLoader;

namespace DashboardHUD
{
    public class DashboardHUDMod : Mod
    {
        public override string ID { get { return "dashboardhud"; } }
        public override string Name { get { return "Dashboard HUD"; } }
        public override string Version { get { return "1.0.2"; } }
        public override string MinLoaderVersion { get { return "1.1.0"; } }
        public override string Author { get { return "UntitledTLD"; } }

        static readonly string[] Styles = { "Box", "Analog", "Race", "Retro LCD", "Neon", "Minimal", "Bars" };
        static readonly string[] Positions = { "Bottom right", "Bottom left", "Bottom center", "Top right", "Top left", "Top center" };
        static readonly string[] Colors = { "Orange", "Cyan", "Green", "Red", "Purple", "Pink", "Yellow", "White" };

        ChoiceSetting style, units, position, color, dialMax;
        FloatSetting scale, opacity, shiftAt;
        IntSetting offsetX, offsetY;
        BoolSetting showSpeed, showRpm, showGear, showFuel, showTemp, showOdo, showTrip, shiftLight, background, onFoot;
        KeySetting toggleKey;

        bool visible = true;

        // ---- live values (smoothed for the needles)
        carscript car;
        float speedKmh, dSpeed, rpm, dRpm, maxRpm = 6000f, fuel, fuelMax, temp, hotTemp = 100f, odoKm, tripKm;
        string gear = "N";
        bool running, hasCar;
        float autoDial;

        // ---- odometer per car (idInSave -> km), stored in the save
        readonly Dictionary<string, float> odoBase = new Dictionary<string, float>();
        readonly Dictionary<carscript, float> odoStart = new Dictionary<carscript, float>();
        float lastCarDistance = -1f;
        carscript lastCar;

        public override void OnLoad()
        {
            style = Settings.Choice("Style", "Analog", Styles, "How the dashboard looks.");
            color = Settings.Choice("Color", "Orange", Colors, "Accent color.");
            units = Settings.Choice("Units", "km/h", new[] { "km/h", "mph" }, "Speed and distance units.");
            position = Settings.Choice("Position", "Bottom right", Positions, "Where on the screen it sits.");
            offsetX = Settings.Int("OffsetX", 0, -1000, 1000, "Move it left (-) or right (+), in pixels at 1080p.");
            offsetY = Settings.Int("OffsetY", 0, -1000, 1000, "Move it up (-) or down (+), in pixels at 1080p.");
            scale = Settings.Slider("Size", 1f, 0.5f, 2f, "Size of the dashboard.");
            opacity = Settings.Slider("Opacity", 0.95f, 0.2f, 1f, "How see-through it is.");
            background = Settings.Toggle("Background", true, "Dark panel behind the readouts.");
            showSpeed = Settings.Toggle("ShowSpeed", true, "Speed.");
            showRpm = Settings.Toggle("ShowRPM", true, "Engine RPM.");
            showGear = Settings.Toggle("ShowGear", true, "Current gear (R / N / 1-6).");
            showFuel = Settings.Toggle("ShowFuel", true, "Fuel in the tank.");
            showTemp = Settings.Toggle("ShowTemp", true, "Engine temperature (turns red when it's too hot).");
            showOdo = Settings.Toggle("ShowOdometer", true, "Total distance this car has driven.");
            showTrip = Settings.Toggle("ShowTrip", true, "Distance driven since you got in / reset the trip.");
            shiftLight = Settings.Toggle("ShiftLight", true, "Flash when it's time to shift up.");
            shiftAt = Settings.Slider("ShiftAt", 0.88f, 0.6f, 0.98f, "Shift light comes on at this part of max RPM.");
            dialMax = Settings.Choice("DialMax", "Auto", new[] { "Auto", "100", "120", "160", "200", "240" }, "Top of the analog speed dial.");
            onFoot = Settings.Toggle("ShowOnFoot", false, "Keep showing the last car's dashboard after you get out.");
            toggleKey = Settings.Key("ToggleKey", KeyCode.F7, "Key that hides / shows the dashboard.");
            Settings.Button("Reset trip", "Sets the trip counter back to 0", () => { tripKm = 0f; });
        }

        // ------------------------------------------------------------------ odometer save / load
        public override void OnGameReady()
        {
            odoBase.Clear(); odoStart.Clear(); lastCar = null; lastCarDistance = -1f; tripKm = 0f;
            string s = SaveData.GetString("odometers", "");
            foreach (var part in s.Split(';'))
            {
                int c = part.IndexOf(':');
                float km;
                if (c > 0 && float.TryParse(part.Substring(c + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out km))
                    odoBase[part.Substring(0, c)] = km;
            }
        }

        public override void OnSave()
        {
            var parts = new List<string>();
            foreach (var c in Game.Cars)
            {
                if (c == null) continue;
                string id = CarKey(c);
                if (id == null) continue;
                odoBase[id] = Odometer(c);
                odoStart[c] = c.distance;
            }
            foreach (var kv in odoBase) parts.Add(kv.Key + ":" + kv.Value.ToString("0.###", CultureInfo.InvariantCulture));
            SaveData.Set("odometers", string.Join(";", parts.ToArray()));
        }

        static string CarKey(carscript c)
        {
            try { return c.tosave != null ? c.tosave.idInSave.ToString() : null; } catch { return null; }
        }

        float Odometer(carscript c)
        {
            float start;
            if (!odoStart.TryGetValue(c, out start)) { start = c.distance; odoStart[c] = start; }
            float b = 0f;
            string id = CarKey(c);
            if (id != null) odoBase.TryGetValue(id, out b);
            return b + Mathf.Max(0f, c.distance - start);
        }

        // ------------------------------------------------------------------ reading the car
        public override void OnUpdate()
        {
            if (toggleKey.Down) { visible = !visible; ModLoader.Banner(visible ? "Dashboard on" : "Dashboard off (" + toggleKey.Value + ")", 1.5f); }
            if (!Game.IsReady) { hasCar = false; return; }

            var c = Game.PlayerCar;
            if (c != null) car = c;
            else if (!onFoot.Value) car = null;
            if (car == null) { hasCar = false; lastCar = null; return; }
            hasCar = true;

            // trip: distance driven while you are in this car
            if (c != null)
            {
                if (lastCar == c && lastCarDistance >= 0f) tripKm += Mathf.Max(0f, c.distance - lastCarDistance);
                lastCar = c; lastCarDistance = c.distance;
            }
            else lastCar = null;

            speedKmh = car.speed;
            var e = car.Engine;
            running = e != null && e.running;
            rpm = e != null ? Mathf.Max(0f, e.rpm) : 0f;
            if (e != null && e.maxRpm > 500f) maxRpm = e.maxRpm;
            temp = e != null ? e.temp : 0f;
            if (e != null && e.minOptimalTemp2 > 10f) hotTemp = e.minOptimalTemp2;
            fuel = 0f; fuelMax = 0f;
            try
            {
                if (car.Tank != null && car.Tank.F != null) { fuel = car.Tank.F.GetAmount(); fuelMax = car.Tank.F.maxC; }
            }
            catch { }
            gear = GearName(car);
            odoKm = Odometer(car);

            float k = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            dSpeed = Mathf.Lerp(dSpeed, Speed, k);
            dRpm = Mathf.Lerp(dRpm, rpm, k);
            float top = Mph ? 100f : 160f;
            autoDial = Mathf.Max(autoDial, top);
            while (Speed > autoDial * 0.97f) autoDial += Mph ? 20f : 40f;
        }

        static string GearName(carscript c)
        {
            try
            {
                if (c.gears != null && c.gear + 1 >= 0 && c.gear + 1 < c.gears.Length)
                {
                    float r = c.gears[c.gear + 1].ratio;
                    if (r < 0f) return "R";
                    if (r == 0f) return "N";
                }
                if (c.gear < 0) return "R";
                if (c.gear == 0) return "N";
                return c.gear.ToString();
            }
            catch { return "-"; }
        }

        bool Mph { get { return units.Value == "mph"; } }
        float Speed { get { return Mph ? speedKmh * 0.621371f : speedKmh; } }
        float Dist(float km) { return Mph ? km * 0.621371f : km; }
        string SpeedUnit { get { return Mph ? "MPH" : "KM/H"; } }
        string DistUnit { get { return Mph ? "mi" : "km"; } }
        float RpmN { get { return Mathf.Clamp01(dRpm / Mathf.Max(1f, maxRpm)); } }
        float FuelN { get { return fuelMax > 0f ? Mathf.Clamp01(fuel / fuelMax) : 0f; } }
        float TempN { get { return Mathf.Clamp01(temp / (hotTemp * 1.25f)); } }
        bool Hot { get { return temp >= hotTemp; } }
        bool LowFuel { get { return fuelMax > 0f && FuelN < 0.12f; } }
        bool ShiftNow { get { return shiftLight.Value && running && dRpm >= maxRpm * shiftAt.Value; } }
        float DialMax { get { float v; return float.TryParse(dialMax.Value, out v) ? v : autoDial; } }
        static bool Blink { get { return Mathf.Repeat(Time.unscaledTime, 0.3f) < 0.15f; } }

        // ------------------------------------------------------------------ drawing helpers
        Texture2D white, disc, panel;
        GUIStyle panelStyle, textStyle;
        float S, ox, oy;      // scale and top-left of the dashboard on screen
        float alpha;

        void InitGfx()
        {
            if (white != null) return;
            white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply();
            disc = MakeDisc(256);
            panel = MakeRounded(64, 18);
            panelStyle = new GUIStyle(); panelStyle.normal.background = panel; panelStyle.border = new RectOffset(20, 20, 20, 20);
            textStyle = new GUIStyle(); textStyle.fontStyle = FontStyle.Bold; textStyle.normal.textColor = Color.white; textStyle.clipping = TextClipping.Overflow;
        }

        static Texture2D MakeDisc(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.ARGB32, false); t.wrapMode = TextureWrapMode.Clamp;
            float r = n / 2f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                    t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(r - d)));
                }
            t.Apply();
            return t;
        }

        static Texture2D MakeRounded(int n, int rad)
        {
            var t = new Texture2D(n, n, TextureFormat.ARGB32, false); t.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, rad, n - rad), cy = Mathf.Clamp(y + 0.5f, rad, n - rad);
                    float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(rad - d)));
                }
            t.Apply();
            return t;
        }

        Color Accent
        {
            get
            {
                switch (color.Value)
                {
                    case "Cyan": return new Color(0.2f, 0.9f, 1f);
                    case "Green": return new Color(0.35f, 1f, 0.45f);
                    case "Red": return new Color(1f, 0.25f, 0.25f);
                    case "Purple": return new Color(0.7f, 0.45f, 1f);
                    case "Pink": return new Color(1f, 0.4f, 0.8f);
                    case "Yellow": return new Color(1f, 0.9f, 0.25f);
                    case "White": return new Color(0.95f, 0.95f, 0.95f);
                    default: return new Color(1f, 0.6f, 0.15f);
                }
            }
        }

        static readonly Color Red = new Color(1f, 0.22f, 0.2f);
        static readonly Color Dim = new Color(1f, 1f, 1f, 0.45f);
        static readonly Color Bg = new Color(0.05f, 0.06f, 0.08f, 0.78f);

        Rect R(float x, float y, float w, float h) { return new Rect(ox + x * S, oy + y * S, w * S, h * S); }
        Vector2 P(float x, float y) { return new Vector2(ox + x * S, oy + y * S); }
        Color A(Color c) { c.a *= alpha; return c; }

        void Fill(float x, float y, float w, float h, Color c) { GUI.color = A(c); GUI.DrawTexture(R(x, y, w, h), white); }
        void Panel(float x, float y, float w, float h, Color c) { GUI.color = A(c); panelStyle.Draw(R(x, y, w, h), false, false, false, false); }
        void Disc(float cx, float cy, float r, Color c) { GUI.color = A(c); GUI.DrawTexture(R(cx - r, cy - r, r * 2, r * 2), disc); }

        // a rotated thin rect from the pivot (cx,cy), angle in degrees (0 = up, clockwise), from radius r0 to r1
        void Ray(float cx, float cy, float angle, float r0, float r1, float width, Color c)
        {
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, P(cx, cy));
            GUI.color = A(c);
            GUI.DrawTexture(R(cx - width / 2f, cy - r1, width, r1 - r0), white);
            GUI.matrix = m;
        }

        void Arc(float cx, float cy, float r, float thick, float a0, float a1, Color c)
        {
            float span = Mathf.Abs(a1 - a0);
            int n = Mathf.Max(1, Mathf.CeilToInt(span / 2.2f));
            float segW = 2f * Mathf.PI * r * (span / 360f) / n + 1.2f;
            for (int i = 0; i < n; i++) Ray(cx, cy, Mathf.Lerp(a0, a1, (i + 0.5f) / n), r - thick, r, segW, c);
        }

        void Text(string s, float x, float y, float w, float h, int size, Color c, TextAnchor anchor, bool shadow)
        {
            textStyle.fontSize = Mathf.Max(6, Mathf.RoundToInt(size * S));
            textStyle.alignment = anchor;
            GUI.color = Color.white;   // GUI.color tints text too: reset it, or the last shape's color bleeds in
            if (shadow)
            {
                textStyle.normal.textColor = A(new Color(0, 0, 0, 0.7f));
                GUI.Label(R(x + 1.5f, y + 1.5f, w, h), s, textStyle);
            }
            textStyle.normal.textColor = A(c);
            GUI.Label(R(x, y, w, h), s, textStyle);
        }

        void Glow(string s, float x, float y, float w, float h, int size, Color c, TextAnchor anchor)
        {
            var g = c; g.a = 0.18f;
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4f;
                Text(s, x + Mathf.Cos(a) * 2.5f, y + Mathf.Sin(a) * 2.5f, w, h, size, g, anchor, false);
            }
            Text(s, x, y, w, h, size, Color.Lerp(c, Color.white, 0.55f), anchor, false);
        }

        void Bar(float x, float y, float w, float h, float v, Color c, Color back)
        {
            Fill(x, y, w, h, back);
            Fill(x, y, w * Mathf.Clamp01(v), h, c);
        }

        // ------------------------------------------------------------------ layout
        public override void OnGUI()
        {
            if (!visible || !hasCar || car == null) return;
            if (Event.current.type != EventType.Repaint) return;
            try { if (mainscript.s != null && mainscript.s.pauseMenuOpen) return; } catch { }
            InitGfx();

            Vector2 size = Size();
            S = scale.Value * Screen.height / 1080f;
            alpha = opacity.Value;
            float w = size.x * S, h = size.y * S, m = 24f * Screen.height / 1080f;
            string p = position.Value;
            ox = p.EndsWith("left") ? m : p.EndsWith("right") ? Screen.width - w - m : (Screen.width - w) / 2f;
            oy = p.StartsWith("Top") ? m : Screen.height - h - m;
            ox += offsetX.Value * Screen.height / 1080f;
            oy += offsetY.Value * Screen.height / 1080f;

            var saved = GUI.color;
            switch (style.Value)
            {
                case "Analog": DrawAnalog(); break;
                case "Race": DrawRace(); break;
                case "Retro LCD": DrawRetro(); break;
                case "Neon": DrawNeon(); break;
                case "Minimal": DrawMinimal(); break;
                case "Bars": DrawBars(); break;
                default: DrawBox(); break;
            }
            GUI.color = saved;
        }

        int ExtraRows()
        {
            int n = 0;
            if (showRpm.Value) n++;
            if (showFuel.Value) n++;
            if (showTemp.Value) n++;
            if (showOdo.Value || showTrip.Value) n++;
            return n;
        }

        Vector2 Size()
        {
            switch (style.Value)
            {
                case "Analog": return new Vector2(560, 300);
                case "Race": return new Vector2(480, 190);
                case "Retro LCD": return new Vector2(380, 160);
                case "Neon": return new Vector2(300, 300);
                case "Minimal": return new Vector2(220, 100);
                case "Bars":
                    {
                        int n = (showSpeed.Value ? 1 : 0) + (showRpm.Value ? 1 : 0) + (showFuel.Value ? 1 : 0) + (showTemp.Value ? 1 : 0);
                        return new Vector2(300, 20 + n * 46 + (showGear.Value || showOdo.Value || showTrip.Value ? 34 : 0));
                    }
                default: return new Vector2(320, 110 + ExtraRows() * 30);
            }
        }

        string DistText()
        {
            string s = "";
            if (showOdo.Value) s += "ODO " + Dist(odoKm).ToString("0.0", CultureInfo.InvariantCulture) + " " + DistUnit;
            if (showTrip.Value) s += (s.Length > 0 ? "   " : "") + "TRIP " + Dist(tripKm).ToString("0.0", CultureInfo.InvariantCulture);
            return s;
        }

        string FuelText { get { return fuelMax > 0f ? fuel.ToString("0.0", CultureInfo.InvariantCulture) + " L" : "--"; } }
        string TempText { get { return Mathf.RoundToInt(temp) + "°C"; } }
        Color GearColor { get { return ShiftNow && Blink ? Red : Color.white; } }

        // ---- Box: a clean card with a big speed number and small bars
        void DrawBox()
        {
            var sz = Size();
            Color ac = Accent;
            if (background.Value) Panel(0, 0, sz.x, sz.y, Bg);
            Fill(0, 0, 6, sz.y, new Color(ac.r, ac.g, ac.b, 0.9f));
            if (showSpeed.Value)
            {
                Text(Mathf.RoundToInt(dSpeed).ToString(), 20, 6, 190, 80, 64, Color.white, TextAnchor.MiddleLeft, true);
                Text(SpeedUnit, 20, 72, 190, 24, 15, ac, TextAnchor.MiddleLeft, false);
            }
            if (showGear.Value)
            {
                Panel(sz.x - 96, 16, 78, 78, new Color(1, 1, 1, 0.08f));
                Text(gear, sz.x - 96, 16, 78, 78, 50, GearColor, TextAnchor.MiddleCenter, true);
            }
            float y = 108;
            if (showRpm.Value)
            {
                Text("RPM", 20, y, 60, 22, 13, Dim, TextAnchor.MiddleLeft, false);
                Bar(74, y + 7, 160, 8, RpmN, ShiftNow ? Red : ac, new Color(1, 1, 1, 0.12f));
                Text(Mathf.RoundToInt(dRpm).ToString(), 240, y, 64, 22, 14, Color.white, TextAnchor.MiddleRight, false);
                y += 30;
            }
            if (showFuel.Value)
            {
                Text("FUEL", 20, y, 60, 22, 13, Dim, TextAnchor.MiddleLeft, false);
                Bar(74, y + 7, 160, 8, FuelN, LowFuel ? Red : ac, new Color(1, 1, 1, 0.12f));
                Text(FuelText, 240, y, 64, 22, 14, LowFuel && Blink ? Red : Color.white, TextAnchor.MiddleRight, false);
                y += 30;
            }
            if (showTemp.Value)
            {
                Text("TEMP", 20, y, 60, 22, 13, Dim, TextAnchor.MiddleLeft, false);
                Bar(74, y + 7, 160, 8, TempN, Hot ? Red : ac, new Color(1, 1, 1, 0.12f));
                Text(TempText, 240, y, 64, 22, 14, Hot && Blink ? Red : Color.white, TextAnchor.MiddleRight, false);
                y += 30;
            }
            if (showOdo.Value || showTrip.Value) Text(DistText(), 20, y, sz.x - 30, 22, 13, Dim, TextAnchor.MiddleLeft, false);
        }

        // ---- Analog: classic round tachometer + speedometer
        void Dial(float cx, float cy, float r, float max, float value, float major, int labelDiv, string unit, float redFrom, string center, string sub)
        {
            Color ac = Accent;
            const float a0 = -135f, a1 = 135f;
            if (background.Value)
            {
                Disc(cx, cy, r + 6, new Color(0, 0, 0, 0.55f));
                Disc(cx, cy, r, new Color(0.07f, 0.08f, 0.1f, 0.92f));
            }
            Arc(cx, cy, r - 4, 3, a0, a1, new Color(1, 1, 1, 0.25f));
            if (redFrom < max) Arc(cx, cy, r - 4, 9, Mathf.Lerp(a0, a1, redFrom / max), a1, new Color(Red.r, Red.g, Red.b, 0.85f));
            int majors = Mathf.RoundToInt(max / major);
            for (int i = 0; i <= majors * 2; i++)
            {
                float a = Mathf.Lerp(a0, a1, i / (float)(majors * 2));
                bool big = i % 2 == 0;
                Ray(cx, cy, a, r - (big ? 22 : 14), r - 6, big ? 3.5f : 2f, big ? Color.white : Dim);
                if (big)
                {
                    float rad = (a - 90f) * Mathf.Deg2Rad, lr = r - 40;
                    float lx = cx + Mathf.Cos(rad) * lr, ly = cy + Mathf.Sin(rad) * lr;
                    Text(((int)(i / 2 * major / labelDiv)).ToString(), lx - 20, ly - 12, 40, 24, r > 120 ? 17 : 15, Color.white, TextAnchor.MiddleCenter, false);
                }
            }
            Text(unit, cx - 60, cy - r * 0.42f, 120, 20, 12, Dim, TextAnchor.MiddleCenter, false);
            float na = Mathf.Lerp(a0, a1, Mathf.Clamp01(value / max));
            Ray(cx, cy, na, -14, r - 12, 6f, new Color(0, 0, 0, 0.4f));
            Ray(cx, cy, na, -14, r - 12, 4f, ac);
            Disc(cx, cy, 12, new Color(0.15f, 0.16f, 0.18f, 1f));
            Disc(cx, cy, 6, ac);
            if (center != null) Text(center, cx - 60, cy + r * 0.28f, 120, 44, 34, Color.white, TextAnchor.MiddleCenter, true);
            if (sub != null) Text(sub, cx - 90, cy + r * 0.62f, 180, 20, 12, Dim, TextAnchor.MiddleCenter, false);
        }

        void DrawAnalog()
        {
            float rpmMax = Mathf.Ceil(maxRpm / 1000f) * 1000f;
            if (showRpm.Value)
                Dial(130, 140, 120, rpmMax, dRpm, 1000f, 1000, "x1000 RPM", maxRpm * 0.9f, showGear.Value ? gear : null, null);
            if (showSpeed.Value)
            {
                float max = DialMax;
                float step = max <= 120 ? 10f : 20f;
                Dial(410, 150, 140, max, dSpeed, step, 1, SpeedUnit, max + 1, Mathf.RoundToInt(dSpeed).ToString(), DistText());
            }
            else if (showGear.Value && !showRpm.Value) Text(gear, 330, 100, 160, 100, 60, GearColor, TextAnchor.MiddleCenter, true);
            if (ShiftNow && Blink) Disc(130, 82, 7, Red);
            // small fuel + temp gauges under the tacho
            float y = 282;
            if (showFuel.Value)
            {
                Text("F", 10, y - 6, 20, 20, 13, Dim, TextAnchor.MiddleCenter, false);
                Bar(32, y, 90, 7, FuelN, LowFuel ? Red : Accent, new Color(1, 1, 1, 0.15f));
            }
            if (showTemp.Value)
            {
                Text("T", 132, y - 6, 20, 20, 13, Dim, TextAnchor.MiddleCenter, false);
                Bar(154, y, 90, 7, TempN, Hot ? Red : Accent, new Color(1, 1, 1, 0.15f));
            }
        }

        // ---- Race: shift lights, huge digital speed, gear box
        void DrawRace()
        {
            Color ac = Accent;
            if (background.Value) Panel(0, 0, 480, 190, Bg);
            if (showRpm.Value)
            {
                const int n = 15;
                bool flash = ShiftNow && Blink;
                for (int i = 0; i < n; i++)
                {
                    float t = (i + 1f) / n;
                    Color c = t < 0.5f ? new Color(0.2f, 1f, 0.3f) : t < 0.8f ? new Color(1f, 0.85f, 0.1f) : Red;
                    if (flash) c = new Color(0.3f, 0.55f, 1f);
                    bool on = RpmN >= t - 0.5f / n || flash;
                    float x = 18 + i * 30;
                    Disc(x + 12, 22, 9, on ? c : new Color(1, 1, 1, 0.08f));
                }
                Text(Mathf.RoundToInt(dRpm) + " RPM", 300, 150, 162, 30, 16, Dim, TextAnchor.MiddleRight, false);
            }
            if (showGear.Value)
            {
                Panel(18, 48, 110, 110, new Color(ac.r, ac.g, ac.b, 0.9f));
                Text(gear, 18, 48, 110, 110, 74, ShiftNow && Blink ? Color.white : new Color(0.05f, 0.05f, 0.06f), TextAnchor.MiddleCenter, false);
            }
            if (showSpeed.Value)
            {
                Text(Mathf.RoundToInt(dSpeed).ToString("000"), 140, 36, 240, 120, 96, Color.white, TextAnchor.MiddleRight, true);
                Text(SpeedUnit, 386, 110, 80, 30, 17, ac, TextAnchor.MiddleLeft, false);
            }
            float x2 = 18;
            if (showFuel.Value) { Text("FUEL " + FuelText, x2, 158, 140, 26, 14, LowFuel && Blink ? Red : Color.white, TextAnchor.MiddleLeft, false); x2 += 140; }
            if (showTemp.Value) { Text("TEMP " + TempText, x2, 158, 130, 26, 14, Hot && Blink ? Red : Color.white, TextAnchor.MiddleLeft, false); }
            if (showOdo.Value || showTrip.Value) Text(DistText(), 300, 88, 162, 24, 12, Dim, TextAnchor.MiddleRight, false);
        }

        // ---- Retro LCD: 80s dash, dim "888" ghost segments behind the digits
        void DrawRetro()
        {
            Color lcd = Accent;
            Color ghost = new Color(lcd.r, lcd.g, lcd.b, 0.09f);
            if (background.Value)
            {
                Panel(0, 0, 380, 160, new Color(0.12f, 0.12f, 0.12f, 0.95f));
                Panel(10, 10, 360, 140, new Color(0.02f, 0.05f, 0.03f, 0.95f));
            }
            if (showSpeed.Value)
            {
                Text("888", 20, 14, 200, 90, 78, ghost, TextAnchor.MiddleRight, false);
                Text(Mathf.RoundToInt(dSpeed).ToString().PadLeft(3), 20, 14, 200, 90, 78, lcd, TextAnchor.MiddleRight, false);
                Text(SpeedUnit, 226, 70, 60, 24, 14, lcd, TextAnchor.MiddleLeft, false);
            }
            if (showGear.Value)
            {
                Text("8", 290, 14, 70, 70, 56, ghost, TextAnchor.MiddleCenter, false);
                Text(gear, 290, 14, 70, 70, 56, ShiftNow && Blink ? Red : lcd, TextAnchor.MiddleCenter, false);
            }
            if (showRpm.Value)
            {
                const int n = 24;
                for (int i = 0; i < n; i++)
                {
                    bool on = RpmN >= (i + 1f) / n;
                    Color c = i >= n * 0.85f ? Red : lcd;
                    Fill(24 + i * 13.5f, 108 - i * 0.6f, 10, 10 + i * 0.6f, on ? c : ghost);
                }
            }
            string line = "";
            if (showFuel.Value) line += "FUEL " + FuelText + "  ";
            if (showTemp.Value) line += "TEMP " + TempText;
            Text(line, 24, 124, 200, 22, 13, (LowFuel || Hot) && Blink ? Red : lcd, TextAnchor.MiddleLeft, false);
            if (showOdo.Value) Text(Dist(odoKm).ToString("000000.0", CultureInfo.InvariantCulture), 230, 124, 130, 22, 13, lcd, TextAnchor.MiddleRight, false);
        }

        // ---- Neon: glowing ring RPM arc around a big speed number
        void DrawNeon()
        {
            Color ac = Accent;
            float cx = 150, cy = 150, r = 130;
            if (background.Value) Disc(cx, cy, r + 8, new Color(0, 0, 0, 0.45f));
            if (showRpm.Value)
            {
                const int n = 40;
                for (int i = 0; i < n; i++)
                {
                    float t = (i + 0.5f) / n;
                    float a = Mathf.Lerp(-135f, 135f, t);
                    bool on = RpmN >= t;
                    Color c = t > 0.85f ? Red : ac;
                    if (on) Ray(cx, cy, a, r - 22, r + 2, 9f, new Color(c.r, c.g, c.b, 0.25f));
                    Ray(cx, cy, a, r - 18, r - 2, 4.5f, on ? Color.Lerp(c, Color.white, 0.3f) : new Color(1, 1, 1, 0.07f));
                }
            }
            if (showSpeed.Value)
            {
                Glow(Mathf.RoundToInt(dSpeed).ToString(), cx - 120, cy - 60, 240, 100, 84, ac, TextAnchor.MiddleCenter);
                Text(SpeedUnit, cx - 60, cy + 30, 120, 22, 14, ac, TextAnchor.MiddleCenter, false);
            }
            if (showGear.Value) Glow(gear, cx - 30, cy + 54, 60, 50, 36, ShiftNow && Blink ? Red : Color.white, TextAnchor.MiddleCenter);
            string line = "";
            if (showFuel.Value) line += FuelText;
            if (showTemp.Value) line += (line.Length > 0 ? "  •  " : "") + TempText;
            Text(line, cx - 110, cy + 104, 220, 22, 13, (LowFuel || Hot) && Blink ? Red : Dim, TextAnchor.MiddleCenter, false);
            if (showOdo.Value || showTrip.Value) Text(DistText(), cx - 140, cy - 92, 280, 20, 11, Dim, TextAnchor.MiddleCenter, false);
        }

        // ---- Minimal: just the numbers, no panel
        void DrawMinimal()
        {
            if (background.Value) Panel(0, 0, 220, 100, new Color(0, 0, 0, 0.35f));
            if (showSpeed.Value)
            {
                Text(Mathf.RoundToInt(dSpeed).ToString(), 10, 0, 140, 80, 60, Color.white, TextAnchor.MiddleRight, true);
                Text(SpeedUnit, 156, 46, 60, 22, 13, Accent, TextAnchor.MiddleLeft, true);
            }
            if (showGear.Value) Text(gear, 156, 10, 60, 40, 30, GearColor, TextAnchor.MiddleLeft, true);
            string line = "";
            if (showRpm.Value) line += Mathf.RoundToInt(dRpm) + " rpm";
            if (showFuel.Value) line += (line.Length > 0 ? "  " : "") + FuelText;
            if (showTemp.Value) line += (line.Length > 0 ? "  " : "") + TempText;
            Text(line, 10, 74, 200, 22, 13, (LowFuel || Hot) && Blink ? Red : Dim, TextAnchor.MiddleCenter, true);
        }

        // ---- Bars: one labeled bar per readout
        void DrawBars()
        {
            var sz = Size();
            Color ac = Accent;
            if (background.Value) Panel(0, 0, sz.x, sz.y, Bg);
            float y = 12;
            Action<string, string, float, bool> row = (label, value, v, warn) =>
            {
                Text(label, 16, y, 120, 22, 13, Dim, TextAnchor.MiddleLeft, false);
                Text(value, 140, y, 144, 22, 16, warn && Blink ? Red : Color.white, TextAnchor.MiddleRight, false);
                Bar(16, y + 26, sz.x - 32, 8, v, warn ? Red : ac, new Color(1, 1, 1, 0.1f));
                y += 46;
            };
            if (showSpeed.Value) row("SPEED", Mathf.RoundToInt(dSpeed) + " " + SpeedUnit.ToLower(), dSpeed / Mathf.Max(1f, DialMax), false);
            if (showRpm.Value) row("RPM", Mathf.RoundToInt(dRpm).ToString(), RpmN, ShiftNow);
            if (showFuel.Value) row("FUEL", FuelText, FuelN, LowFuel);
            if (showTemp.Value) row("TEMP", TempText, TempN, Hot);
            string bottom = (showGear.Value ? "GEAR " + gear + "    " : "") + DistText();
            if (bottom.Length > 0) Text(bottom, 16, y, sz.x - 32, 24, 13, Color.white, TextAnchor.MiddleLeft, false);
        }
    }
}
