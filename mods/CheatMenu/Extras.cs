// CheatMenu 1.1 - the new features (World / Player / Vehicles tabs in the app).
// Every value the game owns is remembered before it is changed and put back when the feature is off.
using System;
using System.Collections.Generic;
using UnityEngine;
using UntitledLoader;

namespace CheatMenu
{
    public partial class CheatMenuMod
    {
        // ---------------------------------------------------------------- state
        string timeLock = "off";          // off | sunrise | day | afternoon | sunset | night
        float daySpeed = 1f;              // 1 = normal
        bool noStorms, slowMo, superSpeed, superJump, noFall, infFuel, noHeat;
        string gravity = "normal";        // normal | low | moon
        Vector3 defaultGravity = new Vector3(0f, -9.81f, 0f);
        bool gravityKnown;
        carscript lastCar;

        // player values before super speed / jump (per player object)
        fpscontroller movePlayer;
        float baseMaxSpeed, baseSpeedM, baseJump;

        void LoadExtras()
        {
            timeLock = Config.GetString("AlwaysTime", "off", "internal: set in the Cheat Menu app (World tab)");
        }

        void ResetExtras()
        {
            // things that must not stay changed after leaving a save
            SetSlowMo(false);
            SetGravity("normal");
            RestoreMovement();
            superSpeed = superJump = noFall = infFuel = noHeat = noStorms = false;
            daySpeed = 1f;
            lastCar = null;
        }

        void UpdateExtras()
        {
            var p = Game.Player;
            var car = Game.PlayerCar;
            if (car != null) lastCar = car;
            ApplyTime();
            if (noStorms) ClearStorms();
            if (noFall && p != null) p.godModeTillGrounded = true;
            ApplyMovement(p);
            if (infFuel || noHeat) ApplyCars();
            if (slowMo && Time.timeScale > 0f && Mathf.Abs(Time.timeScale - 0.35f) > 0.001f) Time.timeScale = 0.35f;
        }

        // ---------------------------------------------------------------- time of day
        // napszakvaltakozas: t = time + tekeres - startTime runs 0..dt (day) then dt..dt+nt (night).
        // Sunrise = 0..rt, sunset = dt-et..dt. Locking = putting t at one spot every frame.
        void ApplyTime()
        {
            var n = napszakvaltakozas.s;
            if (n == null || n.dt <= 0f) return;
            if (timeLock == "off")
            {
                if (daySpeed != 1f) n.tekeres += Time.deltaTime * (daySpeed - 1f);
                return;
            }
            float target;
            switch (timeLock)
            {
                case "sunrise": target = n.rt * 0.55f; break;
                case "day": target = (n.rt + n.dt - n.et) * 0.5f; break;
                case "afternoon": target = n.rt + (n.dt - n.et - n.rt) * 0.82f; break;
                case "sunset": target = n.dt - n.et * 0.45f; break;
                case "night": target = n.dt + n.nt * 0.5f; break;
                default: return;
            }
            n.tekeres = target + n.startTime - n.time;
        }

        string SetTime(string mode)
        {
            var ok = new[] { "off", "sunrise", "day", "afternoon", "sunset", "night" };
            if (Array.IndexOf(ok, mode) < 0) return "err unknown time " + mode;
            timeLock = mode;
            Config.Set("AlwaysTime", mode);
            return mode == "off" ? "ok time runs normally again" : "ok always " + mode;
        }

        // ---------------------------------------------------------------- weather / physics
        void ClearStorms()
        {
            foreach (var s in UnityEngine.Object.FindObjectsOfType<sandstormscript>())
                if (s != null && s.gameObject.activeSelf) s.gameObject.SetActive(false);
            var n = napszakvaltakozas.s;
            if (n != null && n.sandStorms != null && n.sandStorms.Count > 0) n.sandStorms.Clear();
        }

        string SetGravity(string mode)
        {
            if (!gravityKnown) { defaultGravity = Physics.gravity; gravityKnown = true; }
            gravity = mode;
            switch (mode)
            {
                case "low": Physics.gravity = defaultGravity * 0.5f; break;
                case "moon": Physics.gravity = defaultGravity * 0.166f; break;
                default: gravity = "normal"; Physics.gravity = defaultGravity; break;
            }
            return "ok gravity " + gravity;
        }

        void SetSlowMo(bool on)
        {
            slowMo = on;
            if (!on && Mathf.Abs(Time.timeScale - 0.35f) < 0.001f) Time.timeScale = 1f;
        }

        // ---------------------------------------------------------------- movement
        void ApplyMovement(fpscontroller p)
        {
            if (p == null) return;
            if (p != movePlayer) { movePlayer = p; baseMaxSpeed = p.FdefMaxSpeed; baseSpeedM = p.FspeedM; baseJump = p.FjumpForce; }
            float sm = superSpeed ? 2.5f : 1f, jm = superJump ? 2.6f : 1f;
            p.FdefMaxSpeed = baseMaxSpeed * sm;
            p.FspeedM = baseSpeedM * sm;
            p.FjumpForce = baseJump * jm;
        }

