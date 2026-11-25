// CustomActivatableEquipment HUD activations in co-op (gh issue 24; Sam, 2026-11-22, D-20261122-6 = A).
// A player turns a CAE component on or off in the combat HUD (CombatHUDEquipmentSlotEx, ActivatbleMenuTriggers, ComponentToggle), which calls
// ActivatableComponent.toggleComponentActivation(MechComponent) on the clicking peer only: activateComponent's fail roll (rollFail, isInital true),
// a failed activation's crits and damage, deactivateComponent, OnActivationBegin for a non-SafeActivation component, the component's active-state
// stats and effects. No invocation carries any of it, so the other peers kept the component as it was.
// This adapter, only when an assembly named CustomActivatableEquipment is loaded and only in a co-op combat, records a toggle made outside any
// invocation's run on the unit's authority (its owner for a player unit, the host for an AI unit; CoopDecisionChannel): the component uid, whether
// it is active afterwards, the activation fail roll's result and UnityEngine.Random.state when the toggle began, and sends it at once
// (CoopInvocationSideData.SendNow). Every other peer, right before the click's order point, calls the same toggleComponentActivation on its copy of the
// component with that state, and rollFail's result forced to the clicker's (a Priority.Last postfix; the roll still draws, so later draws in the
// toggle stay in step). Installed at the first combat, after CAE's Init.
// Not covered here, on purpose: CAEAIHelper.AIActivatableProc runs from CAE's AbstractActor.OnActivationEnd and OnNewRound prefixes on every peer
// (both peers share LocalPlayerTeamGuid, so both treat the same units as AI), so it is not a one-peer decision; its unseeded draws are an RNG item.
// Rows (CoopDecisionChannel): cae-coop|decide|toggle|<actor>|uid=<uid>,on=<0/1>,roll=<0/1/~>, the same with |apply|, and |MISMATCH when the replay
// ended in another state.
// Until the click's order point the other peers hold the old state, so the clicking peer's MECHS compare (MechStatsReporter.OnTrigger, read through
// StatCollection.GetStatKeyValues) reports, for each stat of the unit's mech, component and pilot collections the toggle changed, its value from
// before the click while the stat still holds the toggle's value (as CustomAmmoCategoriesCoopModeSync does for weapon modes). The mask ends when the
// click's record reaches its order point on this peer (CoopDecisionChannel.Ordered) or the combat changes. L2-d (logic-post4b-load-1): the guest's
// Coolant Pod toggle reached the host 7.2 s later and the MECHS compare between them failed on CAEAIFailRollPassed and HeatSinkCapacity.
// Rows: cae-coop|pending|<actor>|stats=<n> at the click, cae-coop|masked|<actor>|stats=<n>|drift=<n> in a compare (drift: a masked stat that has
// moved since the toggle, reported as it is), cae-coop|ordered|<actor>.
// shortcut: compare fields that are not stats (Current Heat, Is Operational, ...) are not masked; a failed activation's damage moves stats only,
// but upgrade if a leg shows a core field differing in this window.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomActivatableEquipmentCoopToggle
    {
        const string Tag = "cae-coop";
        static Action<string> log;
        static CoopDecisionChannel channel;
        static MethodInfo toggle, isActivated;
        static object recording;      // the component whose toggle this peer is recording
        static string recordedRoll;
        static object replayComponent;
        static bool? forcedRoll;
        static string replayRoll;
        const string Absent = "~";
        static MethodInfo keyValues;
        static int inCompare, maskRows;
        static object maskCombat;
        // Per clicked unit: per stat collection, stat -> { value before the first pending click, value after the last }.
        static readonly Dictionary<object, Dictionary<object, Dictionary<string, string[]>>> masks = new Dictionary<object, Dictionary<object, Dictionary<string, string[]>>>(new SameObject());

        sealed class Rec { public string State; public Dictionary<object, Dictionary<string, string>> Before; }

        sealed class SameObject : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("CustomActivatableEquipment", a => ModAssemblyHook.AtFirstCombat(() => Apply(a), logLine, Tag), logLine, Tag);
        }

        static void Apply(Assembly cae)
        {
            if (!CoopDecisionChannel.Init(log, Tag)) return;
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type component = ModAssemblyHook.GameType("BattleTech.MechComponent");
            Type activatable = cae.GetType("CustomActivatableEquipment.ActivatableComponent", false);
            toggle = activatable?.GetMethod("toggleComponentActivation", st, null, new[] { component }, null);
            isActivated = activatable?.GetMethod("isComponentActivated", st, null, new[] { component }, null);
            MethodInfo rollFail = activatable?.GetMethod("rollFail", st, null, new[] { component, typeof(bool), typeof(bool) }, null);
            if (toggle == null || isActivated == null || rollFail == null)
            {
                log(Tag + "|skipped|shape changed ActivatableComponent.toggleComponentActivation=" + (toggle != null) + " isComponentActivated=" + (isActivated != null) + " rollFail=" + (rollFail != null));
                return;
            }
            channel = new CoopDecisionChannel("cae-toggle", Tag, log);
            channel.On("toggle", Replay);
            channel.Ordered = (kind, actor) => { if (kind == "toggle" && masks.Remove(actor)) log(Tag + "|ordered|" + CoopDecisionChannel.Get(actor, "GUID")); };
            MethodInfo trigger = ModAssemblyHook.GameType("BattleTech.MechStatsReporter")?.GetMethod("OnTrigger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            keyValues = ModAssemblyHook.GameType("BattleTech.StatCollection")?.GetMethod("GetStatKeyValues", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            try
            {
                var h = new Harmony("battletech.port.cae-toggle-coop");
                CoopDecisionChannel.Patch(h, toggle, prefix: Mine(nameof(TogglePrefix), Priority.First), finalizer: Mine(nameof(ToggleFinalizer), Priority.Last));
                CoopDecisionChannel.Patch(h, rollFail, postfix: Mine(nameof(RollFailPostfix), Priority.Last));
                string compare;
                if (trigger == null || keyValues == null)
                    compare = "ERROR pending compare not installed (MechStatsReporter.OnTrigger=" + (trigger != null) + " GetStatKeyValues=" + (keyValues != null) + ")";
                else
                {
                    CoopDecisionChannel.Patch(h, trigger, prefix: Mine(nameof(CompareEnter), Priority.First), finalizer: Mine(nameof(CompareExit), Priority.Last));
                    CoopDecisionChannel.Patch(h, keyValues, postfix: Mine(nameof(ReportPostfix), Priority.Last));
                    compare = "the MECHS compare reads a toggled unit's pre-click stats until its order point";
                }
                channel.Install("HUD toggleComponentActivation sent at once and replayed with the clicker's fail roll; " + compare);
            }
            catch (Exception e) { log(Tag + "|FAILED|cae-toggle install|" + e.Message); }
        }

        static HarmonyMethod Mine(string name, int priority)
            => new HarmonyMethod(typeof(CustomActivatableEquipmentCoopToggle).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };

        static bool Active(object component) => (bool)isActivated.Invoke(null, new[] { component });

        static void TogglePrefix(object component, ref object __state)
        {
            __state = null;
            if (channel.Replaying || recording != null) return;
            try
            {
                object actor = CoopDecisionChannel.Get(component, "parent");
                if (actor == null || !CoopDecisionChannel.CoopCombat() || CoopDecisionChannel.InInvocation() || !CoopDecisionChannel.Authority(actor)) return;
                recording = component;
                recordedRoll = "~";
                __state = new Rec { State = CoopDecisionChannel.RandomState(), Before = Snapshot(actor) };
            }
            catch (Exception e) { log(Tag + "|decide-failed|toggle|" + e.Message); }
        }

        // The stat collections the MECHS compare reads for this unit: the mech's, each component's, the pilot's.
        static IEnumerable<object> Collections(object actor)
        {
            yield return CoopDecisionChannel.Get(actor, "StatCollection");
            foreach (object c in CoopDecisionChannel.Get(actor, "allComponents") as IEnumerable ?? new object[0]) yield return CoopDecisionChannel.Get(c, "StatCollection");
            yield return CoopDecisionChannel.Get(CoopDecisionChannel.Get(actor, "pilot"), "StatCollection");
        }

        // Called outside a compare, so the mask does not apply to what it reads.
        static Dictionary<object, Dictionary<string, string>> Snapshot(object actor)
        {
            var all = new Dictionary<object, Dictionary<string, string>>(new SameObject());
            if (keyValues == null) return all;
            foreach (object stats in Collections(actor))
                if (stats != null && !all.ContainsKey(stats))
                    all[stats] = new Dictionary<string, string>((Dictionary<string, string>)keyValues.Invoke(stats, null), StringComparer.Ordinal);
            return all;
        }

        // After a recorded toggle: add the stats it changed to the unit's mask; a stat back at its pre-click value leaves it.
        static void Mask(object actor, Dictionary<object, Dictionary<string, string>> before)
        {
            object combat = CoopDecisionChannel.Combat();
            if (!ReferenceEquals(combat, maskCombat)) { masks.Clear(); maskCombat = combat; }
            if (!masks.TryGetValue(actor, out var unit)) masks[actor] = unit = new Dictionary<object, Dictionary<string, string[]>>(new SameObject());
            foreach (var kv in Snapshot(actor))
            {
                before.TryGetValue(kv.Key, out var was);
                was = was ?? new Dictionary<string, string>();
                var keys = new List<string>(kv.Value.Keys);
                foreach (string key in was.Keys) if (!kv.Value.ContainsKey(key)) keys.Add(key);
                foreach (string key in keys)
                {
                    string pre = was.TryGetValue(key, out string a) ? a : Absent, post = kv.Value.TryGetValue(key, out string b) ? b : Absent;
                    if (pre == post) continue;
                    if (!unit.TryGetValue(kv.Key, out var stats)) unit[kv.Key] = stats = new Dictionary<string, string[]>(StringComparer.Ordinal);
                    if (stats.TryGetValue(key, out string[] m)) m[1] = post;   // an earlier pending click keeps its pre-click value
                    else stats[key] = new[] { pre, post };
                    if (stats[key][0] == stats[key][1]) stats.Remove(key);
                }
            }
            int n = 0;
            foreach (var s in unit.Values) n += s.Count;
            if (n == 0) masks.Remove(actor);
            log(Tag + "|pending|" + CoopDecisionChannel.Get(actor, "GUID") + "|stats=" + n);
        }

        static void CompareEnter() { inCompare++; }

        static void CompareExit() { if (inCompare > 0) inCompare--; }

        // Inside a MECHS compare: a stat the pending toggle changed reports its pre-click value while it still holds the toggle's.
        static void ReportPostfix(object __instance, Dictionary<string, string> __result)
        {
            if (inCompare == 0 || masks.Count == 0 || __result == null) return;
            try
            {
                if (!ReferenceEquals(CoopDecisionChannel.Combat(), maskCombat)) { masks.Clear(); return; }
                foreach (var unit in masks)
                {
                    if (!unit.Value.TryGetValue(__instance, out var stats)) continue;
                    int masked = 0, drift = 0;
                    foreach (var kv in stats)
                    {
                        string now = __result.TryGetValue(kv.Key, out string v) ? v : Absent;
                        if (now != kv.Value[1]) { drift++; continue; }
                        if (kv.Value[0] == Absent) __result.Remove(kv.Key);
                        else __result[kv.Key] = kv.Value[0];
                        masked++;
                    }
                    if (++maskRows <= 40) log(Tag + "|masked|" + CoopDecisionChannel.Get(unit.Key, "GUID") + "|stats=" + masked + "|drift=" + drift);
                    return;
                }
            }
            catch (Exception e) { if (++maskRows <= 40) log(Tag + "|mask-failed|" + e.Message); }
        }

        static void ToggleFinalizer(object component, object __state)
        {
            if (!(__state is Rec r)) return;
            recording = null;
            try
            {
                string uid = CoopDecisionChannel.Get(component, "uid") as string;
                string on = CoopDecisionChannel.B(Active(component));
                object actor = CoopDecisionChannel.Get(component, "parent");
                channel.Decide("toggle", actor, "uid=" + uid + ",on=" + on + ",roll=" + recordedRoll, r.State, uid, on, recordedRoll);
                Mask(actor, r.Before);
                channel.SendNow();
            }
            catch (Exception e) { log(Tag + "|decide-failed|toggle|" + e.Message); }
        }

        static void RollFailPostfix(object component, bool testRoll, ref bool __result)
        {
            if (testRoll) return;
            if (recording != null && ReferenceEquals(component, recording)) recordedRoll = CoopDecisionChannel.B(__result);
            if (channel.Replaying && ReferenceEquals(component, replayComponent))
            {
                if (forcedRoll.HasValue) __result = forcedRoll.Value;
                replayRoll = CoopDecisionChannel.B(__result);
            }
        }

        // fields: uid, on afterwards, roll (0/1/~).
        static string Replay(object actor, string[] f)
        {
            object component = null;
            foreach (object c in CoopDecisionChannel.Get(actor, "allComponents") as IEnumerable ?? new object[0])
                if (CoopDecisionChannel.Get(c, "uid") as string == f[0]) { component = c; break; }
            if (component == null) throw new InvalidOperationException("component uid " + f[0] + " not found");
            replayComponent = component;
            forcedRoll = f[2] == "~" ? (bool?)null : f[2] == "1";
            replayRoll = "~";
            try
            {
                toggle.Invoke(null, new[] { component });
                string on = CoopDecisionChannel.B(Active(component));
                return "uid=" + f[0] + ",on=" + on + ",roll=" + replayRoll + (on == f[1] && replayRoll == f[2] ? "" : "|MISMATCH");
            }
            finally { replayComponent = null; forcedRoll = null; }
        }
    }
}
