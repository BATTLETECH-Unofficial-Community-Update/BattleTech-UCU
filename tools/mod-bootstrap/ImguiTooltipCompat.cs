// IMGUI tooltip reset (GitHub issues 21 and 31: CombatLog's hover text never leaves the cursor on UCU).
// Class: required technical change (the mod works on retail; Unity 6 changed engine behaviour under it).
//
// On retail's Unity 2018.4 the engine clears GUI.tooltip as each GUI behaviour begins an event, so a control sets it again
// only while the pointer is over it. On Unity 6000.6 the value survives: once a control set it, every later event of
// every behaviour still reads it, with no control under the pointer and even with none drawn. Measured with the
// LoadProbe knob LOADPROBE_IMGUI_TOOLTIP (tools/build-probe/LoadProbe/ImguiTooltip.cs), work item gh-issue-21-combatlog:
// retail 2018.4.2f1 off=0/60 none=0/60, UCU 6000.6.9f1 off=60/60 none=60/60, tipStart set on UCU only. The clear is
// in native code, not in Unity's managed source (UnityCsReference GUIUtility.BeginGUI / ResetGlobalState hold no tooltip).
//
// What it does: a Harmony postfix on UnityEngine.GUIUtility.BeginGUI, the managed call each behaviour's OnGUI starts
// from, sets GUI.tooltip to "", as retail's native side did. Controls drawn after it set it as before. Mod players only:
// the bootstrap registers it after its ModTek check, the vanilla game draws no IMGUI tooltips, and nothing is patched
// until UnityEngine.IMGUIModule loads. This DLL references mscorlib only, so the Unity members are bound by reflection.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BattleTechPort
{
    public static class ImguiTooltipCompat
    {
        const string AssemblyName = "UnityEngine.IMGUIModule";
        static Action<string> log;
        static bool applied;
        static Action<string> setTooltip;

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
            catch (Exception ex) { log("imgui-tooltip|FAILED|" + ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Apply(Assembly assembly)
        {
            Type utility = assembly.GetType("UnityEngine.GUIUtility", false);
            Type gui = assembly.GetType("UnityEngine.GUI", false);
            MethodInfo begin = null;
            if (utility != null)
                foreach (MethodInfo m in utility.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                    if (m.Name == "BeginGUI" && m.GetParameters().Length == 3) { begin = m; break; }
            PropertyInfo tip = gui == null ? null : gui.GetProperty("tooltip", BindingFlags.Static | BindingFlags.Public);
            MethodInfo set = tip == null ? null : tip.GetSetMethod();
            if (begin == null || set == null) { log("imgui-tooltip|skipped|shape changed: BeginGUI=" + (begin != null) + " tooltip setter=" + (set != null)); return; }
            setTooltip = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), set);
            var harmony = new Harmony("battletech.port.imgui-tooltip");
            harmony.Patch(begin, postfix: new HarmonyMethod(typeof(ImguiTooltipCompat).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)));
            log("imgui-tooltip|patched|" + assembly.GetName().Version + "|GUIUtility.BeginGUI postfix");
        }

        static void Postfix() => setTooltip("");
    }
}
