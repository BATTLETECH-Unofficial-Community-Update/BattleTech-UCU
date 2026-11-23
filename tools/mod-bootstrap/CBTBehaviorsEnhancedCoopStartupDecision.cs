// CBTBehaviorsEnhanced's AI startup checks in co-op (gh issue 24; Sam, 2026-11-22, D-20261122-6 = A).
// CBTBehaviorsEnhanced's postfix on MechStartUpNode.Tick (Patches/AI/MechStartUpNode_Tick.cs) runs where the AI thinks, the host only. When the
// AI's startup check fails it keeps the unit shut down and runs CheckHelper.ResolvePilotInjuryCheck, ResolveSystemFailureCheck (destroys a component
// picked with GetRandomElement), ResolveRegularAmmoCheck and ResolveVolatileAmmoCheck (ammo explosions) on the host, each rolling through
// CheckHelper.DidCheckPassThreshold (Combat.NetworkRandom). No invocation carries them, so the guest kept a component, an ammo box or pilot health
// the host lost. A passed check is not a one-peer decision: the AI then issues a MechStartupInvocation, whose CBTBE prefix runs on every peer.
// This adapter, only when an assembly named CBTBehaviorsEnhanced is loaded and only in a co-op combat, records each Resolve* call made inside a
// MechStartUpNode.Tick on the authority (the host for an AI unit; CoopDecisionChannel): its arguments, UnityEngine.Random.state at its start and
// every DidCheckPassThreshold result inside it. The records ride the host's next invocation (the AI's order for the unit). Every other peer, right
// before that invocation, makes the same Resolve* call with the same arguments and state, each DidCheckPassThreshold forced to the host's result
// (the original is skipped, so the receiver draws no NetworkRandom the host drew while thinking). Installed at the first combat, after CBTBE's Init.
// Rows (CoopDecisionChannel): cbtbe-coop|decide|<check>|<mech>|<returned 0/1>:<threshold results>, the same with |apply|, and a |MISMATCH suffix
// when a replay consumed other threshold results than were recorded.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CBTBehaviorsEnhancedCoopStartupDecision
    {
        const string Tag = "cbtbe-coop";
        static readonly string[] Kinds = { "injury", "system", "ammo", "volatile" };
        static readonly string[] Names = { "ResolvePilotInjuryCheck", "ResolveSystemFailureCheck", "ResolveRegularAmmoCheck", "ResolveVolatileAmmoCheck" };
        static Action<string> log;
        static CoopDecisionChannel channel;
        static readonly MethodInfo[] resolve = new MethodInfo[4];
        static object tickUnit;            // the authority's unit whose MechStartUpNode.Tick is running
        static int depth;
        static List<string> bits;          // DidCheckPassThreshold results inside the recorded call
        static Queue<bool> forced;         // replay: the recorded results, in order
        static int consumed;

        sealed class Rec { public int Index; public object[] Args; public string State; }

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("CBTBehaviorsEnhanced", a => ModAssemblyHook.AtFirstCombat(() => Apply(a), logLine, Tag), logLine, Tag);
        }

        static void Apply(Assembly cbtbe)
        {
            if (!CoopDecisionChannel.Init(log, Tag)) return;
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type helper = cbtbe.GetType("CBTBehaviorsEnhanced.CheckHelper", false);
            Type mech = ModAssemblyHook.GameType("BattleTech.Mech");
            resolve[0] = helper?.GetMethod(Names[0], st, null, new[] { mech, typeof(int), typeof(int), typeof(int), typeof(float) }, null);
            for (int i = 1; i < 4; i++) resolve[i] = helper?.GetMethod(Names[i], st, null, new[] { mech, typeof(int), typeof(int), typeof(float) }, null);
            var thresholds = new List<MethodInfo>();
            foreach (MethodInfo m in helper?.GetMethods(st) ?? new MethodInfo[0])
                if (m.Name == "DidCheckPassThreshold" && m.ReturnType == typeof(bool)) thresholds.Add(m);
            MethodInfo tick = ModAssemblyHook.GameType("MechStartUpNode")?.GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            if (Array.IndexOf(resolve, null) >= 0 || thresholds.Count != 2 || tick == null)
            {
                log(Tag + "|skipped|shape changed CheckHelper.Resolve*=" + (Array.IndexOf(resolve, null) < 0) + " DidCheckPassThreshold overloads=" + thresholds.Count + " MechStartUpNode.Tick=" + (tick != null));
                return;
            }
            channel = new CoopDecisionChannel("cbtbe-startup", Tag, log);
            for (int i = 0; i < 4; i++) { int index = i; channel.On(Kinds[i], (actor, f) => Replay(index, actor, f)); }
            try
            {
                var h = new Harmony("battletech.port.cbtbe-startup-coop");
                CoopDecisionChannel.Patch(h, tick, prefix: Mine(nameof(TickPrefix), Priority.First), finalizer: Mine(nameof(TickFinalizer), Priority.Last));
                foreach (MethodInfo m in resolve) CoopDecisionChannel.Patch(h, m, prefix: Mine(nameof(ResolvePrefix), Priority.First), finalizer: Mine(nameof(ResolveFinalizer), Priority.Last));
                foreach (MethodInfo m in thresholds) CoopDecisionChannel.Patch(h, m, prefix: Mine(nameof(ThresholdPrefix), Priority.First), postfix: Mine(nameof(ThresholdPostfix), Priority.Last));
                channel.Install("AI startup-check outcomes (MechStartUpNode.Tick -> CheckHelper.Resolve*) carried with the host's next invocation");
            }
            catch (Exception e) { log(Tag + "|FAILED|cbtbe-startup install|" + e.Message); }
        }

        static HarmonyMethod Mine(string name, int priority)
            => new HarmonyMethod(typeof(CBTBehaviorsEnhancedCoopStartupDecision).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };

        static void TickPrefix(object __instance)
        {
            tickUnit = null;
            try
            {
                object unit = CoopDecisionChannel.Get(__instance, "unit");
                if (unit != null && CoopDecisionChannel.CoopCombat() && CoopDecisionChannel.Authority(unit)) tickUnit = unit;
            }
            catch (Exception e) { log(Tag + "|decide-failed|tick|" + e.Message); }
        }

        static void TickFinalizer() { tickUnit = null; depth = 0; }

        static void ResolvePrefix(MethodBase __originalMethod, object[] __args, ref object __state)
        {
            __state = null;
            if (tickUnit == null || depth > 0 || channel.Replaying) return;
            depth++;
            bits = new List<string>();
            __state = new Rec { Index = Array.IndexOf(resolve, __originalMethod as MethodInfo), Args = (object[])__args.Clone(), State = CoopDecisionChannel.RandomState() };
        }

        static void ResolveFinalizer(object __state, bool __result)
        {
            if (!(__state is Rec r)) return;
            depth--;
            try
            {
                if (r.Index < 0) { log(Tag + "|decide-failed|unknown Resolve method"); return; }
                string results = string.Join("", bits.ToArray());
                var fields = new List<string>();
                for (int i = 1; i < r.Args.Length; i++)
                    fields.Add(r.Args[i] is float f ? CoopDecisionChannel.F(f) : Convert.ToString(r.Args[i], System.Globalization.CultureInfo.InvariantCulture));
                fields.Add(results.Length == 0 ? "~" : results);
                channel.Decide(Kinds[r.Index], r.Args[0], CoopDecisionChannel.B(__result) + ":" + results, r.State, fields.ToArray());
            }
            catch (Exception e) { log(Tag + "|decide-failed|" + Kinds[Math.Max(0, r.Index)] + "|" + e.Message); }
        }

        static bool ThresholdPrefix(ref bool __result)
        {
            if (!channel.Replaying || forced == null) return true;
            if (forced.Count == 0) { consumed = -1; return true; }   // more checks than recorded: let it roll, mark the mismatch
            __result = forced.Dequeue();
            consumed++;
            return false;
        }

        static void ThresholdPostfix(bool __result)
        {
            if (depth > 0 && bits != null && !channel.Replaying) bits.Add(CoopDecisionChannel.B(__result));
        }

        // fields: heatToCheck, rootSequenceGUID, [sequenceGUID,] heatCheck, threshold results.
        static string Replay(int index, object actor, string[] f)
        {
            string results = f[f.Length - 1] == "~" ? "" : f[f.Length - 1];
            forced = new Queue<bool>();
            foreach (char c in results) forced.Enqueue(c == '1');
            consumed = 0;
            object[] args = index == 0
                ? new object[] { actor, CoopDecisionChannel.PI(f[0]), CoopDecisionChannel.PI(f[1]), CoopDecisionChannel.PI(f[2]), CoopDecisionChannel.PF(f[3]) }
                : new object[] { actor, CoopDecisionChannel.PI(f[0]), CoopDecisionChannel.PI(f[1]), CoopDecisionChannel.PF(f[2]) };
            try
            {
                bool returned = (bool)resolve[index].Invoke(null, args);
                bool match = consumed == results.Length && forced.Count == 0;
                return CoopDecisionChannel.B(returned) + ":" + results + (match ? "" : "|MISMATCH consumed=" + consumed);
            }
            finally { forced = null; }
        }
    }
}
