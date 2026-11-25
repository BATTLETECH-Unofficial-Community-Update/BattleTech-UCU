// Mod compatibility workaround for Abilifier (GitHub issue 36, XAI BattleTech: in the Barracks a skill pip click spends no XP
// and Confirm stays grey; the same pack works on retail).  Class: required technical change (the mod works on retail; the
// Unity 6 object list it reads differs).
//
// Abilifier's prefix on SGBarracksAdvancementPanel.OnValueClick skips the game's method and calls its own
// Helpers.SetTempPilotSkill (Abilifier.Framework.Helpers in 1.2.0.2), which finds the panel with Resources.FindObjectsOfTypeAll<SGBarracksAdvancementPanel>().First().
// FindObjectsOfTypeAll also lists panels on loaded prefabs and inactive copies, in no promised order.  When the first one is not
// the panel on screen, its curPilot is null or stale: the helper throws or updates a panel nobody sees, Abilifier's catch logs
// it, and the click changes nothing (OnValueChangeCB is what lights Confirm, SGBarracksMWDetailPanel.OnValueChanged).
//
// What it does: a prefix on OnValueClick, first in line, remembers the panel the player clicked; in SetTempPilotSkill only, the
// First() after that FindObjectsOfTypeAll call becomes PickPanel, which returns the clicked panel when the list holds it and
// the list's first entry otherwise (First()'s own answer).  Every SetTempPilotSkill call starts from a click on that panel
// (directly, through ForceResetCharacter, or from the ability popup it opens).  The mod's method is patched at the first click,
// not when its assembly loads: patching compiles it, and compiling before ModTek has run the mod's Init can run a type
// initializer too early (gh issue 24 fault M, see ModAssemblyHook.AtFirstCombat).  Without Abilifier nothing is patched.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BattleTechPort
{
    public static class AbilifierBarracksPanelCompat
    {
        const string Tag = "abilifier-panel";
        // Upstream HEAD has Abilifier.Helpers; the 1.2.0.2 build XAI ships has Abilifier.Framework.Helpers.
        static readonly string[] HelpersTypes = { "Abilifier.Framework.Helpers", "Abilifier.Helpers" };
        const string PanelType = "BattleTech.UI.SGBarracksAdvancementPanel";
        static Action<string> log;
        static MethodInfo setTempPilotSkill;
        static Harmony harmony;
        static bool patched;
        static object clicked;
        static int picks, misses;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("Abilifier", Apply, logLine, Tag);
        }

        // Kept apart so that the Harmony reference resolves only when Abilifier is present.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Apply(Assembly abilifier)
        {
            foreach (string name in HelpersTypes)
            {
                setTempPilotSkill = abilifier.GetType(name, false)?.GetMethod("SetTempPilotSkill", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (setTempPilotSkill != null) break;
            }
            if (setTempPilotSkill == null) { log(Tag + "|skipped|no SetTempPilotSkill on " + string.Join(" or ", HelpersTypes)); return; }
            int sites = 0;
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(setTempPilotSkill);
            for (int i = 1; i < original.Count; i++) if (IsPanelFirst(original[i - 1], original[i])) sites++;
            if (sites != 1) { log(Tag + "|skipped|shape changed: " + sites + " FindObjectsOfTypeAll<" + PanelType + ">().First() sites"); return; }
            Type panel = ModAssemblyHook.GameType(PanelType);
            MethodInfo click = panel?.GetMethod("OnValueClick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(int) }, null);
            if (click == null) { log(Tag + "|skipped|no " + PanelType + ".OnValueClick(string,int)"); return; }
            harmony = new Harmony("battletech.port.abilifier-panel");
            harmony.Patch(click, prefix: new HarmonyMethod(typeof(AbilifierBarracksPanelCompat).GetMethod(nameof(ClickPrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
            log(Tag + "|armed|" + abilifier.GetName().Version + "|" + setTempPilotSkill.DeclaringType.FullName + ".SetTempPilotSkill is patched at the first pip click");
        }

        static void ClickPrefix(object __instance)
        {
            clicked = __instance;
            if (patched) return;
            patched = true;
            try
            {
                harmony.Patch(setTempPilotSkill, transpiler: new HarmonyMethod(typeof(AbilifierBarracksPanelCompat).GetMethod(nameof(Transpile), BindingFlags.Static | BindingFlags.NonPublic)));
                log(Tag + "|patched|" + setTempPilotSkill.DeclaringType.FullName + ".SetTempPilotSkill takes the clicked panel");
            }
            catch (Exception ex) { log(Tag + "|FAILED|" + ex); }
        }

        static bool IsPanelFirst(CodeInstruction find, CodeInstruction first)
        {
            MethodInfo f = find.operand as MethodInfo, g = first.operand as MethodInfo;
            return find.opcode == OpCodes.Call && f != null && f.Name == "FindObjectsOfTypeAll" && f.IsGenericMethod
                && f.DeclaringType != null && f.DeclaringType.FullName == "UnityEngine.Resources" && f.GetGenericArguments()[0].FullName == PanelType
                && first.opcode == OpCodes.Call && g != null && g.Name == "First" && g.IsGenericMethod && g.GetParameters().Length == 1
                && g.DeclaringType != null && g.DeclaringType.FullName == "System.Linq.Enumerable";
        }

        // Mutates in place so labels and blocks on the call stay; PickPanel returns object because this DLL cannot name the panel.
        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo pick = typeof(AbilifierBarracksPanelCompat).GetMethod(nameof(PickPanel), BindingFlags.Static | BindingFlags.Public);
            CodeInstruction previous = null;
            foreach (CodeInstruction ins in instructions)
            {
                if (previous != null && IsPanelFirst(previous, ins))
                {
                    Type panel = ((MethodInfo)ins.operand).ReturnType;
                    ins.operand = pick;
                    yield return ins;
                    yield return new CodeInstruction(OpCodes.Castclass, panel);
                }
                else yield return ins;
                previous = ins;
            }
        }

        // The panel array is passed as object[] (reference-type array covariance).  Empty throws as First() does.
        public static object PickPanel(object[] panels)
        {
            if (panels == null || panels.Length == 0) throw new InvalidOperationException("Sequence contains no elements");
            if (clicked != null && Array.IndexOf(panels, clicked) >= 0)
            {
                if (!ReferenceEquals(panels[0], clicked) && picks++ == 0) log(Tag + "|pick|First() was not the clicked panel (" + panels.Length + " panels); the clicked one is used");
                return clicked;
            }
            if (misses++ == 0) log(Tag + "|pick|clicked panel not in the list (" + panels.Length + " panels); First() kept");
            return panels[0];
        }
    }
}
