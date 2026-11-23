// StrategicOperations command abilities and in-flight command state at a co-op resync or rejoin load (gh issue 24 gap review, 2026-11-22:
// a gap that a resync landing on it does not degrade cleanly is fixed, at minimum by a guard that logs a row and drops the state).
// ModState.CommandAbilities (the strafe and spawn-turret Ability objects) is built only at TurnDirector.StartFirstRound (SO.cs:6780-6800)
// and emptied by ModState.ResetAll at OnCombatGameDestroyed; a guest's snapshot load does not run StartFirstRound, so its list stays empty,
// and SO's Team.ActivateAbility prefix then skips every command ability on the guest (SO.cs:6498-6530): the host spawns or strafes alone.
// The host sends each command ability's def id, CurrentCooldown, NumUsesLeft and ActivationsSinceActivated; the guest makes it as SO does
// (new Ability(def), Init(combat)), writes those three and refills CommandAbilities in place, in the host's order.
// Dropped on the host (guard), each with a `so-cmd-coop|gap-dropped|<field>|n=` row, because a guest cannot be given them:
// - a strafe in flight: its TB_StrafeSequence runs from a TurnEvent, and TurnDirector.InitFromSave starts a loaded game with no TurnEvents
//   (TurnDirector.cs:395), so the guest never runs it; the host removes those TurnEvents and ModState.PendingStrafeWaves (later waves).
//   The strafing unit already spawned stays on both peers (it is in the snapshot).
// - DeferredInvokeSpawns (a spawn-turret use waiting for the player's next activation, invoked by SO's IncrementActiveTurnActor prefix,
//   SO.cs:6662-6680): closures over the host's objects, keyed by a string whose positions are rounded (Vector3.ToString), so they cannot
//   be made again exactly on the guest.
// - DeferredInvokeBattleArmor: consumed at StartFirstRound (SO.cs:6770), so non-empty only if a resync lands before the first round.
// Refund (super-orchestrator, 2026-11-22, option B): a dropped strafe or spawn gives its use back. A prefix and postfix on every
// Ability.Activate overload and on SO's Utils.ActivateStrafeFromActor / ActivateSpawnTurretFromActor (installed at the first combat) record
// each use: the ability, NumUsesLeft and CurrentCooldown before and after, and the PendingStrafeWaves / DeferredInvokeSpawns keys it added.
// The host sets a dropped use's ability back to its values before the use, and the carry below then sends those values to the guest, so both
// peers end equal; each peer logs `so-cmd-coop|refund|<abilityDefId>|uses=<before>-><after>|cooldown=<before>-><after>`. Nothing is
// guessed: no recorded use, a later use of the same ability that was not dropped, NumUsesLeft changed since the use, or an ability outside
// ModState.CommandAbilities (the guest's copy would not follow) logs `so-cmd-coop|refund-failed|...` and leaves the values. Battle armor
// placed at the first round is no ability use (refund-failed row).
// Cost (super-orchestrator, 2026-11-22: "the player loses nothing for good"): a use's cost is rolled back with it. SO takes it in two
// places: at the use, DeploymentAssetsStats[ID].contractUses-- and, when consumeOnUse, the company stat CmdUseStat.stat -1 through
// CompanyStats.ModifyStat("StratOps", ...) (SO.cs:4524-4535 spawn turret, 4723-4733 strafe); and at contract end, c-bills, from
// CommandUses: Contract.CompleteContract postfix MoneyResults -= sum(CmdUseInfo.TotalCost = UseCount * UseCostAdjusted) (SO.cs:4201-4226).
// CommandUses gets +1 per strafe wave flown (Utils.InitiateStrafe turret path, CustomSpawner.OnStrafeDepsLoaded mech/vehicle path) and per
// turret spawned (SO.cs:4622-4650, when the deferred spawn runs). No c-bills are taken at use time, so the c-bill refund is the UseCount
// decrement. The use hooks diff those three on every recorded use, and InitiateStrafe / OnStrafeDepsLoaded add their diffs to the use that
// owns the strafe key (outermost hook only, so nothing is counted twice). The rollback is made only when every entry is found once and no
// count goes below zero, else `refund-failed|cost|<reason>` and no cost value changes. Host only: contract money and company stats belong
// to the host's career (a client has no payout of its own, CampaignSessionHost.cs:993; its Simulation is the host's snapshot), and
// CommandUses / DeploymentAssetsStats reach the guest through the so-coop carry, which this source is registered ahead of (ModBootstrap),
// so the host's rollback is made before so-coop captures them.
// Other mods' TurnEvents are not dropped: they are counted in `so-cmd-coop|turn-events-not-carried|n=` (the base game carries no TurnEvent
// through a load).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace BattleTechPort
{
    public static class StrategicOperationsCoopCommandSync
    {
        const string Tag = "so-cmd-coop";
        static Action<string> log;
        static FieldInfo commandAbilities, pendingStrafes, deferredSpawns, deferredBattleArmor;
        static PropertyInfo battleTechGame, abilityDef, cooldown, usesLeft, activations, turnEvents, eventSequence;
        static ConstructorInfo newAbility;
        static MethodInfo init;
        static CoopCarryCounter counter;
        static int rows;
        // Recorded uses, per ability instance, in order (this combat only).
        sealed class Use
        {
            public object Ability; public int CdBefore, UsesBefore, CdAfter, UsesAfter; public List<string> Keys = new List<string>();
            public Dictionary<string, int> Cmd = new Dictionary<string, int>(), Contract = new Dictionary<string, int>(), Company = new Dictionary<string, int>();
            public Dictionary<string, bool> Created = new Dictionary<string, bool>();
        }
        sealed class Costs { public Dictionary<string, int> Cmd = new Dictionary<string, int>(), Contract = new Dictionary<string, int>(), Company = new Dictionary<string, int>(); }
        sealed class Snap { public int Cd, Uses; public Dictionary<string, bool> Keys; public Costs Cost; public object Ability; public string StrafeKey; }
        static int depth;
        static FieldInfo commandUses, deploymentStats;
        sealed class Same : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
        static readonly Dictionary<object, List<Use>> uses = new Dictionary<object, List<Use>>(new Same());
        static object usesCombat;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("StrategicOperations", Apply, logLine, Tag);
        }

        static void Apply(Assembly so)
        {
            try
            {
                const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type modState = so.GetType("StrategicOperations.Framework.ModState", false);
                Type ability = ModAssemblyHook.GameType("BattleTech.Ability");
                Type def = ModAssemblyHook.GameType("BattleTech.AbilityDef");
                Type combat = ModAssemblyHook.GameType("BattleTech.CombatGameState");
                commandAbilities = modState?.GetField("CommandAbilities", st);
                pendingStrafes = modState?.GetField("PendingStrafeWaves", st);
                deferredSpawns = modState?.GetField("DeferredInvokeSpawns", st);
                deferredBattleArmor = modState?.GetField("DeferredInvokeBattleArmor", st);
                commandUses = modState?.GetField("CommandUses", st);
                deploymentStats = modState?.GetField("DeploymentAssetsStats", st);
                battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", st);
                abilityDef = ability?.GetProperty("Def", inst);
                cooldown = ability?.GetProperty("CurrentCooldown", inst);
                usesLeft = ability?.GetProperty("NumUsesLeft", inst);
                activations = ability?.GetProperty("ActivationsSinceActivated", inst);
                newAbility = def == null ? null : ability?.GetConstructor(new[] { def });
                init = combat == null ? null : ability?.GetMethod("Init", inst, null, new[] { combat }, null);
                turnEvents = ModAssemblyHook.GameType("BattleTech.TurnDirector")?.GetProperty("TurnEvents", inst);
                eventSequence = ModAssemblyHook.GameType("BattleTech.TurnEvent")?.GetProperty("EventSequence", inst);
                MethodInfo register = ModAssemblyHook.GameType("BattleTech.CoopInvocationSideData")?.GetMethod("Register", st);
                var missing = new List<string>();
                foreach (KeyValuePair<string, object> m in new Dictionary<string, object> {
                    { "ModState.CommandAbilities", commandAbilities }, { "ModState.PendingStrafeWaves", pendingStrafes }, { "ModState.DeferredInvokeSpawns", deferredSpawns },
                    { "ModState.DeferredInvokeBattleArmor", deferredBattleArmor }, { "ModState.CommandUses", commandUses }, { "ModState.DeploymentAssetsStats", deploymentStats },
                    { "CmdUseInfo.UnitID/UseCount/TotalCost", Members(so, "StrategicOperations.Framework.Classes+CmdUseInfo", "UnitID", "UseCount", "TotalCost") },
                    { "CmdUseStat.ID/contractUses/stat", Members(so, "StrategicOperations.Framework.Classes+CmdUseStat", "ID", "contractUses", "stat") },
                    { "StatCollection.GetValue<T>/ModifyStat<T>", StatMethods() }, { "UnityGameInstance.BattleTechGame", battleTechGame }, { "Ability.Def", abilityDef },
                    { "Ability.CurrentCooldown", cooldown?.GetSetMethod(true) }, { "Ability.NumUsesLeft", usesLeft?.GetSetMethod(true) },
                    { "Ability.ActivationsSinceActivated", activations?.GetSetMethod(true) }, { "Ability(AbilityDef)", newAbility }, { "Ability.Init(CombatGameState)", init },
                    { "TurnDirector.TurnEvents", turnEvents }, { "TurnEvent.EventSequence", eventSequence }, { "CoopInvocationSideData.Register", register } })
                    if (m.Value == null) missing.Add(m.Key);
                if (missing.Count > 0) { log(Tag + "|FAILED|shape changed, not found: " + string.Join(", ", missing.ToArray()) + " (needs UCU patch 166)"); return; }
                counter = CoopCarryCounter.For(Tag, log);
                // Ability and SO methods are patched only at the first combat, after the mods' Init (fault M).
                ModAssemblyHook.AtFirstCombat(() => PatchUses(ability, so), log, Tag);
                register.Invoke(null, new object[] { Tag, new Func<object, string>(Capture), new Action<object, string>(ApplyData) });
                log(Tag + "|installed|StrategicOperations CommandAbilities made again on a peer that loads a co-op snapshot; strafes in flight and deferred spawns dropped on the host, their uses refunded on both peers");
            }
            catch (Exception e) { log(Tag + "|FAILED|" + ModStateCarry.Inner(e)); }
        }

        static object Combat() => ModStateCarry.Get(battleTechGame.GetValue(null, null), "Combat");

        // Host, at the peer's snapshot load: the guard drops, then the command abilities.
        static string Capture(object invocation)
        {
            if (invocation != null) return null;
            try
            {
                object combat = Combat();
                if (combat == null) return null;
                List<string> refunds = Guard(combat);
                var sb = new StringBuilder();
                var ids = new List<string>();
                foreach (object a in commandAbilities.GetValue(null) as IEnumerable ?? new object[0])
                {
                    string id = ModStateCarry.Get(abilityDef.GetValue(a, null), "Id") as string;
                    if (string.IsNullOrEmpty(id) || id.IndexOf('\t') >= 0 || id.IndexOf('\n') >= 0) { Row("unmapped", "ability-id|" + id); continue; }
                    sb.Append(id).Append('\t').Append(I(cooldown.GetValue(a, null))).Append('\t').Append(I(usesLeft.GetValue(a, null))).Append('\t')
                        .Append(I(activations.GetValue(a, null))).Append('\n');
                    ids.Add(id + ":" + I(cooldown.GetValue(a, null)) + "/" + I(usesLeft.GetValue(a, null)) + "/" + I(activations.GetValue(a, null)));
                }
                foreach (string r in refunds) sb.Append("R\t").Append(r.Replace('\t', ' ').Replace('\n', ' ')).Append('\n');
                counter.Capture++;
                if (ids.Count > 0) counter.NonEmpty++;
                Row("capture", "abilities=" + ids.Count + "|" + string.Join(";", ids.ToArray()));
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
            }
            catch (Exception e) { Row("FAILED", "capture|" + ModStateCarry.Inner(e)); return null; }
        }

        static List<string> Guard(object combat)
        {
            var refunds = new List<string>();
            var dropped = new List<string>();
            object director = ModStateCarry.Get(combat, "TurnDirector");
            var events = director == null ? null : turnEvents.GetValue(director, null) as IList;
            int strafeEvents = 0, other = 0;
            var otherTypes = new List<string>();
            if (events != null)
                for (int i = events.Count - 1; i >= 0; i--)
                {
                    object seq = events[i] == null ? null : eventSequence.GetValue(events[i], null);
                    if (seq != null && seq.GetType().Name == "TB_StrafeSequence") { events.RemoveAt(i); strafeEvents++; }
                    else { other++; otherTypes.Add(seq == null ? "null" : seq.GetType().FullName); }
                }
            var waves = pendingStrafes.GetValue(null) as IDictionary;
            int nWaves = waves?.Count ?? 0;
            if (strafeEvents > 0 || nWaves > 0)
            {
                var keys = new List<string>();
                if (waves != null) foreach (object k in waves.Keys) keys.Add(k as string ?? "?");
                waves?.Clear();
                Row("gap-dropped", "PendingStrafeWaves|n=" + nWaves + "|turn-events=" + strafeEvents + "|" + string.Join(";", keys.ToArray()));
                dropped.AddRange(keys);
                if (nWaves == 0) Fail(refunds, "PendingStrafeWaves|turn-events=" + strafeEvents + "|no pending wave names the use");
            }
            dropped.AddRange(Drop(deferredSpawns, "DeferredInvokeSpawns"));
            foreach (string k in Drop(deferredBattleArmor, "DeferredInvokeBattleArmor"))
                Fail(refunds, "DeferredInvokeBattleArmor|" + k + "|no ability use (battle armor placed at the first round)");
            if (other > 0) Row("turn-events-not-carried", "n=" + other + "|" + string.Join(";", otherTypes.ToArray()));
            Refund(combat, dropped, refunds);
            return refunds;
        }

        static List<string> Drop(FieldInfo f, string name)
        {
            var keys = new List<string>();
            var list = f.GetValue(null) as IList;
            if (list == null || list.Count == 0) return keys;
            foreach (object kv in list) keys.Add(ModStateCarry.Get(kv, "Key") as string ?? "?");
            list.Clear();
            Row("gap-dropped", name + "|n=" + keys.Count + "|" + string.Join(";", keys.ToArray()));
            return keys;
        }

        static void Fail(List<string> refunds, string text)
        {
            string row = "refund-failed|" + text;
            refunds.Add(row);
            log(Tag + "|" + row);
        }

        static string DefId(object a) => ModStateCarry.Get(abilityDef.GetValue(a, null), "Id") as string ?? "?";
        static int Val(PropertyInfo p, object a) => Convert.ToInt32(p.GetValue(a, null), CultureInfo.InvariantCulture);

        // Sets each dropped use's ability back to its values before the earliest dropped use, only when that is exact.
        static void Refund(object combat, List<string> dropped, List<string> refunds)
        {
            if (dropped.Count == 0) return;
            if (!ReferenceEquals(combat, usesCombat)) { uses.Clear(); usesCombat = combat; }
            var byAbility = new Dictionary<object, List<Use>>(new Same());
            foreach (string key in dropped)
            {
                bool found = false;
                foreach (List<Use> list in uses.Values)
                    foreach (Use u in list)
                        if (u.Keys.Contains(key))
                        {
                            found = true;
                            if (!byAbility.TryGetValue(u.Ability, out List<Use> l)) byAbility[u.Ability] = l = new List<Use>();
                            if (!l.Contains(u)) l.Add(u);
                        }
                if (!found) Fail(refunds, key + "|no recorded use (made before the use hook, or by another path)");
            }
            var command = commandAbilities.GetValue(null) as IList;
            foreach (KeyValuePair<object, List<Use>> kv in byAbility)
            {
                object a = kv.Key;
                string id = DefId(a);
                List<Use> all = uses[a];
                int first = all.Count;
                foreach (Use u in kv.Value) first = Math.Min(first, all.IndexOf(u));
                bool suffix = true;
                for (int i = first; i < all.Count; i++) if (!kv.Value.Contains(all[i])) suffix = false;
                int usesNow = Val(usesLeft, a), cdNow = Val(cooldown, a);
                if (command == null || !command.Contains(a)) { Fail(refunds, id + "|not in ModState.CommandAbilities, so the guest's copy would not follow"); continue; }
                if (!suffix) { Fail(refunds, id + "|a later use of it was not dropped"); continue; }
                if (usesNow != all[all.Count - 1].UsesAfter) { Fail(refunds, id + "|NumUsesLeft changed since the use (now " + usesNow + ", after the use " + all[all.Count - 1].UsesAfter + ")"); continue; }
                Use from = all[first];
                string cost = RollBackCost(all.GetRange(first, all.Count - first), refunds, id);
                usesLeft.SetValue(a, from.UsesBefore, null);
                cooldown.SetValue(a, from.CdBefore, null);
                all.RemoveRange(first, all.Count - first);
                string row = "refund|" + id + "|uses=" + usesNow + "->" + from.UsesBefore + "|cooldown=" + cdNow + "->" + from.CdBefore + cost;
                refunds.Add(row);
                log(Tag + "|" + row);
            }
        }

        static object Members(Assembly so, string type, params string[] names)
        {
            Type t = so.GetType(type, false);
            if (t == null) return null;
            foreach (string n in names)
                if (t.GetField(n, inst) == null && t.GetProperty(n, inst) == null) return null;
            return t;
        }
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static MethodInfo statGet, statModify;
        static object StatMethods()
        {
            Type t = ModAssemblyHook.GameType("BattleTech.StatCollection");
            if (t == null) return null;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!m.IsGenericMethodDefinition) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (m.Name == "GetValue" && ps.Length == 1 && ps[0].ParameterType == typeof(string)) statGet = m.MakeGenericMethod(typeof(int));
                if (m.Name == "ModifyStat" && ps.Length == 7 && ps[3].ParameterType.IsEnum) statModify = m.MakeGenericMethod(typeof(int));
            }
            return statGet == null || statModify == null ? null : t;
        }

        static object CompanyStats() => ModStateCarry.Get(ModStateCarry.Get(battleTechGame.GetValue(null, null), "Simulation"), "CompanyStats");
        static int Int(object o, string name) => Convert.ToInt32(ModStateCarry.Get(o, name), CultureInfo.InvariantCulture);

        static Costs ReadCosts()
        {
            var c = new Costs();
            foreach (object e in commandUses.GetValue(null) as IEnumerable ?? new object[0])
            {
                string id = ModStateCarry.Get(e, "UnitID") as string ?? "";
                if (!c.Cmd.ContainsKey(id)) c.Cmd[id] = Int(e, "UseCount");
            }
            object company = CompanyStats();
            foreach (object e in deploymentStats.GetValue(null) as IEnumerable ?? new object[0])
            {
                string id = ModStateCarry.Get(e, "ID") as string ?? "";
                if (!c.Contract.ContainsKey(id)) c.Contract[id] = Int(e, "contractUses");
                string stat = ModStateCarry.Get(e, "stat") as string;
                if (company != null && !string.IsNullOrEmpty(stat) && !c.Company.ContainsKey(stat)
                    && (bool)company.GetType().GetMethod("ContainsStatistic", new[] { typeof(string) }).Invoke(company, new object[] { stat }))
                    c.Company[stat] = (int)statGet.Invoke(company, new object[] { stat });
            }
            return c;
        }

        static void AddDiff(Dictionary<string, int> into, Dictionary<string, int> before, Dictionary<string, int> after, Dictionary<string, bool> created)
        {
            foreach (KeyValuePair<string, int> kv in after)
            {
                bool had = before.TryGetValue(kv.Key, out int b);
                if (!had && created != null) created[kv.Key] = true;
                int d = kv.Value - (had ? b : 0);
                if (d != 0) into[kv.Key] = (into.TryGetValue(kv.Key, out int x) ? x : 0) + d;
            }
            foreach (KeyValuePair<string, int> kv in before)
                if (!after.ContainsKey(kv.Key)) into[kv.Key] = (into.TryGetValue(kv.Key, out int x) ? x : 0) - kv.Value;
        }

        static void AddCost(Use u, Costs before, Costs after)
        {
            AddDiff(u.Cmd, before.Cmd, after.Cmd, u.Created);
            AddDiff(u.Contract, before.Contract, after.Contract, null);
            AddDiff(u.Company, before.Company, after.Company, null);
        }

        static Dictionary<string, int> Sum(List<Use> list, Func<Use, Dictionary<string, int>> pick)
        {
            var sum = new Dictionary<string, int>();
            foreach (Use u in list) foreach (KeyValuePair<string, int> kv in pick(u)) sum[kv.Key] = (sum.TryGetValue(kv.Key, out int x) ? x : 0) + kv.Value;
            return sum;
        }

        static List<object> Matching(FieldInfo f, string member, string id)
        {
            var found = new List<object>();
            foreach (object e in f.GetValue(null) as IEnumerable ?? new object[0]) if (ModStateCarry.Get(e, member) as string == id) found.Add(e);
            return found;
        }

        static int Cbills()
        {
            int total = 0;
            foreach (object e in commandUses.GetValue(null) as IEnumerable ?? new object[0]) total += Int(e, "TotalCost");
            return total;
        }

        // Undoes what the dropped uses added to CommandUses, DeploymentAssetsStats and the company stats; all of it, or none of it.
        static string RollBackCost(List<Use> dropped, List<string> refunds, string abilityId)
        {
            try
            {
                Dictionary<string, int> cmd = Sum(dropped, u => u.Cmd), contract = Sum(dropped, u => u.Contract), company = Sum(dropped, u => u.Company);
                var created = new Dictionary<string, bool>();
                foreach (Use u in dropped) foreach (string k in u.Created.Keys) created[k] = true;
                cmd = NonZero(cmd); contract = NonZero(contract); company = NonZero(company);
                if (cmd.Count == 0 && contract.Count == 0 && company.Count == 0) return "";
                // Check everything first.
                var cmdPlan = new List<KeyValuePair<object, int>>();
                foreach (KeyValuePair<string, int> kv in cmd)
                {
                    List<object> e = Matching(commandUses, "UnitID", kv.Key);
                    if (e.Count != 1) return CostFail(refunds, abilityId, "CommandUses has " + e.Count + " entries for " + kv.Key);
                    int now = Int(e[0], "UseCount"), next = now - kv.Value;
                    if (next < 0) return CostFail(refunds, abilityId, "CommandUses " + kv.Key + " UseCount " + now + " is below the dropped uses' " + kv.Value);
                    if (next == 0 && !created.ContainsKey(kv.Key)) return CostFail(refunds, abilityId, "CommandUses " + kv.Key + " would be left with no use but was not made by the dropped uses");
                    cmdPlan.Add(new KeyValuePair<object, int>(e[0], next));
                }
                var contractPlan = new List<KeyValuePair<object, int>>();
                foreach (KeyValuePair<string, int> kv in contract)
                {
                    List<object> e = Matching(deploymentStats, "ID", kv.Key);
                    if (e.Count == 0) return CostFail(refunds, abilityId, "DeploymentAssetsStats has no entry for " + kv.Key);
                    contractPlan.Add(new KeyValuePair<object, int>(e[0], Int(e[0], "contractUses") - kv.Value));   // SO changes the first match
                }
                object stats = CompanyStats();
                if (company.Count > 0 && stats == null) return CostFail(refunds, abilityId, "no SimGame company stats here");
                foreach (string stat in company.Keys)
                    if (!(bool)stats.GetType().GetMethod("ContainsStatistic", new[] { typeof(string) }).Invoke(stats, new object[] { stat }))
                        return CostFail(refunds, abilityId, "company stat " + stat + " is gone");
                // Then write.
                int cbBefore = Cbills();
                int cmdBefore = 0, cmdAfter = 0, conBefore = 0, conAfter = 0, coBefore = 0, coAfter = 0;
                IList cmdList = (IList)commandUses.GetValue(null);
                foreach (KeyValuePair<object, int> kv in cmdPlan)
                {
                    cmdBefore += Int(kv.Key, "UseCount"); cmdAfter += kv.Value;
                    if (kv.Value == 0) cmdList.Remove(kv.Key);
                    else kv.Key.GetType().GetField("UseCount", inst).SetValue(kv.Key, kv.Value);
                }
                foreach (KeyValuePair<object, int> kv in contractPlan)
                {
                    conBefore += Int(kv.Key, "contractUses"); conAfter += kv.Value;
                    kv.Key.GetType().GetField("contractUses", inst).SetValue(kv.Key, kv.Value);
                }
                foreach (KeyValuePair<string, int> kv in company)
                {
                    int now = (int)statGet.Invoke(stats, new object[] { kv.Key });
                    object add = Enum.Parse(statModify.GetParameters()[3].ParameterType, "Int_Add");
                    statModify.Invoke(stats, new object[] { "StratOps", -1, kv.Key, add, -kv.Value, -1, true });
                    coBefore += now; coAfter += (int)statGet.Invoke(stats, new object[] { kv.Key });
                }
                var sb = new StringBuilder();
                if (cmdPlan.Count > 0) sb.Append("|commandUses=").Append(cmdBefore).Append("->").Append(cmdAfter).Append("|cbills=").Append(cbBefore).Append("->").Append(Cbills());
                if (contractPlan.Count > 0) sb.Append("|contractUses=").Append(conBefore).Append("->").Append(conAfter);
                if (company.Count > 0) sb.Append("|companyStat=").Append(string.Join(",", new List<string>(company.Keys).ToArray())).Append(':').Append(coBefore).Append("->").Append(coAfter);
                return sb.ToString();
            }
            catch (Exception e) { return CostFail(refunds, abilityId, ModStateCarry.Inner(e)); }
        }

        static Dictionary<string, int> NonZero(Dictionary<string, int> d)
        {
            var r = new Dictionary<string, int>();
            foreach (KeyValuePair<string, int> kv in d) if (kv.Value != 0) r[kv.Key] = kv.Value;
            return r;
        }

        static string CostFail(List<string> refunds, string abilityId, string reason)
        {
            Fail(refunds, "cost|" + abilityId + "|" + reason);
            return "";
        }

        // ---- use records ----
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void PatchUses(Type ability, Assembly so)
        {
            var h = new HarmonyLib.Harmony("battletech.port.so-cmd-coop-uses");
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var pre = new HarmonyLib.HarmonyMethod(typeof(StrategicOperationsCoopCommandSync).GetMethod(nameof(UsePrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = HarmonyLib.Priority.First };
            var post = new HarmonyLib.HarmonyMethod(typeof(StrategicOperationsCoopCommandSync).GetMethod(nameof(UseFinalizer), BindingFlags.Static | BindingFlags.NonPublic));
            var spre = new HarmonyLib.HarmonyMethod(typeof(StrategicOperationsCoopCommandSync).GetMethod(nameof(StaticUsePrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = HarmonyLib.Priority.First };
            var spost = new HarmonyLib.HarmonyMethod(typeof(StrategicOperationsCoopCommandSync).GetMethod(nameof(StaticUseFinalizer), BindingFlags.Static | BindingFlags.NonPublic));
            var wpre = new HarmonyLib.HarmonyMethod(typeof(StrategicOperationsCoopCommandSync).GetMethod(nameof(WavePrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = HarmonyLib.Priority.First };
            var wfin = new HarmonyLib.HarmonyMethod(typeof(StrategicOperationsCoopCommandSync).GetMethod(nameof(WaveFinalizer), BindingFlags.Static | BindingFlags.NonPublic));
            var dpre = new HarmonyLib.HarmonyMethod(typeof(StrategicOperationsCoopCommandSync).GetMethod(nameof(SpawnerPrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = HarmonyLib.Priority.First };
            var names = new List<string>();
            foreach (MethodInfo m in ability.GetMethods(all))
                if (m.Name == "Activate" && !m.IsAbstract) { h.Patch(m, prefix: pre, finalizer: post); names.Add("Ability.Activate(" + m.GetParameters().Length + ")"); }
            Type utils = so.GetType("StrategicOperations.Framework.Utils", false);
            foreach (string n in new[] { "ActivateStrafeFromActor", "ActivateSpawnTurretFromActor" })
            {
                MethodInfo m = utils?.GetMethod(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null || m.GetParameters().Length == 0 || !ability.IsAssignableFrom(m.GetParameters()[0].ParameterType)) { log(Tag + "|FAILED|use-hook|Utils." + n + " not found: its uses are not recorded"); continue; }
                h.Patch(m, prefix: spre, finalizer: spost);
                names.Add("Utils." + n);
            }
            // Later strafe waves add to CommandUses outside the use; their cost goes to the use that owns the strafe key.
            MethodInfo initiate = utils?.GetMethod("InitiateStrafe", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (initiate == null || initiate.GetParameters().Length != 2 || initiate.GetParameters()[0].ParameterType != typeof(string))
                log(Tag + "|FAILED|use-hook|Utils.InitiateStrafe(string, PendingStrafeWave) not found: later waves' cost is not recorded");
            else { h.Patch(initiate, prefix: wpre, finalizer: wfin); names.Add("Utils.InitiateStrafe"); }
            Type spawner = so.GetType("StrategicOperations.Framework.Classes+CustomSpawner", false);
            MethodInfo loaded = spawner?.GetMethod("OnStrafeDepsLoaded", inst, null, Type.EmptyTypes, null);
            if (loaded == null || spawner.GetField("ParentSequenceIDForStrafe", inst) == null)
                log(Tag + "|FAILED|use-hook|CustomSpawner.OnStrafeDepsLoaded / ParentSequenceIDForStrafe not found: mech and vehicle strafe waves' cost is not recorded");
            else { h.Patch(loaded, prefix: dpre, finalizer: wfin); names.Add("CustomSpawner.OnStrafeDepsLoaded"); }
            log(Tag + "|use-hook|" + string.Join(",", names.ToArray()));
        }

        // Only the outermost hooked call records (an SO prefix calls Utils.ActivateStrafeFromActor inside Ability.Activate, and InitiateStrafe
        // inside both); finalizers run when the original throws too, so the depth always unwinds.
        static void UsePrefix(object __instance, out object __state) => __state = Before(__instance, null);
        static Exception UseFinalizer(Exception __exception, object __instance, object __state) { After(__instance, __state); return __exception; }
        static void StaticUsePrefix(object __0, out object __state) => __state = Before(__0, null);
        static Exception StaticUseFinalizer(Exception __exception, object __0, object __state) { After(__0, __state); return __exception; }
        static void WavePrefix(object __0, out object __state) => __state = Before(null, __0 as string ?? "");
        static void SpawnerPrefix(object __instance, out object __state) => __state = Before(null, ModStateCarry.Get(__instance, "ParentSequenceIDForStrafe") as string ?? "");
        static Exception WaveFinalizer(Exception __exception, object __state) { After(null, __state); return __exception; }

        static object Before(object a, string strafeKey)
        {
            if (++depth != 1) return null;
            try
            {
                if (a == null && strafeKey == null) return null;
                return new Snap { Ability = a, StrafeKey = strafeKey, Cd = a == null ? 0 : Val(cooldown, a), Uses = a == null ? 0 : Val(usesLeft, a), Keys = PendingKeys(), Cost = ReadCosts() };
            }
            catch (Exception e) { Row("FAILED", "use-hook|" + ModStateCarry.Inner(e)); return null; }
        }

        static void After(object a, object state)
        {
            if (depth > 0) depth--;
            try
            {
                if (!(state is Snap s)) return;
                object combat = Combat();
                if (!ReferenceEquals(combat, usesCombat)) { uses.Clear(); usesCombat = combat; }
                Costs now = ReadCosts();
                if (s.Ability == null)
                {
                    // A later wave: its cost goes to the recorded use that owns the strafe key.
                    var tmp = new Use();
                    AddCost(tmp, s.Cost, now);
                    if (tmp.Cmd.Count == 0 && tmp.Contract.Count == 0 && tmp.Company.Count == 0) return;
                    foreach (List<Use> list in uses.Values)
                        foreach (Use u in list)
                            if (u.Keys.Contains(s.StrafeKey)) { AddCost(u, s.Cost, now); return; }
                    return;
                }
                int cd = Val(cooldown, a), us = Val(usesLeft, a);
                var use = new Use { Ability = a, CdBefore = s.Cd, UsesBefore = s.Uses, CdAfter = cd, UsesAfter = us };
                foreach (string k in PendingKeys().Keys) if (!s.Keys.ContainsKey(k)) use.Keys.Add(k);
                AddCost(use, s.Cost, now);
                if (cd == s.Cd && us == s.Uses && use.Keys.Count == 0 && use.Cmd.Count == 0 && use.Contract.Count == 0 && use.Company.Count == 0) return;
                if (!uses.TryGetValue(a, out List<Use> l2)) uses[a] = l2 = new List<Use>();
                l2.Add(use);
            }
            catch (Exception e) { Row("FAILED", "use-hook|" + ModStateCarry.Inner(e)); }
        }

        static Dictionary<string, bool> PendingKeys()
        {
            var keys = new Dictionary<string, bool>();
            if (pendingStrafes.GetValue(null) is IDictionary waves) foreach (object k in waves.Keys) if (k is string ks) keys[ks] = true;
            foreach (object kv in deferredSpawns.GetValue(null) as IEnumerable ?? new object[0]) if (ModStateCarry.Get(kv, "Key") is string ks) keys[ks] = true;
            return keys;
        }

        // Guest, after its snapshot load.
        static void ApplyData(object invocation, string data)
        {
            if (invocation != null || data == null) return;
            try
            {
                object combat = Combat();
                if (combat == null) return;
                IList list = (IList)commandAbilities.GetValue(null);
                Row("before", "abilities=" + list.Count);
                var made = new List<object>();
                var ids = new List<string>();
                var refundRows = new List<string>();
                foreach (string line in Encoding.UTF8.GetString(Convert.FromBase64String(data)).Split('\n'))
                {
                    if (line.Length == 0) continue;
                    string[] p = line.Split('\t');
                    if (p.Length == 2 && p[0] == "R") { refundRows.Add(p[1]); continue; }
                    if (p.Length != 4) throw new FormatException("ability line has " + p.Length + " fields");
                    object def = Def(combat, p[0]);
                    if (def == null) { Row("unmapped", "ability-def|" + p[0]); continue; }
                    object a = newAbility.Invoke(new[] { def });
                    init.Invoke(a, new[] { combat });
                    cooldown.SetValue(a, int.Parse(p[1], CultureInfo.InvariantCulture), null);
                    usesLeft.SetValue(a, int.Parse(p[2], CultureInfo.InvariantCulture), null);
                    activations.SetValue(a, int.Parse(p[3], CultureInfo.InvariantCulture), null);
                    made.Add(a);
                    ids.Add(p[0] + ":" + p[1] + "/" + p[2] + "/" + p[3]);
                }
                // Parsed and made in full before the list is touched, so a malformed payload changes nothing.
                list.Clear();
                foreach (object a in made) list.Add(a);
                counter.Apply++;
                if (made.Count > 0) counter.NonEmpty++;
                Row("apply", "abilities=" + made.Count + "|" + string.Join(";", ids.ToArray()));
                // The host's refund rows, on this peer, once the refunded values above are in place.
                foreach (string r in refundRows) log(Tag + "|" + r);
            }
            catch (Exception e) { Row("apply-failed", ModStateCarry.Inner(e)); }
        }

        static object Def(object combat, string id)
        {
            object store = ModStateCarry.Get(ModStateCarry.Get(combat, "DataManager"), "AbilityDefs");
            if (store == null) return null;
            var types = new List<Type> { store.GetType() };
            types.AddRange(store.GetType().GetInterfaces());
            foreach (Type t in types)
            {
                MethodInfo tryGet = t.GetMethod("TryGet", BindingFlags.Instance | BindingFlags.Public);
                if (tryGet == null || tryGet.GetParameters().Length != 2) continue;
                object[] args = { id, null };
                return (bool)tryGet.Invoke(store, args) ? args[1] : null;
            }
            return null;
        }

        static string I(object v) => Convert.ToInt32(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

        static void Row(string kind, string text)
        {
            if (++rows > 300) return;
            object combat = Combat();
            string at = combat == null ? "?" : ModStateCarry.Get(ModStateCarry.Get(combat, "TurnDirector"), "CurrentRound") + "/" + ModStateCarry.Get(ModStateCarry.Get(combat, "TurnDirector"), "CurrentPhase");
            log(Tag + "|" + kind + "|" + text + "|at=" + at);
        }
    }
}
