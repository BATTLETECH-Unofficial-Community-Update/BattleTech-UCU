// Mod postfixes on EncounterLayerData.BuildItemRegistry, run on a save load once the combat has its encounter layer (mod
// support). On a new game EncounterLayerData.BuildItemRegistry calls
// CombatGameState.SetEncounterLayerData before anything else; on a load from save it skips that, and the combat gets its
// encounter layer only in EncounterLayerParent.InitFromSavePassTwo (retail's order). StrategicOperations' postfix on
// BuildItemRegistry creates an AI support team, Team.InitStats reads combat.EncounterLayerData through
// CombatGameConstants.GetActiveMoraleDef, and on a load that is still null: the NullReferenceException stops
// InitFromSavePassOne and the loaded battle never leaves its load screen, on retail as on UCU.
// At the first combat load from a save this lists the postfixes other assemblies put on BuildItemRegistry. Each one
// whose parameters are only the layer (__instance) and BuildItemRegistry's combat argument is held while a load from
// save has no encounter layer yet, and runs, in its original order with the same arguments, right after
// CombatGameState.SetEncounterLayerData. A new game is not affected. A held postfix that throws still fails the load.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class EncounterLoadDeferral
    {
        const string HarmonyId = "battletech.port.encounter-load-deferral";
        static Action<string> log;
        static bool applied;
        static MethodInfo buildItemRegistry;
        static PropertyInfo isLoadingFromSave, encounterLayerData;
        static readonly Dictionary<MethodBase, int[]> argMap = new Dictionary<MethodBase, int[]>();
        static readonly List<KeyValuePair<MethodInfo, object[]>> held = new List<KeyValuePair<MethodInfo, object[]>>();

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("Assembly-CSharp", HookCombatLoad, logLine, "encounter-load");
        }

        static void HookCombatLoad(Assembly game)
        {
            try
            {
                MethodInfo create = game.GetType("BattleTech.GameInstance", false)?.GetMethod("CreateCombatFromSave", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (create == null) { log("encounter-load|skipped|GameInstance.CreateCombatFromSave not found"); return; }
                new Harmony(HarmonyId).Patch(create, prefix: new HarmonyMethod(typeof(EncounterLoadDeferral).GetMethod(nameof(BeforeCombatLoad), BindingFlags.Static | BindingFlags.NonPublic)));
                log("encounter-load|armed|GameInstance.CreateCombatFromSave");
            }
            catch (Exception ex) { log("encounter-load|FAILED|" + ex); }
        }

        static void BeforeCombatLoad()
        {
            if (applied || !CombatSavesRuntimeGate.SaveDataEnabled()) return; // patch 161: marker, or a co-op combat (its snapshot reload)
            applied = true;
            try { Apply(); }
            catch (Exception ex) { log("encounter-load|FAILED|" + ex); }
        }

        static void Apply()
        {
            Type layer = ModAssemblyHook.GameType("BattleTech.EncounterLayerData");
            Type combat = ModAssemblyHook.GameType("BattleTech.CombatGameState");
            buildItemRegistry = layer?.GetMethod("BuildItemRegistry", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, new[] { combat }, null);
            MethodInfo setLayer = combat?.GetMethod("SetEncounterLayerData", BindingFlags.Instance | BindingFlags.Public);
            isLoadingFromSave = combat?.GetProperty("IsLoadingFromSave", BindingFlags.Instance | BindingFlags.Public);
            encounterLayerData = combat?.GetProperty("EncounterLayerData", BindingFlags.Instance | BindingFlags.Public);
            if (buildItemRegistry == null || setLayer == null || isLoadingFromSave == null || encounterLayerData == null)
            {
                log("encounter-load|skipped|shape changed BuildItemRegistry=" + (buildItemRegistry != null) + " SetEncounterLayerData=" + (setLayer != null));
                return;
            }
            Patches info = Harmony.GetPatchInfo(buildItemRegistry);
            var harmony = new Harmony(HarmonyId);
            var hold = new HarmonyMethod(typeof(EncounterLoadDeferral).GetMethod(nameof(ModPostfixPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            int count = 0;
            if (info != null)
                foreach (Patch p in info.Postfixes)
                {
                    MethodInfo pm = p.PatchMethod;
                    string name = pm.DeclaringType?.FullName + "." + pm.Name + " (" + p.owner + ")";
                    string why = Mappable(pm, layer, combat, out int[] map);
                    if (why != null) { log("encounter-load|left|" + name + "|" + why); continue; }
                    argMap[pm] = map;
                    harmony.Patch(pm, prefix: hold);
                    count++;
                    log("encounter-load|held on load|" + name);
                }
            if (count == 0) { log("encounter-load|none|no mod postfix on EncounterLayerData.BuildItemRegistry to hold"); return; }
            harmony.Patch(setLayer, postfix: new HarmonyMethod(typeof(EncounterLoadDeferral).GetMethod(nameof(AfterSetLayer), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
            log("encounter-load|patched|" + count + " postfix(es) run after CombatGameState.SetEncounterLayerData on a load");
        }

        // 0 = the layer, 1 = the combat argument. Null when every parameter maps; otherwise why not.
        static string Mappable(MethodInfo pm, Type layer, Type combat, out int[] map)
        {
            ParameterInfo[] ps = pm.GetParameters();
            string origName = buildItemRegistry.GetParameters()[0].Name;
            map = new int[ps.Length];
            if (!pm.IsStatic) return "not static";
            if (pm.ReturnType != typeof(void)) return "returns a value";
            for (int i = 0; i < ps.Length; i++)
            {
                ParameterInfo p = ps[i];
                if (p.ParameterType.IsByRef) return "parameter " + p.Name + " is by reference";
                if (p.Name == "__instance" && p.ParameterType.IsAssignableFrom(layer)) { map[i] = 0; continue; }
                if ((p.Name == origName || p.Name == "__0") && p.ParameterType.IsAssignableFrom(combat)) { map[i] = 1; continue; }
                return "parameter " + p.Name + " is not the layer or the combat";
            }
            return null;
        }

        static bool ModPostfixPrefix(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                if (!argMap.TryGetValue(__originalMethod, out int[] map) || !CombatSavesRuntimeGate.SaveDataEnabled()) return true;
                object combat = null;
                for (int i = 0; i < map.Length; i++) if (map[i] == 1) combat = __args[i];
                if (combat == null) return true; // no combat argument: can't tell a load, so never held
                if (!(bool)isLoadingFromSave.GetValue(combat, null) || encounterLayerData.GetValue(combat, null) != null) return true;
                held.Add(new KeyValuePair<MethodInfo, object[]>((MethodInfo)__originalMethod, (object[])__args.Clone()));
                return false;
            }
            catch (Exception ex) { log("encounter-load|prefix|FAILED|" + ex); return true; }
        }

        static void AfterSetLayer()
        {
            if (held.Count == 0) return;
            var run = new List<KeyValuePair<MethodInfo, object[]>>(held);
            held.Clear();
            foreach (var kv in run)
            {
                try { kv.Key.Invoke(null, kv.Value); }
                catch (TargetInvocationException ex)
                {
                    log("encounter-load|ran|FAILED|" + kv.Key.DeclaringType?.FullName + "|" + ex.InnerException);
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                }
                log("encounter-load|ran|" + kv.Key.DeclaringType?.FullName);
            }
        }
    }
}
