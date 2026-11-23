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
// shortcut: until the click's order point the clicking peer's MECHS compare sees the new state and the others the old one (CustomAmmoCategoriesCoopModeSync
// masks that window for weapon modes); upgrade if a leg shows an activation compare inside that window.
using System;
using System.Collections;
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

        sealed class Rec { public string State; }

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
            try
            {
                var h = new Harmony("battletech.port.cae-toggle-coop");
                CoopDecisionChannel.Patch(h, toggle, prefix: Mine(nameof(TogglePrefix), Priority.First), finalizer: Mine(nameof(ToggleFinalizer), Priority.Last));
                CoopDecisionChannel.Patch(h, rollFail, postfix: Mine(nameof(RollFailPostfix), Priority.Last));
                channel.Install("HUD toggleComponentActivation sent at once and replayed with the clicker's fail roll");
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
                __state = new Rec { State = CoopDecisionChannel.RandomState() };
            }
            catch (Exception e) { log(Tag + "|decide-failed|toggle|" + e.Message); }
        }

        static void ToggleFinalizer(object component, object __state)
        {
            if (!(__state is Rec r)) return;
            recording = null;
            try
            {
                string uid = CoopDecisionChannel.Get(component, "uid") as string;
                string on = CoopDecisionChannel.B(Active(component));
                channel.Decide("toggle", CoopDecisionChannel.Get(component, "parent"), "uid=" + uid + ",on=" + on + ",roll=" + recordedRoll, r.State, uid, on, recordedRoll);
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
