// IRTweaks adapter for combat saves (mod support).
// With ScaleObjectiveBuildingStructure on (BTA 20.3 has it on), IRTweaks' postfix on
// TurnDirector.OnInitializeContractComplete sets each objective building's Structure stat and StartingStructure to
// floor(CurrentStructure x multi + mod). It keeps an "already scaled" list so that happens once, but clears that list in
// CombatGameState._Init, and the game sends OnInitializeContractComplete again when a combat save loads. So on every load
// the saved structure, already scaled, would be scaled again. The save holds the scaled Structure stat and
// StartingStructure, and Building.InitFromSave gives the scene object its health back from them, so on a load the postfix
// is skipped, with a log line: the buildings keep what they had at the save.
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
        static PropertyInfo combatOf, wasFromSave;
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
            scale = assembly.GetType("IRTweaks.Modules.Combat.ScaleObjectiveBuildingStructure_TurnDirector_OnInitializeContractComplete", false)
                ?.GetMethod("Postfix", all);
            Type turnDirector = GameType("BattleTech.TurnDirector");
            Type combat = GameType("BattleTech.CombatGameState");
            combatOf = turnDirector == null ? null : AccessTools.Property(turnDirector, "Combat");
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
                if (!CombatSavesRuntimeGate.IsEnabled())
                {
                    log("irtweaks-save|combat saves off (UCU toggle absent)");
                    return;
                }
                object config = irtweaks.GetType("IRTweaks.Mod", false)?.GetField("Config", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                object fixes = config?.GetType().GetField("Fixes")?.GetValue(config);
                FieldInfo disableCombatSaves = fixes?.GetType().GetField("DisableCombatSaves");
                if (disableCombatSaves == null || disableCombatSaves.FieldType != typeof(bool))
                    log("irtweaks-save|skipped|DisableCombatSaves shape changed; combat saves remain off");
                else
                {
                    disableCombatSaves.SetValue(fixes, false);
                    log("irtweaks-save|combat saves on (UCU toggle)");
                }
                if (scale == null || combatOf == null || wasFromSave == null)
                {
                    log("irtweaks-save|skipped|scale adapter shape changed Postfix=" + (scale != null) + " TurnDirector.Combat=" + (combatOf != null) + " WasFromSave=" + (wasFromSave != null));
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
                log("irtweaks-save|patched|objective building scaling skipped on a load");
            }
            catch (Exception ex) { log("irtweaks-save|arm|FAILED|" + ex); }
        }

        // The patched method is IRTweaks' static Postfix(TurnDirector __instance, MessageCenterMessage message): __0 is the
        // TurnDirector.
        static bool ScalePrefix(object __0)
        {
            try
            {
                object combat = __0 == null ? null : combatOf.GetValue(__0, null);
                if (combat == null || !(bool)wasFromSave.GetValue(combat, null)) return true;
                log("irtweaks-save|scale|skipped on load|ScaleObjectiveBuildingStructure: the save holds the scaled structure");
                return false;
            }
            catch (Exception ex) { log("irtweaks-save|scale|FAILED|" + ex); return true; }
        }
    }
}