        void RestoreMovement()
        {
            if (movePlayer == null) return;
            movePlayer.FdefMaxSpeed = baseMaxSpeed; movePlayer.FspeedM = baseSpeedM; movePlayer.FjumpForce = baseJump;
            movePlayer = null;
        }

        // ---------------------------------------------------------------- cars
        void ApplyCars()
        {
            var car = Game.PlayerCar ?? lastCar;
            if (car == null) return;
            if (infFuel && car.Tank != null && car.Tank.F != null && car.Tank.F.GetAmount() < car.Tank.F.maxC * 0.98f && car.Engine != null)
                FillWithMix(car.Tank, car.Engine.FuelConsumption);
            if (noHeat && car.Engine != null && car.Engine.temp > car.Engine.maxOptimalTemp1)
                car.Engine.temp = car.Engine.maxOptimalTemp1;
        }

        carscript TargetCar() { return Game.PlayerCar ?? Look.Car ?? lastCar; }

        string RepairCar()
        {
            var car = TargetCar();
            if (car == null) return "err sit in a car or look at one";
            int n = 0;
            foreach (var pc in car.transform.root.GetComponentsInChildren<partconditionscript>(true))
            {
                if (pc == null || pc.state == 0) continue;
                pc.Refresh(0, pc.color); n++;
            }
            if (car.Engine != null) car.Engine.temp = Mathf.Min(car.Engine.temp, car.Engine.minOptimalTemp1);
            return "ok repaired " + Cars.Model(car) + " (" + n + " part(s) like new)";
        }

        string FlipCar()
        {
            var car = TargetCar();
            if (car == null) return "err sit in a car or look at one";
            Teleport.FlipUpright(car);
            return "ok " + Cars.Model(car) + " is back on its wheels";
        }

        string BringCar()
        {
            if (lastCar == null) return "err drive a car first, then it can come to you";
            var p = Game.Player;
            if (p == null) return "err no player";
            if (Game.PlayerCar == lastCar) return "err you are already in it";
            var fwd = Vector3.ProjectOnPlane(p.Cam != null ? p.Cam.transform.forward : p.transform.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            var pos = p.transform.position + fwd * 6f + Vector3.up * 1.5f;
            RaycastHit hit;
            if (Physics.Raycast(pos + Vector3.up * 30f, Vector3.down, out hit, 80f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                pos = hit.point + Vector3.up * 1.2f;
            Teleport.Car(lastCar, pos, Quaternion.LookRotation(Vector3.Cross(fwd, Vector3.up), Vector3.up));
            return "ok " + Cars.Model(lastCar) + " is right in front of you";
        }

        string GoToCar()
        {
            if (lastCar == null) return "err drive a car first";
            Teleport.PlayerToCar(lastCar);
            return "ok next to your " + Cars.Model(lastCar);
        }

        string ExtrasState()
        {
            return " time=" + timeLock + " dayspeed=" + daySpeed + " nostorms=" + (noStorms ? 1 : 0) + " gravity=" + gravity +
                   " slowmo=" + (slowMo ? 1 : 0) + " speed=" + (superSpeed ? 1 : 0) + " jump=" + (superJump ? 1 : 0) +
                   " nofall=" + (noFall ? 1 : 0) + " fuel=" + (infFuel ? 1 : 0) + " noheat=" + (noHeat ? 1 : 0) +
                   " car=" + (lastCar != null ? Uri.EscapeDataString(Cars.Model(lastCar) ?? "car") : "-");
        }

        // returns null when the command is not one of these
        string ExecuteExtra(string cmd, string[] a)
        {
            bool on = a.Length > 1 && a[1] == "1";
            switch (cmd)
            {
                case "time": return SetTime(a.Length > 1 ? a[1] : "off");
                case "dayspeed":
                    float sp; daySpeed = a.Length > 1 && float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out sp) ? Mathf.Clamp(sp, 1f, 60f) : 1f;
                    return "ok day runs " + daySpeed + "x" + (timeLock != "off" ? " (time is locked: turn Always off to see it)" : "");
                case "gravity": return SetGravity(a.Length > 1 ? a[1] : "normal");
                case "nostorms": noStorms = on; return "ok sandstorms " + (on ? "gone" : "back to normal (new ones can come)");
                case "slowmo": SetSlowMo(on); return "ok slow motion " + (on ? "on" : "off");
                case "speed": superSpeed = on; return "ok super speed " + (on ? "on" : "off");
                case "jump": superJump = on; return "ok super jump " + (on ? "on" : "off");
                case "nofall": noFall = on; return "ok no fall damage " + (on ? "on" : "off");
                case "fuel": infFuel = on; return "ok infinite fuel " + (on ? "on (your car)" : "off");
                case "noheat": noHeat = on; return "ok no overheating " + (on ? "on (your car)" : "off");
                case "repair": return RepairCar();
                case "flip": return FlipCar();
                case "bringcar": return BringCar();
                case "gotocar": return GoToCar();
                case "tplook": return Teleport.PlayerToLookPoint() ? "ok teleported" : "err nothing to teleport to";
            }
            return null;
        }
    }
}
