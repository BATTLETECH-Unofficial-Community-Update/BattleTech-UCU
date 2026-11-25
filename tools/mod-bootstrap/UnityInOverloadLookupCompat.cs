// Unity 6 "in" overload lookups (GitHub issue 42: BTX_ExpansionPack 1.4.3 shows "There was a problem loading the Expansion Pack"
// on UCU only). Class: required technical change (the mods work on retail; Unity 6 changed the engine API under them).
//
// Unity 6 adds a public twin with `in` (by-reference) parameters next to many UnityEngine methods that Unity 2018.4 had once:
// UnityEngine.CoreModule has Vector3.Distance(Vector3, Vector3) on retail and also Vector3.Distance(in Vector3, in Vector3) on
// UCU (138 such pairs across the UnityEngine modules of full-24, read with Mono.Cecil). A mod that looks a method up by name alone,
// AccessTools.Method(typeof(Vector3), "Distance"), gets the one method on retail and an AmbiguousMatchException on UCU.
// BTX_ExpansionPack's ArtilleryCrits transpiler (on CustAmmoCategories.AreaOfEffectHelper.AoEProcessing) does that; the throw
// fails its whole Harmony init (initSuccess = false), so none of its patches and none of its post-load setup run, and its
// MainMenu postfix shows the warning (logic-bext-submods-20261123 I42-C3025-new-4: "Error applying Harmony patches: IL Compile
// Error", inner "Ambiguous match in Harmony patch for UnityEngine.Vector3:Distance").
//
// What it does: a Harmony prefix on HarmonyLib.AccessTools.Method and DeclaredMethod (Type, string, Type[], Type[]). When the
// lookup gives no parameter types, the type is in a UnityEngine assembly, several methods carry the name, and all but one of
// them are `in`/`ref` twins of a by-value method of the same name, it returns that by-value method: the method retail's lookup
// returned, and the one the mods' IL calls (they were compiled against Unity 2018). Every other lookup runs unchanged, an
// ambiguity retail also had included. Registered for every modded player; nothing is patched until ModTek's assembly loads.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class UnityInOverloadLookupCompat
    {
        static Action<string> log;
        static int resolved;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("ModTek", Apply, log, "unity-in-overload");
        }

        static void Apply(Assembly modTek)
        {
            var harmony = new Harmony("battletech.port.unity-in-overload");
            var prefix = new HarmonyMethod(typeof(UnityInOverloadLookupCompat).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
            int patched = 0;
            foreach (string name in new[] { "Method", "DeclaredMethod" })
            {
                MethodInfo m = typeof(AccessTools).GetMethod(name, BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(Type), typeof(string), typeof(Type[]), typeof(Type[]) }, null);
                if (m == null) { log("unity-in-overload|skipped|AccessTools." + name + "(Type, string, Type[], Type[]) not found"); continue; }
                harmony.Patch(m, prefix: prefix);
                patched++;
            }
            log("unity-in-overload|installed|AccessTools lookups patched=" + patched + "|harmony=" + typeof(AccessTools).Assembly.GetName().Version);
        }

        static bool Prefix(Type type, string name, Type[] parameters, Type[] generics, MethodBase __originalMethod, ref MethodInfo __result)
        {
            if (parameters != null || generics != null || type == null || name == null) return true;
            string assembly;
            try { assembly = type.Assembly.GetName().Name; } catch { return true; }
            if (!assembly.StartsWith("UnityEngine", StringComparison.Ordinal)) return true;
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var named = new List<MethodInfo>();
            foreach (MethodInfo x in type.GetMethods(flags)) if (x.Name == name) named.Add(x);   // mscorlib only: no System.Linq
            if (named.Count < 2) return true;
            MethodInfo kept = null;
            foreach (MethodInfo x in named)
            {
                if (IsByRefTwin(x, named)) continue;
                if (kept != null) return true;   // two by-value methods: retail's lookup was ambiguous too
                kept = x;
            }
            if (kept == null) return true;
            __result = kept;
            if (resolved++ < 50)
            {
                var types = new List<string>();
                foreach (ParameterInfo p in kept.GetParameters()) types.Add(p.ParameterType.Name);
                log("unity-in-overload|resolved|" + type.FullName + "." + name + " -> (" + string.Join(", ", types.ToArray()) + ")|of=" + named.Count + "|via=AccessTools." + __originalMethod.Name);
            }
            return false;
        }

        // A method whose parameters are all by-reference (`in` or `ref`, never `out`) and that has a by-value sibling with the same
        // element types: the twin Unity 6 added beside a Unity 2018 method.
        static bool IsByRefTwin(MethodInfo m, List<MethodInfo> named)
        {
            ParameterInfo[] p = m.GetParameters();
            if (p.Length == 0) return false;
            foreach (ParameterInfo x in p) if (!x.ParameterType.IsByRef || x.IsOut) return false;
            foreach (MethodInfo o in named)
            {
                if (o == m) continue;
                ParameterInfo[] q = o.GetParameters();
                if (q.Length != p.Length) continue;
                bool same = true;
                for (int i = 0; i < p.Length && same; i++) same = !q[i].ParameterType.IsByRef && q[i].ParameterType == p[i].ParameterType.GetElementType();
                if (same) return true;
            }
            return false;
        }
    }
}
