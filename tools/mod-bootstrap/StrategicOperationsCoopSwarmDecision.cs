// StrategicOperations battle-armor decisions made on one co-op peer (gh issue 24; Sam, 2026-11-22, D-20261122-6 = A).
// Two StrategicOperations paths decide for a battle-armor unit where only one peer runs them:
//  - its prefix on AITeam.makeInvocationFromOrders (Patches/AI_Patches.cs), where the AI thinks (the host): a swarming unit with no usable weapon
//    calls BattleArmorUtils.DismountBA directly (FetchRandomAdjacentHex picks the landing hex), a swarmed unit activates its deswarm ability with
//    Ability.Activate(unit, unit) directly (deswarm rolls, cluster damage, dismounts), and a swarming unit's melee picks its mounted location into
//    ModState.SwarmMeleeSequences before returning a MechMeleeInvocation;
//  - its prefix on CombatHUDButtonBase.OnClick for BTN_DoneWithMech on a swarming unit (Patches/BattleArmorPatches.cs), on the clicking peer: with
//    MeleeOnSwarmAttacks (BTA: true) it picks the location into SwarmMeleeSequences and publishes the MechMeleeInvocation.
// SwarmMeleeSequences is read when the MechMeleeInvocation resolves (MechMeleeSequence patch) on every peer, so a peer without the entry hit another
// location; the dismount and the ability ran on the host only.
// This adapter, only when an assembly named StrategicOperations is loaded and only in a co-op combat:
//  - records, on the authority inside makeInvocationFromOrders, each top-level DismountBA and Ability.Activate(AbstractActor, ICombatant) call
//    (arguments, UnityEngine.Random.state at its start); they ride the host's next invocation, and every other peer makes the same call with that
//    state right before it runs (StrategicOperations' own Mod.Random reads are seeded per read by StrategicOperationsCoopDeterminism on both peers);
//  - when the authority sends a MechMeleeInvocation for a unit with a SwarmMeleeSequences entry, sends that entry with it, and every other peer
//    writes it right before the invocation runs. That covers both the AI path and the HUD click, since both send the invocation.
// Installed at the first combat, after StrategicOperations' Init.
// Rows (CoopDecisionChannel): strategicops-coop|decide|dismount|<BA>|carrier=<guid>,pos=<x_y_z>, |decide|ability|<creator>|<def>,target=<guid>,active=<0/1>,
// |decide|melee-loc|<BA>|loc=<n>, the same with |apply|.
// Not covered: the swarming-unit HandleDeath the AI prefix calls for a unit flagged for death (every peer handles that death through its own flow),
// and the non-melee HUD branch (an AttackStackSequence added on the clicking peer only; off while MeleeOnSwarmAttacks is true).
using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class StrategicOperationsCoopSwarmDecision
    {
        const string Tag = "strategicops-coop";
        static Action<string> log;
        static CoopDecisionChannel channel;
        static MethodInfo dismount, activate;
        static FieldInfo meleeSequences;
        static Type vector3;
        static bool aiScope;
        static int depth;

        sealed class Rec { public object[] Args; public string State; }

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("StrategicOperations", a => ModAssemblyHook.AtFirstCombat(() => Apply(a), logLine, Tag), logLine, Tag);
        }

        static void Apply(Assembly so)
        {
            if (!CoopDecisionChannel.Init(log, Tag)) return;
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor"), combatant = ModAssemblyHook.GameType("BattleTech.ICombatant");
            Type order = ModAssemblyHook.GameType("OrderInfo");
            vector3 = ModAssemblyHook.GameType("UnityEngine.Vector3");
            dismount = so.GetType("StrategicOperations.Framework.BattleArmorUtils", false)?.GetMethod("DismountBA", st, null,
                new[] { actor, actor, vector3, typeof(bool), typeof(bool), typeof(bool), typeof(bool) }, null);
            activate = ModAssemblyHook.GameType("BattleTech.Ability")?.GetMethod("Activate", inst, null, new[] { actor, combatant }, null);
            meleeSequences = so.GetType("StrategicOperations.Framework.ModState", false)?.GetField("SwarmMeleeSequences", st);
            MethodInfo think = ModAssemblyHook.GameType("AITeam")?.GetMethod("makeInvocationFromOrders", inst, null, new[] { actor, order }, null);
            if (dismount == null || activate == null || meleeSequences == null || think == null || vector3 == null)
            {
                log(Tag + "|skipped|shape changed BattleArmorUtils.DismountBA=" + (dismount != null) + " Ability.Activate(AbstractActor,ICombatant)=" + (activate != null)
                    + " ModState.SwarmMeleeSequences=" + (meleeSequences != null) + " AITeam.makeInvocationFromOrders=" + (think != null));
                return;
            }
            channel = new CoopDecisionChannel("so-swarm", Tag, log);
            channel.On("dismount", ReplayDismount);
            channel.On("ability", ReplayAbility);
            channel.On("melee-loc", ReplayMeleeLocation);
            channel.BeforeCapture = MeleeLocation;
            try
            {
                var h = new Harmony("battletech.port.so-swarm-coop");
                CoopDecisionChannel.Patch(h, think, prefix: Mine(nameof(ThinkPrefix), Priority.First), finalizer: Mine(nameof(ThinkFinalizer), Priority.Last));
                CoopDecisionChannel.Patch(h, dismount, prefix: Mine(nameof(CallPrefix), Priority.First), finalizer: Mine(nameof(DismountFinalizer), Priority.Last));
                CoopDecisionChannel.Patch(h, activate, prefix: Mine(nameof(CallPrefix), Priority.First), finalizer: Mine(nameof(AbilityFinalizer), Priority.Last));
                channel.Install("AI DismountBA / Ability.Activate replayed; SwarmMeleeSequences sent with the authority's MechMeleeInvocation (AI and HUD)");
            }
            catch (Exception e) { log(Tag + "|FAILED|so-swarm install|" + e.Message); }
        }

        static HarmonyMethod Mine(string name, int priority)
            => new HarmonyMethod(typeof(StrategicOperationsCoopSwarmDecision).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };

        static void ThinkPrefix(object unit)
        {
            aiScope = false;
            try { aiScope = CoopDecisionChannel.CoopCombat() && CoopDecisionChannel.Authority(unit); }
            catch (Exception e) { log(Tag + "|decide-failed|think|" + e.Message); }
        }

        static void ThinkFinalizer() { aiScope = false; depth = 0; }

        // Only the outermost recorded call in the AI's think is a record: what it calls in turn replays with it.
        static void CallPrefix(object[] __args, ref object __state)
        {
            __state = null;
            if (!aiScope || depth > 0 || channel.Replaying) return;
            depth++;
            __state = new Rec { Args = (object[])__args.Clone(), State = CoopDecisionChannel.RandomState() };
        }

        static void DismountFinalizer(object __state)
        {
            if (!(__state is Rec r)) return;
            depth--;
            try
            {
                object ba = r.Args[0];
                string carrier = CoopDecisionChannel.Get(r.Args[1], "GUID") as string ?? "~";
                channel.Decide("dismount", ba, "carrier=" + carrier + ",pos=" + Vec(CoopDecisionChannel.Get(ba, "CurrentPosition")), r.State,
                    carrier, Vec(r.Args[2]), CoopDecisionChannel.B((bool)r.Args[3]), CoopDecisionChannel.B((bool)r.Args[4]), CoopDecisionChannel.B((bool)r.Args[5]), CoopDecisionChannel.B((bool)r.Args[6]));
            }
            catch (Exception e) { log(Tag + "|decide-failed|dismount|" + e.Message); }
        }

        static void AbilityFinalizer(object __instance, object __state)
        {
            if (!(__state is Rec r)) return;
            depth--;
            try
            {
                string def = CoopDecisionChannel.Get(CoopDecisionChannel.Get(__instance, "Def"), "Id") as string;
                string target = CoopDecisionChannel.Get(r.Args[1], "GUID") as string ?? "~";
                channel.Decide("ability", r.Args[0], def + ",target=" + target + ",active=" + CoopDecisionChannel.B(CoopDecisionChannel.Get(__instance, "IsActive") is bool a && a), r.State, def, target);
            }
            catch (Exception e) { log(Tag + "|decide-failed|ability|" + e.Message); }
        }

        // Sender: a MechMeleeInvocation for a unit this peer decides, with a SwarmMeleeSequences entry, carries that entry.
        static void MeleeLocation(object invocation)
        {
            if (invocation.GetType().Name != "MechMeleeInvocation" || !(CoopDecisionChannel.Get(invocation, "MechGUID") is string guid)) return;
            if (!(meleeSequences.GetValue(null) is IDictionary map) || !map.Contains(guid)) return;
            object actor = Find(guid);
            if (actor == null || !CoopDecisionChannel.Authority(actor)) return;
            string loc = Convert.ToString(map[guid], System.Globalization.CultureInfo.InvariantCulture);
            channel.Decide("melee-loc", actor, "loc=" + loc, "~", loc);
        }

        static string ReplayMeleeLocation(object actor, string[] f)
        {
            ((IDictionary)meleeSequences.GetValue(null))[CoopDecisionChannel.Get(actor, "GUID") as string] = CoopDecisionChannel.PI(f[0]);
            return "loc=" + f[0];
        }

        // fields: carrier, locationOverride, isFriendly, calledFromDeswarm, calledFromHandleDeath, unShrinkRep.
        static string ReplayDismount(object ba, string[] f)
        {
            object carrier = f[0] == "~" ? null : Find(f[0]);
            if (carrier == null && f[0] != "~") throw new InvalidOperationException("carrier " + f[0] + " not found");
            dismount.Invoke(null, new[] { ba, carrier, ParseVec(f[1]), (object)(f[2] == "1"), f[3] == "1", f[4] == "1", f[5] == "1" });
            return "carrier=" + f[0] + ",pos=" + Vec(CoopDecisionChannel.Get(ba, "CurrentPosition"));
        }

        // fields: ability def id, target.
        static string ReplayAbility(object creator, string[] f)
        {
            object ability = null;
            foreach (object list in new[] { CoopDecisionChannel.Get(CoopDecisionChannel.Get(creator, "pilot") ?? CallPilot(creator), "Abilities"), CoopDecisionChannel.Get(creator, "ComponentAbilities") })
            {
                foreach (object a in list as IEnumerable ?? new object[0])
                    if (CoopDecisionChannel.Get(CoopDecisionChannel.Get(a, "Def"), "Id") as string == f[0]) { ability = a; break; }
                if (ability != null) break;
            }
            if (ability == null) throw new InvalidOperationException("ability " + f[0] + " not found on the creator");
            object target = f[1] == "~" ? null : Find(f[1]);
            activate.Invoke(ability, new[] { creator, target });
            return f[0] + ",target=" + f[1] + ",active=" + CoopDecisionChannel.B(CoopDecisionChannel.Get(ability, "IsActive") is bool a2 && a2);
        }

        static object CallPilot(object actor) => actor?.GetType().GetMethod("GetPilot", Type.EmptyTypes)?.Invoke(actor, null);

        static object Find(string guid)
        {
            object combat = CoopDecisionChannel.Combat();
            return combat?.GetType().GetMethod("FindActorByGUID", new[] { typeof(string) })?.Invoke(combat, new object[] { guid });
        }

        static string Vec(object v)
            => v == null ? "0_0_0" : F(CoopDecisionChannel.Get(v, "x")) + "_" + F(CoopDecisionChannel.Get(v, "y")) + "_" + F(CoopDecisionChannel.Get(v, "z"));

        static string F(object f) => f is float x ? CoopDecisionChannel.F(x) : "0";

        static object ParseVec(string s)
        {
            string[] p = s.Split('_');
            return Activator.CreateInstance(vector3, new object[] { CoopDecisionChannel.PF(p[0]), CoopDecisionChannel.PF(p[1]), CoopDecisionChannel.PF(p[2]) });
        }
    }
}
