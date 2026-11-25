// IMGUI text colour (GitHub issue 21: CombatLog's resize grip is invisible on UCU, and every coloured IMGUI mod text is dark).
// Class: required technical change (the mod works on retail; Unity 6 changed engine behaviour under it).
//
// Retail (Unity 2018.4) and UCU (6000.6) both run in Linear colour space. Retail draws an IMGUI text colour as written:
// a 0.6 grey label is 153 on screen. Unity 6 draws IMGUI text through TextCore, and IMGUITextHandle sets
// TextGenerationSettings.shouldConvertToLinearSpace in Linear projects, so the vertex colour is linearised before the
// GUI text shader linearises it again: the same label is 81 (0.6^2.2). Texture draws under GUI.color are not affected
// (153 on both). CombatLog's grip is a 0.6 grey label over a 68 grey button, so on UCU it is 77 on 68: invisible.
// Measured with the LoadProbe knob LOADPROBE_IMGUI_TOOLTIP (tools/build-probe/LoadProbe/ImguiGrip.cs), work item
// gh-issue-21-combatlog.
//
// What it does: a Harmony postfix on UnityEngine.IMGUITextHandle.ConvertGUIStyleToGenerationSettings clears
// shouldConvertToLinearSpace, so IMGUI text colours reach the screen as retail drew them. Mod players only: the
// bootstrap registers it after its ModTek check, the vanilla game draws its UI with uGUI, and nothing is patched until
// UnityEngine.IMGUIModule loads. This DLL references mscorlib only, so the Unity members are bound by reflection.
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BattleTechPort
{
    public static class ImguiTextColorCompat
    {
        const string AssemblyName = "UnityEngine.IMGUIModule";
        static Action<string> log;
        static bool applied;
        static Action<object> clearLinear;

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
            catch (Exception ex) { log("imgui-text-color|FAILED|" + ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Apply(Assembly assembly)
        {
            Type handle = assembly.GetType("UnityEngine.IMGUITextHandle", false);
            MethodInfo convert = handle == null ? null : handle.GetMethod("ConvertGUIStyleToGenerationSettings", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Type settings = convert == null || convert.GetParameters().Length == 0 ? null : convert.GetParameters()[0].ParameterType;
            FieldInfo flag = settings == null ? null : settings.GetField("shouldConvertToLinearSpace", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (flag == null || flag.FieldType != typeof(bool) || settings.IsValueType)
            { log("imgui-text-color|skipped|shape changed: convert=" + (convert != null) + " flag=" + (flag != null)); return; }
            // called for every IMGUI text draw: a compiled field store, not FieldInfo.SetValue
            var dm = new DynamicMethod("ClearLinear", null, new[] { typeof(object) }, typeof(ImguiTextColorCompat).Module, true);
            ILGenerator il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, settings); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stfld, flag); il.Emit(OpCodes.Ret);
            clearLinear = (Action<object>)dm.CreateDelegate(typeof(Action<object>));
            var harmony = new Harmony("battletech.port.imgui-text-color");
            harmony.Patch(convert, postfix: new HarmonyMethod(typeof(ImguiTextColorCompat).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)));
            log("imgui-text-color|patched|" + assembly.GetName().Version + "|IMGUITextHandle.ConvertGUIStyleToGenerationSettings postfix");
        }

        static void Postfix(object settings) { if (settings != null) clearLinear(settings); }
    }
}
