// MechAffinity pilot quirk and affinity effects after a combat load (mod support, logic-bext-fixes-20261122 finding S, effects layer).
// MechAffinity applies its passive pilot effects from a postfix on AbstractActor.AddToTeam (MechAffinity.Patches.AbstractActor_AddToTeam ->
// PilotQuirkManager/PilotAffinityManager.applyBonuses -> EffectManager.CreateEffect, ids "PassiveEffect_<guid>_<process-wide counter>").
// A combat save holds every effect, those included, and a load calls AddToTeam again (EncounterLayerParent.InitFromSavePassTwo ->
// Mech/Vehicle.SecondaryInit), after EffectManager.Hydrate restored them, so each load adds a second copy. A co-op host never reloads, so
// after every resync its guest holds the unit's quirk effects twice (BEXT-FX-probe compare 101: Locust f2e3b2cf NaiveQuirkDebuff and
// MilitaryQuirkHealth _0/_1 on both peers, plus _2/_3 on the guest; PRE-RESYNC group=effects).
// This adapter, only when an assembly named MechAffinity is loaded, while a combat is loading from a save with combat saves on
// (CombatSavesRuntimeGate.SaveDataEnabled: the marker or a co-op combat), notes the effects before AbstractActor.AddToTeam (a prefix
// before every other) and, after every other postfix, cancels each "PassiveEffect_" effect the call created when the save already holds
// one with the same Description.Id, target and creator (EffectManager.CancelEffect, which takes its statistic change back off). Effects
// the save does not hold, MechAffinity's own lists (spawned actors, delayed lancemate effects) and a new mission are untouched.
// Rows: `mechaffinity-load|patched|...`, `mechaffinity-load|dedupe|<guid>|cancelled=<n>|kept=<k>` (first 60, then every 25th).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class MechAffinityLoadDedupe
    {
        const string Tag = "mechaffinity-load";
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static Action<string> log;
        static PropertyInfo combatOf, isLoadingFromSave, effectManager, effectData, target, guid;
        static FieldInfo effects, effectId, creatorId;
        static MethodInfo cancel;
        static int rows;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("MechAffinity", Apply, logLine, Tag);
        }

        static void Apply(Assembly mod)
        {
            Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor"), combat = ModAssemblyHook.GameType("BattleTech.CombatGameState");
            Type em = ModAssemblyHook.GameType("BattleTech.EffectManager"), effect = ModAssemblyHook.GameType("BattleTech.Effect"), team = ModAssemblyHook.GameType("BattleTech.Team");
            MethodInfo addToTeam = team == null ? null : actor?.GetMethod("AddToTeam", Inst, null, new[] { team }, null);
            combatOf = actor?.GetProperty("Combat", Inst);
            guid = actor?.GetProperty("GUID", Inst);
            isLoadingFromSave = combat?.GetProperty("IsLoadingFromSave", Inst);
            effectManager = combat?.GetProperty("EffectManager", Inst);
            effects = em?.GetField("effects", Inst);
            cancel = effect == null ? null : em?.GetMethod("CancelEffect", new[] { effect, typeof(bool) });
            effectData = effect?.GetProperty("EffectData", Inst);
            target = effect?.GetProperty("Target", Inst);
            effectId = effect?.GetField("id", Inst);
            creatorId = effect?.GetField("creatorID", Inst);
            if (addToTeam == null || combatOf == null || isLoadingFromSave == null || effectManager == null || effects == null || cancel == null
                || effectData == null || target == null || effectId == null || creatorId == null)
            {
                log(Tag + "|skipped|shape changed AddToTeam=" + (addToTeam != null) + " EffectManager.effects=" + (effects != null) + " CancelEffect=" + (cancel != null)
                    + "; MechAffinity's pilot effects double on a combat load");
                return;
            }
            var h = new Harmony("battletech.port.mechaffinity-load");
            h.Patch(addToTeam, prefix: new HarmonyMethod(typeof(MechAffinityLoadDedupe).GetMethod(nameof(NotePrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(MechAffinityLoadDedupe).GetMethod(nameof(DedupePostfix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
            log(Tag + "|patched|" + mod.GetName().Version + "|AbstractActor.AddToTeam on a combat load: MechAffinity's pilot effects are not added again");
        }

        static void NotePrefix(object __instance, out Dictionary<object, bool> __state)
        {
            __state = null;
            try
            {
                object combat = combatOf.GetValue(__instance, null);
                if (combat == null || !(bool)isLoadingFromSave.GetValue(combat, null) || !CombatSavesRuntimeGate.SaveDataEnabled()) return;
                if (!(effects.GetValue(effectManager.GetValue(combat, null)) is IList all)) return;
                __state = new Dictionary<object, bool>();
                foreach (object e in all) __state[e] = true;
            }
            catch (Exception ex) { Row("note-failed|" + GuidOf(__instance) + "|" + ex.Message); }
        }

        static void DedupePostfix(object __instance, Dictionary<object, bool> __state)
        {
            if (__state == null) return;
            try
            {
                object em = effectManager.GetValue(combatOf.GetValue(__instance, null), null);
                if (!(effects.GetValue(em) is IList all)) return;
                var saved = new Dictionary<string, bool>();
                foreach (object e in __state.Keys) saved[Key(e)] = true;
                var added = new List<object>();
                foreach (object e in all) if (!__state.ContainsKey(e) && ((effectId.GetValue(e) as string) ?? "").StartsWith("PassiveEffect_", StringComparison.Ordinal)) added.Add(e);
                int cancelled = 0;
                foreach (object e in added) if (saved.ContainsKey(Key(e))) { cancel.Invoke(em, new object[] { e, true }); cancelled++; }
                if (added.Count > 0) Row("dedupe|" + GuidOf(__instance) + "|cancelled=" + cancelled + "|kept=" + (added.Count - cancelled));
            }
            catch (Exception ex) { Row("dedupe-failed|" + GuidOf(__instance) + "|" + (ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex).Message); }
        }

        // Description.Id, target identity and creator: what CoopDesyncDetector's effects group keys an effect by (with its timer).
        static string Key(object e)
        {
            object data = effectData.GetValue(e, null);
            object desc = data?.GetType().GetField("Description", Inst)?.GetValue(data) ?? data?.GetType().GetProperty("Description", Inst)?.GetValue(data, null);
            string id = desc?.GetType().GetProperty("Id", Inst)?.GetValue(desc, null) as string ?? "";
            object t = target.GetValue(e, null);
            return id + "|" + (t == null ? "" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(t).ToString()) + "|" + (creatorId.GetValue(e) as string ?? "");
        }

        static void Row(string text) { if (++rows <= 60 || rows % 25 == 0) log(Tag + "|" + text); }

        static string GuidOf(object actor)
        {
            try { return guid?.GetValue(actor, null) as string ?? "?"; } catch { return "?"; }
        }
    }
}
