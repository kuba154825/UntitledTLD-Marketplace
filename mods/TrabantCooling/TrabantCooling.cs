// TrabantCooling - first untitledloader mod.
//
// Adds a radiator (coolant tank) mount near the Trabant's engine. Any car's coolant tank fits.
// While one is mounted:
//   * the car uses it exactly like a water-cooled car does (the game's own coolantTankscript.Cool,
//     fan, thermostat, coolant loss, boiling) - so swapped car07 / car11 / bus engines stay cool;
//   * the stock air cooling of an air-cooled engine is switched off (its passive heat loss is set to
//     that of a normal water-cooled engine), so the radiator is what keeps it cool.
// Remove the radiator and everything goes back to stock.
using System.Collections.Generic;
using UnityEngine;
using UntitledLoader;

namespace TrabantCooling
{
    public class TrabantCoolingMod : Mod
    {
        public override string ID { get { return "trabantcooling"; } }
        public override string Name { get { return "Trabant Cooling"; } }
        public override string Version { get { return "1.0.0"; } }
        public override string MinLoaderVersion { get { return "1.1.0"; } }
        public override string Author { get { return "UntitledTLD"; } }

        const string SlotId = "trabantcooling.radiator";

        string[] targetCars;
        string[] airCooledEngines;
        string referenceEngine;
        Vector3 slotOffset, slotRotation, slotSize;
        bool showHint;

        // engines whose air cooling we turned off -> their original heat loss
        readonly Dictionary<enginescript, float> originalHeatLoss = new Dictionary<enginescript, float>();
        float referenceHeatLoss = -1f;
        readonly HashSet<carscript> ownedCoolantSlot = new HashSet<carscript>();

        public override void OnLoad()
        {
            ReadConfig();
            Slots.Register(new SlotDefinition
            {
                Id = SlotId,
                AppliesTo = car => Cars.IsModel(car, targetCars) && (car.coolantSlot == null || ownedCoolantSlot.Contains(car)),
                Placement = car => new SlotPlacement(Cars.EngineBay(car), slotOffset, slotRotation),
                AcceptedTypes = () => Game.PartTypesOfPrefabsWith<coolantTankscript>(),
                ColliderSize = slotSize,
                OnMounted = (slot, part) => Log.Info("radiator " + Game.ItemNameOf(part) + " mounted on " + Cars.Model(slot.Car)),
                OnUnmounted = (slot, part) => Log.Info("radiator removed from " + Cars.Model(slot.Car)),
            });
            Log.Info("targets: " + string.Join(",", targetCars) + "  air-cooled engines: " + string.Join(",", airCooledEngines));
        }

        void ReadConfig()
        {
            targetCars = Split(Settings.Text("TargetCars", "car03,car03k", "Cars (internal names) that get the radiator mount. car03 = Trabant 601.").Value);
            airCooledEngines = Split(Settings.Text("AirCooledEngines", "car03engine", "Engines whose air cooling is switched off while a radiator is mounted.").Value);
            referenceEngine = Settings.Text("ReferenceWaterCooledEngine", "car01engine", "Air-cooled engines get this engine's passive heat loss while a radiator is mounted.").Value;
            slotOffset = Vec(Settings.Text("SlotOffset", "0,0.3,0.35", "Mount position relative to the engine slot (x,y,z metres). Press F8 in game to see it. Applies to cars spawned/loaded after the change.").Value, new Vector3(0f, 0.3f, 0.35f));
            slotRotation = Vec(Settings.Text("SlotRotation", "0,0,0", "Mount rotation relative to the engine slot (degrees).").Value, Vector3.zero);
            slotSize = Vec(Settings.Text("SlotSize", "0.45,0.35,0.35", "Size of the box you hold the radiator into.").Value, new Vector3(0.45f, 0.35f, 0.35f));
            showHint = Settings.Toggle("ShowHint", true, "Show a hint when holding a radiator near a Trabant.").Value;
        }

        public override void OnSettingsChanged() { ReadConfig(); }

