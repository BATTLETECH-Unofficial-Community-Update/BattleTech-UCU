// Mod compatibility workaround for CustomPrewarm (GitHub issue #5). This works around a bug in the mod, which fails on
// retail too; it does not fix a UCU regression. First shipped in 2.1.2-2B with GitHub issues #4, #5 and #6.
//
// SimGameResultAction.additionalValues is optional in def JSON, so it is null on every action that leaves it out (BT
// Advanced's backgrounds, for one). CustomPrewarm's cache type copies it with new List<string>(src.additionalValues),
// which throws on null; the first cache build dies and the mod turns caching off. Its toBT() turns the list back with
// ToArray(), so it can never give back a null either.
//
// What it does, so the game sees exactly what it sees without caching:
//  - Serialize.SimGameResultAction(BattleTech.SimGameResultAction): a null source array is lent an empty one for the
//    copy. The postfix puts null back on the game's action and stores null in the cache object, so the cache keeps the
//    difference between a missing list and an empty one.
//  - Serialize.SimGameResultAction.toBT(): a cache object with a null list turns back into an action with a null
//    array. A non-null list runs the mod's own code.
// Null matters only to save bytes: every reader in SimGameState treats null and empty alike, but the save serializer
// skips a null array and writes an empty one as a field (ProtobufSerialization.SerializeClass, ArraySerializationHandler),
// and an empty one loads back as empty.
//
// ModTek loads CustomPrewarm long after doorstop calls the bootstrap, so the patch is applied from AssemblyLoad when an
// assembly named CustomPrewarm arrives. Without that assembly nothing is patched and nothing is logged.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomPrewarmCompat
    {
        const string AssemblyName = "CustomPrewarm";
        const string TypeName = "CustomPrewarm.Serialize.SimGameResultAction";
        static Action<string> log;
        static bool applied;
        static FieldInfo gameValues;   // BattleTech.SimGameResultAction.additionalValues (string[])
        static FieldInfo cacheValues;  // CustomPrewarm.Serialize.SimGameResultAction.additionalValues (List<string>)

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
            catch (Exception ex) { log("customprewarm|FAILED|" + ex); }
        }

        // Kept apart so that the Harmony reference resolves only when CustomPrewarm is present.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Apply(Assembly assembly)
        {
            Type cacheType = assembly.GetType(TypeName, false);
            if (cacheType == null) { log("customprewarm|skipped|no " + TypeName); return; }
            cacheValues = cacheType.GetField("additionalValues", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            ConstructorInfo ctor = null;
            foreach (ConstructorInfo c in cacheType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                ParameterInfo[] p = c.GetParameters();
                if (p.Length == 1 && p[0].ParameterType.FullName == "BattleTech.SimGameResultAction") { ctor = c; break; }
            }
            MethodInfo toBT = cacheType.GetMethod("toBT", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (ctor == null || toBT == null || cacheValues == null || cacheValues.FieldType != typeof(System.Collections.Generic.List<string>))
            {
                log("customprewarm|skipped|shape changed ctor=" + (ctor != null) + " toBT=" + (toBT != null) + " field=" + (cacheValues != null ? cacheValues.FieldType.FullName : "none"));
                return;
            }
            gameValues = ctor.GetParameters()[0].ParameterType.GetField("additionalValues", BindingFlags.Instance | BindingFlags.Public);
            if (gameValues == null || gameValues.FieldType != typeof(string[]) || toBT.ReturnType != ctor.GetParameters()[0].ParameterType)
            {
                log("customprewarm|skipped|game action shape changed");
                return;
            }

            var harmony = new Harmony("battletech.port.customprewarm");
            harmony.Patch(ctor,
                prefix: new HarmonyMethod(typeof(CustomPrewarmCompat).GetMethod(nameof(CtorPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                postfix: new HarmonyMethod(typeof(CustomPrewarmCompat).GetMethod(nameof(CtorPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(toBT,
                prefix: new HarmonyMethod(typeof(CustomPrewarmCompat).GetMethod(nameof(ToBTPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            log("customprewarm|patched|" + assembly.GetName().Version + "|" + TypeName + " ctor and toBT");
        }

        // __0 is the BattleTech.SimGameResultAction; typed object so the bootstrap needs no Assembly-CSharp reference.
        static void CtorPrefix(object __0, out bool __state)
        {
            __state = __0 != null && gameValues.GetValue(__0) == null;
            if (__state) gameValues.SetValue(__0, new string[0]);
        }

        static void CtorPostfix(object __instance, object __0, bool __state)
        {
            if (!__state) return;
            gameValues.SetValue(__0, null);
            cacheValues.SetValue(__instance, null);
        }

        static bool ToBTPrefix(object __instance, ref object __result, MethodBase __originalMethod)
        {
            if (cacheValues.GetValue(__instance) != null) return true;
            // The mod's own copy minus additionalValues; keep in step with toBT() if the mod adds fields.
            Type cacheType = __instance.GetType();
            Type gameType = ((MethodInfo)__originalMethod).ReturnType;
            object result = Activator.CreateInstance(gameType);
            foreach (string field in new[] { "Type", "value", "valueConstant" })
                gameType.GetField(field).SetValue(result, cacheType.GetField(field).GetValue(__instance));
            __result = result;
            return false;
        }
    }
}
