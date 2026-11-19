// Unity 6 renamed UnityEngine.Random.Range's parameters: (float,float) min/max became minInclusive/maxInclusive and
// (int,int) min/max became minInclusive/maxExclusive. CustomUnitSpawn 1.0.0.8 binds its Harmony patches by the retail names.
// Keep these aliases limited to that mod and those two targets; other Harmony patches keep normal binding.
using System;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomUnitSpawnCompat
    {
        const string AssemblyName = "CustomUnitSpawn";
        static Action<string> log;
        static bool applied;
        [ThreadStatic] static MethodBase currentTarget;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) Consider(assembly);
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
            try { Apply(); }
            catch (Exception ex) { log("customunitspawn|FAILED|" + ex); }
        }

        // Kept apart so Harmony's internal type resolves only after CustomUnitSpawn is present.
        static void Apply()
        {
            Type manipulator = Type.GetType("HarmonyLib.Public.Patching.HarmonyManipulator, 0Harmony", true);
            Type argumentExtensions = Type.GetType("HarmonyLib.PatchArgumentExtensions, 0Harmony", true);
            MethodInfo process = null, getArgumentIndex = null;
            foreach (MethodInfo method in manipulator.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == "Process" && !method.IsStatic && parameters.Length == 2
                    && parameters[1].ParameterType == typeof(MethodBase)) process = method;
            }
            foreach (MethodInfo method in argumentExtensions.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == "GetArgumentIndex" && method.IsStatic && parameters.Length == 3
                    && parameters[0].ParameterType == typeof(MethodInfo)
                    && parameters[1].ParameterType == typeof(string[])
                    && parameters[2].ParameterType == typeof(ParameterInfo)) getArgumentIndex = method;
            }
            if (process == null || getArgumentIndex == null)
                throw new MissingMethodException("HarmonyManipulator.Process or PatchArgumentExtensions.GetArgumentIndex");

            var harmony = new Harmony("battletech.port.customunitspawn");
            harmony.Patch(process,
                prefix: new HarmonyMethod(typeof(CustomUnitSpawnCompat).GetMethod(nameof(ProcessPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                finalizer: new HarmonyMethod(typeof(CustomUnitSpawnCompat).GetMethod(nameof(ProcessFinalizer), BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(getArgumentIndex,
                prefix: new HarmonyMethod(typeof(CustomUnitSpawnCompat).GetMethod(nameof(AliasArgumentIndex), BindingFlags.Static | BindingFlags.NonPublic)));
            log("customunitspawn|patched|Harmony argument aliases for UnityEngine.Random.Range(float,float) and Range(int,int)");
        }

        static void ProcessPrefix(MethodBase originalMethod, out MethodBase __state)
        {
            __state = currentTarget;
            currentTarget = originalMethod;
        }

        static Exception ProcessFinalizer(Exception __exception, MethodBase __state)
        {
            currentTarget = __state;
            return __exception;
        }

        static bool AliasArgumentIndex(MethodInfo patch, string[] originalParameterNames, ParameterInfo patchParam, ref int __result)
        {
            if (!IsRangeTarget(currentTarget) || patch?.DeclaringType?.Assembly.GetName().Name != AssemblyName
                || patchParam == null) return true;
            ParameterInfo[] targetParameters = currentTarget.GetParameters();
            if (patchParam.ParameterType != targetParameters[0].ParameterType) return true;
            string targetName = patchParam.Name == "min" ? "minInclusive"
                : patchParam.Name == "max" ? (targetParameters[1].ParameterType == typeof(float) ? "maxInclusive" : "maxExclusive") : null;
            if (targetName == null) return true;
            __result = Array.IndexOf(originalParameterNames, targetName);
            return __result < 0;
        }

        static bool IsRangeTarget(MethodBase method)
        {
            if (!(method is MethodInfo info) || !info.IsStatic || info.Name != "Range"
                || info.DeclaringType?.FullName != "UnityEngine.Random") return false;
            ParameterInfo[] parameters = info.GetParameters();
            if (parameters.Length != 2) return false;
            if (info.ReturnType == typeof(float))
                return parameters[0].ParameterType == typeof(float) && parameters[0].Name == "minInclusive"
                    && parameters[1].ParameterType == typeof(float) && parameters[1].Name == "maxInclusive";
            if (info.ReturnType == typeof(int))
                return parameters[0].ParameterType == typeof(int) && parameters[0].Name == "minInclusive"
                    && parameters[1].ParameterType == typeof(int) && parameters[1].Name == "maxExclusive";
            return false;
        }
    }
}
