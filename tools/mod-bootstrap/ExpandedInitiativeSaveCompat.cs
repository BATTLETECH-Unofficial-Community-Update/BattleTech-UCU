// ExpandedInitiative adapter for combat saves (mod support). ExpandedInitiative widens phases to
// Mod.MinPhase..Mod.MaxPhase (1..10 in 2.0.0.0) and postfixes AbstractActor.HasValidInitiative to accept them, but its
// postfix takes `bool __result` by value, so its assignment never reaches the caller and retail's 1..5 rule stays.
// HasValidInitiative is read only by AbstractActor.Dehydrate (logs an ERROR) and AbstractActor.Hydrate (logs an ERROR
// and resets Initiative to BaseInitiative), so a unit saved on phase 6..10 loads on its base phase instead.
// This applies the rule ExpandedInitiative's postfix computes, with its own MinPhase and MaxPhase constants. It is
// skipped when that postfix takes __result by reference (fixed upstream) or the shape changed. Applied when an
// assembly named ExpandedInitiative loads; without it nothing is patched and nothing is logged.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BattleTechPort
{
    public static class ExpandedInitiativeSaveCompat
    {
        const string AssemblyName = "ExpandedInitiative";
        static Action<string> log;
        static bool applied;
        static int minPhase, maxPhase;
        static PropertyInfo initiative;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When(AssemblyName, Apply, logLine, "ei-save");
        }

        static int? Constant(Type t, string name)
        {
            FieldInfo f = t?.GetField(name, BindingFlags.Static | BindingFlags.Public);
            return f != null && f.IsLiteral && f.FieldType == typeof(int) ? (int?)(int)f.GetRawConstantValue() : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Apply(Assembly assembly)
        {
            if (applied) return;
            applied = true;
            Type mod = assembly.GetType("ExpandedInitiative.Mod", false);
            Type modPatch = assembly.GetType("ExpandedInitiative.AbstractActor_HasValidInitiative", false)
                ?? assembly.GetType("ExpandedInitiative.patches.AbstractActor_HasValidInitiative", false);
            MethodInfo modPostfix = modPatch?.GetMethod("Postfix", BindingFlags.Static | BindingFlags.Public);
            ParameterInfo result = null;
            if (modPostfix != null) foreach (ParameterInfo p in modPostfix.GetParameters()) if (p.Name == "__result") result = p;
            int? min = Constant(mod, "MinPhase"), max = Constant(mod, "MaxPhase");
            Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor");
            MethodInfo getter = actor?.GetProperty("HasValidInitiative", BindingFlags.Instance | BindingFlags.Public)?.GetGetMethod();
            initiative = actor?.GetProperty("Initiative", BindingFlags.Instance | BindingFlags.Public);
            if (result == null || min == null || max == null || getter == null || initiative == null)
            {
                log("ei-save|skipped|shape changed __result=" + (result != null) + " MinPhase=" + (min != null) + " MaxPhase=" + (max != null)
                    + " HasValidInitiative=" + (getter != null) + " Initiative=" + (initiative != null));
                return;
            }
            if (result.ParameterType.IsByRef)
            {
                log("ei-save|skipped|" + assembly.GetName().Version + " sets __result by reference");
                return;
            }
            minPhase = min.Value;
            maxPhase = max.Value;
            new Harmony("battletech.port.ei-save").Patch(getter,
                postfix: new HarmonyMethod(typeof(ExpandedInitiativeSaveCompat).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
            log("ei-save|patched|" + assembly.GetName().Version + "|HasValidInitiative accepts " + minPhase + ".." + maxPhase);
        }

        static void Postfix(object __instance, ref bool __result)
        {
            try
            {
                int i = (int)initiative.GetValue(__instance, null);
                __result = i >= minPhase && i <= maxPhase;
            }
            catch (Exception ex) { log("ei-save|FAILED|" + ex); }
        }
    }
}
