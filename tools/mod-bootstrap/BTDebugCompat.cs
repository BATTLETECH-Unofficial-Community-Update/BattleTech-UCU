// Mod compatibility workaround for BTDebug (found loading BEX:T in a UCU install, 2026-11-17: infinite load on
// Preparing for Combat).  Class: required technical change (the mod works on retail; UCU changed what Shader.Find sees).
//
// BTDebug.GizmoManager's constructor makes its gizmo materials with new Material(Shader.Find("...")).  Two of the names,
// "Unlit/BT-Stars" and "Unlit/BT-LaserUI", are retail shaders that only retail's asset bundles carry on UCU, and
// Shader.Find returns null for those (BTShaderResolver, transplant patch 13).  new Material(null) throws; the exception
// leaves BTDebug's prefix on EncounterLayerParent.InitializeContract, the contract never initializes, and the load never ends.
//
// What it does, so the constructor runs and the gizmo materials exist: every Shader.Find call inside that one constructor is
// replaced by FindShader, which asks Shader.Find first (the game's own answer wins), then BTShaderResolver.Find (UCU's
// bundle shaders, when the binder has one), then the plain shaders UI/DefaultBackground and Sprites/Default.  A gizmo that
// could not get its own shader is drawn flat; nothing else in the game uses these materials.  Only that constructor is
// touched, and only when an assembly named BTDebug loads (ModTek loads mods long after doorstop calls the bootstrap, as for
// CustomPrewarmCompat); without BTDebug nothing is patched and nothing is logged.  Shader.Find itself is not patched.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BattleTechPort
{
    public static class BTDebugCompat
    {
        const string AssemblyName = "BTDebug";
        const string TypeName = "BTDebug.GizmoManager";
        static readonly string[] Fallbacks = { "UI/DefaultBackground", "Sprites/Default" };
        static Action<string> log;
        static bool applied;
        static MethodInfo shaderFind;        // UnityEngine.Shader.Find(string)
        static MethodInfo resolverFind;      // BattleTech.Rendering.BTShaderResolver.Find(string), when the player has it
        static readonly Dictionary<string, bool> logged = new Dictionary<string, bool>();

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
            catch (Exception ex) { log("btdebug|FAILED|" + ex); }
        }

        // Kept apart so that the Harmony reference resolves only when BTDebug is present.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Apply(Assembly assembly)
        {
            Type gizmo = assembly.GetType(TypeName, false);
            ConstructorInfo ctor = gizmo == null ? null : gizmo.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (ctor == null) { log("btdebug|skipped|no " + TypeName + " constructor"); return; }

            // The assembly that holds UnityEngine.Shader is whichever one this constructor's own call names.
            MethodInfo find = null;
            foreach (CodeInstruction ins in PatchProcessor.GetOriginalInstructions(ctor))
                if (IsShaderFind(ins)) { find = (MethodInfo)ins.operand; break; }
            if (find == null) { log("btdebug|skipped|shape changed: no Shader.Find call in the constructor"); return; }
            shaderFind = find;
            Type resolver = Type.GetType("BattleTech.Rendering.BTShaderResolver, Assembly-CSharp", false);
            resolverFind = resolver == null ? null : resolver.GetMethod("Find", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string) }, null);

            var harmony = new Harmony("battletech.port.btdebug");
            harmony.Patch(ctor, transpiler: new HarmonyMethod(typeof(BTDebugCompat).GetMethod(nameof(Transpile), BindingFlags.Static | BindingFlags.NonPublic)));
            log("btdebug|patched|" + assembly.GetName().Version + "|" + TypeName + " ctor, resolver=" + (resolverFind != null));
        }

        static bool IsShaderFind(CodeInstruction ins)
        {
            MethodInfo m = ins.operand as MethodInfo;
            return ins.opcode == OpCodes.Call && m != null && m.Name == "Find" && m.IsStatic && m.DeclaringType != null
                && m.DeclaringType.FullName == "UnityEngine.Shader" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string);
        }

        // Mutates in place so labels and blocks on the call stay; FindShader returns object because this DLL cannot name Shader.
        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo helper = typeof(BTDebugCompat).GetMethod(nameof(FindShader), BindingFlags.Static | BindingFlags.Public);
            foreach (CodeInstruction ins in instructions)
            {
                if (IsShaderFind(ins))
                {
                    Type shader = ((MethodInfo)ins.operand).ReturnType;
                    ins.operand = helper;
                    yield return ins;
                    yield return new CodeInstruction(OpCodes.Castclass, shader);
                }
                else yield return ins;
            }
        }

        public static object FindShader(string name)
        {
            object found = Invoke(shaderFind, name);
            if (found != null) return found;
            found = resolverFind == null ? null : Invoke(resolverFind, name);
            string via = "BTShaderResolver";
            for (int i = 0; found == null && i < Fallbacks.Length; i++) { found = Invoke(shaderFind, Fallbacks[i]); via = Fallbacks[i]; }
            if (!logged.ContainsKey(name))
            {
                logged[name] = true;
                log("btdebug|shader|" + name + " is not findable; BTDebug gets " + (found == null ? "null" : via));
            }
            return found;
        }

        static object Invoke(MethodInfo method, string name)
        {
            try { return method.Invoke(null, new object[] { name }); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
    }
}