        static string[] Split(string s)
        {
            var p = s.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < p.Length; i++) p[i] = p[i].Trim();
            return p;
        }

        static Vector3 Vec(string s, Vector3 def)
        {
            var p = s.Split(',');
            float x, y, z;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var st = System.Globalization.NumberStyles.Float;
            if (p.Length == 3 && float.TryParse(p[0], st, ci, out x) && float.TryParse(p[1], st, ci, out y) && float.TryParse(p[2], st, ci, out z)) return new Vector3(x, y, z);
            return def;
        }

        public override void OnGameReady()
        {
            referenceHeatLoss = -1f;
            var prefab = Game.ItemPrefab(referenceEngine);
            var e = prefab != null ? prefab.GetComponentInChildren<enginescript>(true) : null;
            if (e != null) referenceHeatLoss = e.engineHeatLoss;
            Log.Info("reference engine " + referenceEngine + " heat loss = " + referenceHeatLoss);
        }

        public override void OnGameExit()
        {
            originalHeatLoss.Clear();
            ownedCoolantSlot.Clear();
        }

        public override void OnUpdate()
        {
            if (!Game.IsReady) return;
            var keepDisabled = new HashSet<enginescript>();
            foreach (var car in Game.Cars)
            {
                if (car == null) continue;
                var slot = Slots.Get(car, SlotId);
                if (slot == null) continue;

                // hand our slot to the game as this car's coolant slot: the game then runs the
                // radiator (Cool(), fan, boiling, coolant loss) exactly as for water-cooled cars
                if (car.coolantSlot == null || car.coolantSlot == slot.Slot)
                {
                    car.coolantSlot = slot.Slot;
                    ownedCoolantSlot.Add(car);
                }

                bool radiator = slot.HasPart && slot.Part.GetComponentInChildren<coolantTankscript>() != null;
                var engine = Cars.Engine(car);
                if (radiator && engine != null && IsAirCooled(engine))
                {
                    keepDisabled.Add(engine);
                    DisableAirCooling(engine);
                }
            }
            // anything we changed that no longer sits in a Trabant with a radiator goes back to stock
            foreach (var e in new List<enginescript>(originalHeatLoss.Keys))
            {
                if (e == null) { originalHeatLoss.Remove(e); continue; }
                if (keepDisabled.Contains(e)) continue;
                e.engineHeatLoss = originalHeatLoss[e];
                originalHeatLoss.Remove(e);
                Log.Info("air cooling restored on " + Game.ItemNameOf(e));
            }
        }

        bool IsAirCooled(enginescript engine)
        {
            var model = Game.ItemNameOf(engine);
            foreach (var air in airCooledEngines)
                if (string.Equals(air, model, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        void DisableAirCooling(enginescript engine)
        {
            if (referenceHeatLoss < 0f || originalHeatLoss.ContainsKey(engine)) return;
            originalHeatLoss[engine] = engine.engineHeatLoss;
            engine.engineHeatLoss = Mathf.Min(engine.engineHeatLoss, referenceHeatLoss);
            Log.Info("air cooling disabled on " + Game.ItemNameOf(engine) + " (heat loss " + originalHeatLoss[engine] + " -> " + engine.engineHeatLoss + ")");
        }

        public override void OnGUI()
        {
            if (!showHint || !Game.IsReady) return;
            var p = Game.Player;
            if (p == null || p.pickedUp == null || p.pickedUp.GetComponentInChildren<coolantTankscript>() == null) return;
            foreach (var car in Game.Cars)
            {
                var slot = Slots.Get(car, SlotId);
                if (slot == null || slot.HasPart) continue;
                if ((slot.transform.position - p.transform.position).sqrMagnitude > 9f) continue;
                GUI.Label(new Rect(Screen.width / 2f - 200, Screen.height * 0.62f, 400, 24),
                    "Trabant radiator mount: hold the radiator above the engine (F8 shows the spot)");
                break;
            }
        }
    }
}
