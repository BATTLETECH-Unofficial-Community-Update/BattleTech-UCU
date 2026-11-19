// Mod postfixes on AbstractActor.Hydrate, run once the actor is whole (mod support). Mech, Vehicle
// and Turret Hydrate call base.Hydrate first and set MechDef, components and pilot after it (retail's own order, checked
// in retail Assembly-CSharp). Mods that postfix AbstractActor.Hydrate read that state: in BTA, TisButAScratch reads
// GetPilot().StatCollection and StrategicOperations reads GetStaticUnitTags() (MechDef). Both throw a
// NullReferenceException on the first actor, CombatGameState hydration fails and the combat save returns to the main
// menu, on retail as on UCU.
// At the first combat load from a save this lists the postfixes other assemblies put on AbstractActor.Hydrate. Each one
// whose parameters are only the actor (__instance) and Hydrate's own arguments is deferred: while the actor is inside
// its concrete Mech, Vehicle or Turret Hydrate the call is skipped and noted, and after that Hydrate returns the noted
// postfixes run in their original order with the same arguments. Their code is unchanged. A postfix that throws then
// still fails the load. Others are left alone and logged with the reason. Nothing is patched before a combat load from
// a save, and with no such postfixes only the log line is written.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class ActorHydrateDeferral
    {
        const string HarmonyId = "battletech.port.actor-hydrate-deferral";
        static Action<string> log;
        static bool applied;
        static MethodInfo baseHydrate;
        // per deferred mod postfix: how to build its arguments from (actor, references, loadedState)
        static readonly Dictionary<MethodBase, int[]> argMap = new Dictionary<MethodBase, int[]>();
        static readonly List<object> inHydrate = new List<object>();
        static readonly Dictionary<object, List<MethodInfo>> pending = new Dictionary<object, List<MethodInfo>>(new RefEq());

        sealed class RefEq : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("Assembly-CSharp", HookCombatLoad, logLine, "actor-hydrate");
        }

        static void HookCombatLoad(Assembly game)
        {
            try
            {
                Type gameInstance = game.GetType("BattleTech.GameInstance", false);
                MethodInfo create = gameInstance?.GetMethod("CreateCombatFromSave", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (create == null) { log("actor-hydrate|skipped|GameInstance.CreateCombatFromSave not found"); return; }
                new Harmony(HarmonyId).Patch(create, prefix: new HarmonyMethod(typeof(ActorHydrateDeferral).GetMethod(nameof(BeforeCombatLoad), BindingFlags.Static | BindingFlags.NonPublic)));
                log("actor-hydrate|armed|GameInstance.CreateCombatFromSave");
            }
            catch (Exception ex) { log("actor-hydrate|FAILED|" + ex); }
        }

        static void BeforeCombatLoad()
        {
            if (applied) return;
            applied = true;
            try { Apply(); }
            catch (Exception ex) { log("actor-hydrate|FAILED|" + ex); }
        }

        static void Apply()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor");
            foreach (MethodInfo m in actor.GetMethods(inst))
                if (m.Name == "Hydrate" && m.GetParameters().Length == 2) baseHydrate = m;
            if (baseHydrate == null) { log("actor-hydrate|skipped|AbstractActor.Hydrate not found"); return; }
            Patches info = Harmony.GetPatchInfo(baseHydrate);
            var harmony = new Harmony(HarmonyId);
            var skip = new HarmonyMethod(typeof(ActorHydrateDeferral).GetMethod(nameof(ModPostfixPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            int deferred = 0;
            if (info != null)
                foreach (Patch p in info.Postfixes)
                {
                    MethodInfo pm = p.PatchMethod;
                    string name = pm.DeclaringType?.FullName + "." + pm.Name + " (" + p.owner + ")";
                    string why = Mappable(pm, actor, out int[] map);
                    if (why != null) { log("actor-hydrate|left|" + name + "|" + why); continue; }
                    argMap[pm] = map;
                    harmony.Patch(pm, prefix: skip);
                    deferred++;
                    log("actor-hydrate|deferred|" + name);
                }
            if (deferred == 0) { log("actor-hydrate|none|no mod postfix on AbstractActor.Hydrate to defer"); return; }
            var enter = new HarmonyMethod(typeof(ActorHydrateDeferral).GetMethod(nameof(Enter), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
            var leave = new HarmonyMethod(typeof(ActorHydrateDeferral).GetMethod(nameof(Leave), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last };
            string hooked = "";
            foreach (string tn in new[] { "BattleTech.Mech", "BattleTech.Vehicle", "BattleTech.Turret" })
            {
                Type t = ModAssemblyHook.GameType(tn);
                if (t == null) continue;
                foreach (MethodInfo m in t.GetMethods(inst))
                    if (m.Name == "Hydrate" && m.GetParameters().Length == 2) { harmony.Patch(m, prefix: enter, postfix: leave); hooked += " " + t.Name; }
            }
            log("actor-hydrate|patched|" + deferred + " postfix(es) run after the Hydrate of" + hooked);
        }

        // 0 = the actor, 1 = references, 2 = loadedState. Null when every parameter maps; otherwise why not.
        static string Mappable(MethodInfo pm, Type actor, out int[] map)
        {
            ParameterInfo[] ps = pm.GetParameters();
            ParameterInfo[] orig = baseHydrate.GetParameters();
            map = new int[ps.Length];
            if (!pm.IsStatic) return "not static";
            if (pm.ReturnType != typeof(void)) return "returns a value";
            for (int i = 0; i < ps.Length; i++)
            {
                ParameterInfo p = ps[i];
                if (p.ParameterType.IsByRef) return "parameter " + p.Name + " is by reference";
                if (p.Name == "__instance" && p.ParameterType.IsAssignableFrom(actor)) { map[i] = 0; continue; }
                int at = -1;
                for (int j = 0; j < orig.Length; j++)
                    if (p.Name == orig[j].Name || p.Name == "__" + j) at = j;
                if (at >= 0 && p.ParameterType.IsAssignableFrom(orig[at].ParameterType)) { map[i] = at + 1; continue; }
                return "parameter " + p.Name + " is not the actor or a Hydrate argument";
            }
            return null;
        }

        static bool ModPostfixPrefix(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                if (!argMap.TryGetValue(__originalMethod, out int[] map)) return true;
                object a = null;
                for (int i = 0; i < map.Length; i++) if (map[i] == 0) a = __args[i];
                if (a == null) return true;
                bool inside = false;
                foreach (object o in inHydrate) if (ReferenceEquals(o, a)) { inside = true; break; }
                if (!inside) return true;
                if (!pending.TryGetValue(a, out List<MethodInfo> list)) pending[a] = list = new List<MethodInfo>();
                list.Add((MethodInfo)__originalMethod);
                return false;
            }
            catch (Exception ex) { log("actor-hydrate|prefix|FAILED|" + ex); return true; }
        }

        static void Enter(object __instance) => inHydrate.Add(__instance);

        static void Leave(object __instance, object __0, object __1)
        {
            for (int i = inHydrate.Count - 1; i >= 0; i--) if (ReferenceEquals(inHydrate[i], __instance)) { inHydrate.RemoveAt(i); break; }
            foreach (object o in inHydrate) if (ReferenceEquals(o, __instance)) return; // still inside an outer Hydrate
            if (!pending.TryGetValue(__instance, out List<MethodInfo> list)) return;
            pending.Remove(__instance);
            foreach (MethodInfo pm in list)
            {
                int[] map = argMap[pm];
                var args = new object[map.Length];
                for (int i = 0; i < map.Length; i++) args[i] = map[i] == 0 ? __instance : map[i] == 1 ? __0 : __1;
                try { pm.Invoke(null, args); }
                catch (TargetInvocationException ex)
                {
                    log("actor-hydrate|ran|FAILED|" + pm.DeclaringType?.FullName + "|" + __instance.GetType().Name + "|" + ex.InnerException);
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                }
            }
            log("actor-hydrate|ran|" + __instance.GetType().Name + "|" + list.Count);
        }
    }
}
