// CustomUnits last-move stat on the receiving co-op peer (gh issue 24; the maintainer's rule: co-op desyncs are fixed, not hidden).
// CustomUnits keeps a per-unit stat, CULastMoveDistance (created lazily by CustomAmmoCategories' UnitUnaffectionsActorStats.LastMoveDistance(unit, value)), and writes it
// from ActorMovementSequence_InitDistanceClamp.Postfix, a postfix on ActorMovementSequence.Init: value = Pathing.MaxCost - Pathing.CostLeft, the terrain-weighted cost
// of the unit's current path, and nothing when the unit has no Pathing path. In a co-op combat the peer that moves a unit has the path; the other peer builds the same
// ActorMovementSequence from the invocation's waypoints (positions only) and its copy of the unit has no path, so the stat never appears there. The retail MECHS compare
// then differs (key present on one peer only, key order shifted), the guest is dropped, and CustomUnits' own MoveClamp reader (Mech_MaxWalkDistance.MoveClamp, vehicles
// with a configured MoveClamp) computes on other state. pair-rp-fixonce1 v1 (Flea) and pair-rp-faction1 (Brigand, Javelin): the stat was on the host and absent on the guest at
// every dump.
// UCU patch 145 puts the sender's cost in AbstractActorMovementInvocation (PathCostUsed, -1 = the sender had no path) and publishes it on the receiver while the sequence is
// built (ReceivedActorGUID, ReceivedPathCostUsed). This adapter, only when assemblies named CustomUnits and CustomAmmoCategories are loaded, adds a postfix after
// CustomUnits' own on ActorMovementSequence.Init: when a received cost exists for this unit and the unit has no path (CustomUnits skipped it), it calls the same
// LastMoveDistance(unit, value) with the sender's value. A peer that moved the unit itself, single-player, and a game without those mods are untouched.
// R-fix21 (so-ai-badrop-coop, culastmove-cause.md): the sender's cost is the value read when the invocation is built (X, right after the AI's path update); the
// mover's own stat is the value read again at Init (Y). They are equal for every unit that was compared, but for one AI unit in R-fix21 the mover's Pathing had changed
// in between (X 54.56 vs Y 94.59, 112.39 vs 104.38, 113.77 vs 63.45 against moves of 97, 101 and 97 m) and the MECHS compare named CULastMoveDistance. So the
// mover also decides a `movestat` record in CoopDecisionChannel with the stat it holds after CustomUnits' own postfix (Y); the other peer sets that value with the same
// setter right before the invocation it rides (the next one the mover sends: the Reserve that ends the activation, before the next compare). The construction-time set
// above stays (it is what the receiver's own sequence reads). Rows: customunits-coop-movestat|movestat|<host|mover>|<unit>|ctor=<X>|init=<Y>|differs=<0|1>|... on the
// mover for every move (the mover's MaxCost, CostLeft, path count and move type at both reads, the data for the cause), |movestat|apply|<unit>|<Y> on the other peer.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomUnitsCoopMoveStat
    {
        const string OwnId = "battletech.port.customunits-coop-movestat";
        static Action<string> log;
        static FieldInfo receivedGuid, receivedCost;
        static MethodInfo setStat;
        static PropertyInfo pathingProp, hasPathProp, guidProp;
        static int logged, statRows;
        const string Tag = "customunits-coop-movestat";
        static CoopDecisionChannel channel;
        static MethodInfo getStat;
        static PropertyInfo maxCostProp, costLeftProp, currentPathProp, moveTypeProp;
        static FieldInfo currentPathField;
        static readonly Dictionary<string, string> ctorRead = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Harness only (CoopMod MoveCostSubstitute, before UCU patch 145 is in the player): supplies the sender's cost for an actor (-1 = none) instead of the
        // product statics. Null in a shipped game.
        public static Func<object, float> HarnessCost;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("CustomUnits", Apply, logLine, "customunits-coop-movestat");
        }

        static void Apply(Assembly customUnits)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type sequence = ModAssemblyHook.GameType("BattleTech.ActorMovementSequence");
            Type invocation = ModAssemblyHook.GameType("BattleTech.AbstractActorMovementInvocation");
            receivedGuid = invocation?.GetField("ReceivedActorGUID", all);
            receivedCost = invocation?.GetField("ReceivedPathCostUsed", all);
            MethodInfo init = null;
            if (sequence != null)
                foreach (MethodInfo m in sequence.GetMethods(all))
                {
                    ParameterInfo[] p = m.GetParameters();
                    if (m.Name == "Init" && p.Length == 2 && p[0].ParameterType.Name == "AbstractActor") { init = m; break; }
                }
            Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor");
            pathingProp = actor?.GetProperty("Pathing", BindingFlags.Instance | BindingFlags.Public);
            guidProp = actor?.GetProperty("GUID", BindingFlags.Instance | BindingFlags.Public);
            hasPathProp = ModAssemblyHook.GameType("BattleTech.Pathing")?.GetProperty("HasPath", BindingFlags.Instance | BindingFlags.Public);
            if (init == null || (HarnessCost == null && (receivedGuid == null || receivedCost == null)) || pathingProp == null || hasPathProp == null || guidProp == null)
            {
                log("customunits-coop-movestat|skipped|shape changed Init=" + (init != null) + " ReceivedActorGUID=" + (receivedGuid != null) + " ReceivedPathCostUsed=" + (receivedCost != null)
                    + " Pathing=" + (pathingProp != null) + " HasPath=" + (hasPathProp != null) + " GUID=" + (guidProp != null) + " (needs UCU patch 145)");
                return;
            }
            new Harmony(OwnId).Patch(init, postfix: new HarmonyMethod(typeof(CustomUnitsCoopMoveStat).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
            log("customunits-coop-movestat|installed|BattleTech.ActorMovementSequence::Init");
            if (HarnessCost == null) ModAssemblyHook.AtFirstCombat(InstallRecord, log, Tag);
        }

        // The mover's stat as a decision record (see the header). Only HBS methods and the channel are used here; a game without UCU patches 166 and 176 keeps the
        // construction-time set alone (the channel's Init logs a skipped row).
        static void InstallRecord()
        {
            try
            {
                const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type pathing = ModAssemblyHook.GameType("BattleTech.Pathing");
                Type invocation = ModAssemblyHook.GameType("BattleTech.AbstractActorMovementInvocation");
                Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor");
                maxCostProp = pathing?.GetProperty("MaxCost", inst);
                costLeftProp = pathing?.GetProperty("CostLeft", inst);
                moveTypeProp = pathing?.GetProperty("MoveType", inst);
                currentPathProp = pathing?.GetProperty("CurrentPath", inst);
                currentPathField = pathing?.GetField("CurrentPath", inst);
                ConstructorInfo ctor = invocation?.GetConstructor(inst, null, new[] { actor, typeof(bool) }, null);
                Type stats = ModAssemblyHook.GameType("CustAmmoCategories.UnitUnaffectionsActorStats");
                if (stats != null)
                    foreach (MethodInfo m in stats.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        ParameterInfo[] p = m.GetParameters();
                        if (m.Name == "LastMoveDistance" && p.Length == 1 && m.ReturnType == typeof(float)) { getStat = m; break; }
                    }
                if (ctor == null || maxCostProp == null || costLeftProp == null || getStat == null || SetStat() == null)
                {
                    log(Tag + "|record-skipped|shape changed AbstractActorMovementInvocation(actor,bool)=" + (ctor != null) + " Pathing.MaxCost=" + (maxCostProp != null) + " .CostLeft=" + (costLeftProp != null)
                        + " LastMoveDistance(get)=" + (getStat != null) + " (set)=" + (SetStat() != null));
                    return;
                }
                if (!CoopDecisionChannel.Init(log, Tag)) return;
                channel = new CoopDecisionChannel("cu-movestat", Tag, log);
                channel.On("movestat", ReplayStat);
                var h = new Harmony(OwnId + "-record");
                CoopDecisionChannel.Patch(h, ctor, postfix: new HarmonyMethod(typeof(CustomUnitsCoopMoveStat).GetMethod(nameof(CtorPostfix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
                channel.Install("the mover's CULastMoveDistance (after CustomUnits' own write at ActorMovementSequence.Init) carried to the other peer");
            }
            catch (Exception e) { log(Tag + "|FAILED|record-install|" + e.Message); }
        }

        static string Now() => ((long)(DateTime.UtcNow - Epoch).TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

        static string PathRead(object pathing)
        {
            string path = "?";
            try
            {
                object list = currentPathProp != null ? currentPathProp.GetValue(pathing, null) : currentPathField?.GetValue(pathing);
                if (list is ICollection c) path = c.Count.ToString(CultureInfo.InvariantCulture);
            }
            catch { }
            string maxCost = "?", costLeft = "?", moveType = "?";
            try { maxCost = CoopDecisionChannel.F((float)maxCostProp.GetValue(pathing, null)); } catch { }
            try { costLeft = CoopDecisionChannel.F((float)costLeftProp.GetValue(pathing, null)); } catch { }
            try { moveType = Convert.ToString(moveTypeProp?.GetValue(pathing, null), CultureInfo.InvariantCulture); } catch { }
            return "maxCost=" + maxCost + ",costLeft=" + costLeft + ",path=" + path + ",moveType=" + moveType;
        }

        // AbstractActorMovementInvocation(actor, bool): the construction-time read (X) of the mover's Pathing, kept for the row at Init.
        static void CtorPostfix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 1 || __args[0] == null || !CoopDecisionChannel.CoopCombat()) return;
                object a = __args[0];
                string guid = guidProp.GetValue(a, null) as string;
                object pathing = pathingProp.GetValue(a, null);
                if (guid != null && pathing != null) ctorRead[guid] = PathRead(pathing);
            }
            catch (Exception e) { log(Tag + "|FAILED|ctor|" + e.Message); }
        }

        // The mover, after CustomUnits' own write at Init: the stat it now holds is the record; X (the cost the invocation carries) and both Pathing reads are the row.
        static void RecordMover(object actor, object pathing, string guid)
        {
            if (channel == null || guid == null || !CoopDecisionChannel.CoopCombat() || !CoopDecisionChannel.Authority(actor)) return;
            float y = (float)getStat.Invoke(null, new object[] { actor });
            string x = "-", differs = "0";
            if (HarnessCost == null && receivedCost != null && string.Equals(receivedGuid.GetValue(null) as string, guid, StringComparison.Ordinal))
            {
                float cx = (float)receivedCost.GetValue(null);
                x = CoopDecisionChannel.F(cx);
                if (cx != y) differs = "1";
            }
            ctorRead.TryGetValue(guid, out string atCtor);
            string role = CoopDecisionChannel.Get(actor, "team")?.GetType().Name == "AITeam" ? "host" : "mover";
            if (++statRows <= 400)
                log(Tag + "|movestat|" + role + "|" + guid + "|ctor=" + x + "|init=" + CoopDecisionChannel.F(y) + "|differs=" + differs + "|ctorRead=" + (atCtor ?? "-") + "|initRead=" + PathRead(pathing) + "|t=" + Now());
            ctorRead.Remove(guid);
            channel.Decide("movestat", actor, "y=" + CoopDecisionChannel.F(y), "~", CoopDecisionChannel.F(y));
        }

        // The other peer: the mover's value, set with the same setter, right before the invocation the record rode.
        static string ReplayStat(object unit, string[] f)
        {
            float y = CoopDecisionChannel.PF(f[0]);
            SetStat().Invoke(null, new object[] { unit, y });
            if (statRows <= 400) { statRows++; log(Tag + "|movestat|apply|" + (guidProp.GetValue(unit, null) as string) + "|" + f[0] + "|t=" + Now()); }
            return "y=" + f[0];
        }

        static MethodInfo SetStat()
        {
            if (setStat != null) return setStat;
            // CustomAmmoCategories may load after CustomUnits: resolved at first use.
            Type stats = ModAssemblyHook.GameType("CustAmmoCategories.UnitUnaffectionsActorStats");
            if (stats == null) return null;
            foreach (MethodInfo m in stats.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                ParameterInfo[] p = m.GetParameters();
                if (m.Name == "LastMoveDistance" && p.Length == 2 && p[1].ParameterType == typeof(float)) { setStat = m; break; }
            }
            return setStat;
        }

        static void Postfix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 1 || __args[0] == null) return;
                object actor = __args[0];
                object pathing = pathingProp.GetValue(actor, null);
                string guid = guidProp.GetValue(actor, null) as string;
                if (pathing != null && (bool)hasPathProp.GetValue(pathing, null))   // CustomUnits' own postfix wrote it from this peer's path
                {
                    RecordMover(actor, pathing, guid);
                    return;
                }
                float cost;
                if (HarnessCost != null) cost = HarnessCost(actor);
                else
                {
                    cost = (float)receivedCost.GetValue(null);
                    if (!string.Equals(receivedGuid.GetValue(null) as string, guid, StringComparison.Ordinal)) return;
                }
                if (cost < 0f || guid == null) return;
                MethodInfo set = SetStat();
                if (set == null) return;
                set.Invoke(null, new object[] { actor, cost });
                if (++logged <= 12) log("customunits-coop-movestat|set|" + guid + "|cost=" + cost.ToString("R") + "|t=" + Now());
            }
            catch (Exception e) { log("customunits-coop-movestat|postfix failed|" + e.Message + "|t=" + Now()); }
        }
    }
}
