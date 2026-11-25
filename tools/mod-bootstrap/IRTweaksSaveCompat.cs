// IRTweaks adapter for combat saves (mod support).
// With ScaleObjectiveBuildingStructure on (BTA 20.3 has it on), IRTweaks' postfixes on
// TurnDirector.OnInitializeContractComplete and on Building.AttachBuildingToObjective both call BuildingHelper.ScaleHealth,
// which sets an objective building's Structure stat and StartingStructure to floor(CurrentStructure x multi + mod). It keeps
// an "already scaled" list so that happens once, but clears that list in CombatGameState._Init, and the game does both again
// when a combat save loads (each objective attaches its buildings again at its first check, UpdateCounts). So on every load, and on every
// co-op resync, the saved structure, already scaled, would be scaled again (150 to 800, then 800 to 4050). The save holds the
// scaled Structure stat and StartingStructure, and Building.InitFromSave gives the scene object its health back from them, so
// in a combat from a save ScaleHealth is skipped, with a log line, for a building whose StartingStructure is not its unscaled
// one: the buildings keep what they had at the save. A building first attached after the load still scales, and IRTweaks'
// callers still put the skipped ones in its list.
// IRTweaks' own code is patched only at the first CombatGameState._Init, after IRTweaks.Mod.Init has run and read its
// settings, and only when ScaleObjectiveBuildingStructure is on. Patching it when its assembly loads compiles the
// postfix, which runs IRTweaks.ModState's static constructor before Mod.Init; that constructor reads the not yet loaded
// settings and throws, and ModTek then fails IRTweaks and every mod that depends on it .
// Applied when an assembly named IRTweaks loads; without it nothing is patched and nothing is logged.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BattleTechPort
{
    public static class IRTweaksSaveCompat
    {
        const string AssemblyName = "IRTweaks";
        static Action<string> log;
        static bool applied;
        static PropertyInfo combatOf, wasFromSave, guidOf, defOf, defPoints, starting;
        static FieldInfo overridePoints;
        static MethodInfo scale;
        static Assembly irtweaks;
        static bool armed;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies()) Consider(loaded);
        }

        static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args) => Consider(args.LoadedAssembly);

        static void Consider(Assembly assembly)
        {
            if (applied || assembly == null) return;
            string name;
            try { name = assembly.GetName().Name; } catch { return; }
            if (name != AssemblyName) return;
            applied = true;
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            try { Apply(assembly); }
            catch (Exception ex) { log("irtweaks-save|FAILED|" + ex); }
        }

        static Type GameType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = a.GetType(name, false); } catch { continue; }
                if (t != null) return t;
            }
            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Apply(Assembly assembly)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            irtweaks = assembly;
            Type building = GameType("BattleTech.Building");
            scale = building == null ? null : assembly.GetType("IRTweaks.Modules.Combat.BuildingHelper", false)?.GetMethod("ScaleHealth", all, null, new[] { building }, null);
            Type combat = GameType("BattleTech.CombatGameState");
            combatOf = building == null ? null : AccessTools.Property(building, "Combat");
            guidOf = building == null ? null : AccessTools.Property(building, "GUID");
            defOf = building == null ? null : AccessTools.Property(building, "BuildingDef");
            defPoints = defOf == null ? null : AccessTools.Property(defOf.PropertyType, "StructurePoints");
            starting = building == null ? null : AccessTools.Property(building, "StartingStructure");
            overridePoints = building == null ? null : AccessTools.Field(building, "overrideStructurePoints");
            wasFromSave = combat?.GetProperty("WasFromSave", all);
            MethodInfo init = combat == null ? null : AccessTools.Method(combat, "_Init");
            if (init == null)
            {
                log("irtweaks-save|skipped|shape changed CombatGameState._Init=" + (init != null));
                return;
            }
            new Harmony("battletech.port.irtweaks-save").Patch(init,
                postfix: new HarmonyMethod(typeof(IRTweaksSaveCompat).GetMethod(nameof(ArmPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            log("irtweaks-save|armed|" + assembly.GetName().Version + "|patches IRTweaks at the first combat");
        }

        // First CombatGameState._Init: IRTweaks has run its Init by now. Patch its scaling postfix only when the setting is on.
        static void ArmPostfix()
        {
            if (armed) return;
            armed = true;
            try
            {
                // Issue 24 fault B (patch 161): with the marker absent the adapter is registered at a co-op combat, for its
                // snapshots. Their load needs the scale skip below; IRTweaks' own DisableCombatSaves stays as it is.
                bool marker = CombatSavesRuntimeGate.IsEnabled();
                if (!marker && !CombatSavesRuntimeGate.SaveDataEnabled())
                {
                    log("irtweaks-save|combat saves off (UCU toggle absent)");
                    return;
                }
                object config = irtweaks.GetType("IRTweaks.Mod", false)?.GetField("Config", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                object fixes = config?.GetType().GetField("Fixes")?.GetValue(config);
                if (!marker)
                    log("irtweaks-save|combat saves left as IRTweaks has them (UCU toggle absent; co-op snapshot loads only)");
                else
                {
                    FieldInfo disableCombatSaves = fixes?.GetType().GetField("DisableCombatSaves");
                    if (disableCombatSaves == null || disableCombatSaves.FieldType != typeof(bool))
                        log("irtweaks-save|skipped|DisableCombatSaves shape changed; combat saves remain off");
                    else
                    {
                        disableCombatSaves.SetValue(fixes, false);
                        log("irtweaks-save|combat saves on (UCU toggle)");
                    }
                }
                if (scale == null || combatOf == null || wasFromSave == null || defPoints == null || starting == null || overridePoints == null)
                {
                    log("irtweaks-save|skipped|scale adapter shape changed ScaleHealth=" + (scale != null) + " Building.Combat=" + (combatOf != null) + " WasFromSave=" + (wasFromSave != null)
                        + " StructurePoints=" + (defPoints != null) + " StartingStructure=" + (starting != null) + " overrideStructurePoints=" + (overridePoints != null));
                    return;
                }
                object on = fixes?.GetType().GetField("ScaleObjectiveBuildingStructure")?.GetValue(fixes);
                if (!(on is bool b))
                {
                    log("irtweaks-save|skipped|setting not readable config=" + (config != null) + " fixes=" + (fixes != null));
                    return;
                }
                if (!b)
                {
                    log("irtweaks-save|off|ScaleObjectiveBuildingStructure is off: nothing to skip");
                    return;
                }
                new Harmony("battletech.port.irtweaks-save").Patch(scale,
                    prefix: new HarmonyMethod(typeof(IRTweaksSaveCompat).GetMethod(nameof(ScalePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                log("irtweaks-save|patched|objective buildings scaled at the save are not scaled again on a load");
            }
            catch (Exception ex) { log("irtweaks-save|arm|FAILED|" + ex); }
        }

        // The patched method is IRTweaks' static ScaleHealth(Building): __0 is the building.
        static bool ScalePrefix(object __0)
        {
            try
            {
                object combat = __0 == null ? null : combatOf.GetValue(__0, null);
                if (combat == null || !(bool)wasFromSave.GetValue(combat, null) || !CombatSavesRuntimeGate.SaveDataEnabled()) return true;
                float now = (float)starting.GetValue(__0, null);
                int points = (int)overridePoints.GetValue(__0);
                object def = defOf.GetValue(__0, null);   // null: a restored building whose InitFromSave has not run yet
                if (points <= 0 && def != null) points = (int)defPoints.GetValue(def, null);
                if (points > 0 && now == points) return true;   // unscaled
                log("irtweaks-save|scale|skipped on load|" + guidOf?.GetValue(__0, null) + " structure " + now + " unscaled " + points + ": the save holds the scaled structure");
                return false;
            }
            catch (Exception ex) { log("irtweaks-save|scale|FAILED|" + ex); return true; }
        }
    }
}
