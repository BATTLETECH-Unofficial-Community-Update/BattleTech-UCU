// BEX Timeline's ammo and component conversions after a combat load (mod support, logic-bext-fixes-20261122 layer H).
// BEXTimeline.MechComponentsOverTime.Mech_InitStats.Prefix (a prefix on Mech.InitStats) changes an AI mech's loadout when it spawns,
// by date and faction, with UnityEngine.Random draws: it removes AC ammo boxes and adds "<id>Tracer" boxes in their place (new
// AmmunitionBox appended to allComponents and ammoBoxes), and swaps other components the same way. A combat save holds the
// converted loadout, and a load calls Mech.InitStats again (Mech.Init's IsLoadingFromSave branch, Mech.cs:1049-1056), so the prefix
// rolls again on the loaded mech and converts more boxes, drawing from the global Random. A co-op host never reloads, so after a
// resync its guest has a different loadout (BEXT-G-fix2 compare 201: Victor dd1b3129, AC20 x2 + AC20Tracer on the host, AC20 +
// AC20Tracer x2 on the guest; PRE-RESYNC group=ammo; ComponentOrderLoadCompat left the list as loaded, an ammo box matching no entry).
// This adapter, only when an assembly named BEXTimeline is loaded, skips that prefix while a combat is loading from a save with
// combat saves on (CombatSavesRuntimeGate.SaveDataEnabled: the marker or a co-op combat): the save already holds the conversions.
// The prefix's whole effect (decompile MechComponentsOverTime.Mech_InitStats): it removes AmmunitionBoxes from allComponents and ammoBoxes and appends
// new ones (AddOurAmmo: a MechComponentRef with RefreshComponentDef, uids from UnityEngine.Random), and draws the global UnityEngine.Random; it writes
// no stat, tag or cached def, and on a load it reads the static spawningActor* faction/pirate fields left by the last spawn. Its spawn swaps are in
// the save: the host's appended AC20Tracer (index 34) is in the guest's reloaded list (BEXT-G-fix2 compdump), so skipping the whole prefix loses
// nothing and also removes the guest-only global Random draws a load would make (the RNG sweep's R2 redirect covers one other BEXTimeline call only).
// The gate also covers single-player combat-save loads, which double-convert the same way (only UCU's save shim loads a modded combat save).
// A new mission's spawn is untouched. Rows: `bextimeline-load|patched|...`, `bextimeline-load|skipped|<guid>` (first 60).
using System;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class BEXTimelineLoadSkip
    {
        const string Tag = "bextimeline-load";
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static Action<string> log;
        static PropertyInfo combatOf, isLoadingFromSave, guidOf;
        static int rows;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("BEXTimeline", Apply, logLine, Tag);
        }

        static void Apply(Assembly bex)
        {
            Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor");
            combatOf = actor?.GetProperty("Combat", Inst);
            guidOf = actor?.GetProperty("GUID", Inst);
            isLoadingFromSave = ModAssemblyHook.GameType("BattleTech.CombatGameState")?.GetProperty("IsLoadingFromSave", Inst);
            MethodInfo prefix = bex.GetType("BEXTimeline.MechComponentsOverTime+Mech_InitStats", false)?.GetMethod("Prefix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (prefix == null || combatOf == null || isLoadingFromSave == null)
            {
                log(Tag + "|skipped|shape changed Mech_InitStats.Prefix=" + (prefix != null) + " Combat=" + (combatOf != null) + " IsLoadingFromSave=" + (isLoadingFromSave != null));
                return;
            }
            new Harmony("battletech.port.bextimeline-load").Patch(prefix, prefix: new HarmonyMethod(typeof(BEXTimelineLoadSkip).GetMethod(nameof(SkipOnLoad), BindingFlags.Static | BindingFlags.NonPublic)));
            log(Tag + "|patched|" + bex.GetName().Version + "|Mech_InitStats.Prefix skipped while a combat loads from a save (the save holds its conversions)");
        }

        // __0: the mod prefix's own first argument (the mech).
        static bool SkipOnLoad(object __0)
        {
            try
            {
                object combat = __0 == null ? null : combatOf.GetValue(__0, null);
                if (combat == null || !(bool)isLoadingFromSave.GetValue(combat, null) || !CombatSavesRuntimeGate.SaveDataEnabled()) return true;
                if (++rows <= 60) log(Tag + "|skipped|" + guidOf?.GetValue(__0, null));
                return false;
            }
            catch (Exception e) { log(Tag + "|failed|" + e.Message); return true; }
        }
    }
}
