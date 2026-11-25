// Extended_CE's run-movement rules on the co-op peer that did not plan the move (mod support, logic-bext-fixes-20261122, heat layer).
// Extended_CE (BEX:T) reads the moving unit's Pathing when its move completes: RunRules.Mech_OnMoveComplete_Prefix (a prefix on
// Mech.OnMoveComplete) takes WalkHeat/2 off _tempHeat when Pathing.HasPath and CostLeft > MaxCost/3 (MaxCost/2 with MASC), else adds
// the running to-hit malus to MechTTStatStore.PreviousTurnMoveMalus; Abilities.AbstractActor_OnMoveComplete (a postfix on
// AbstractActor.OnMoveComplete) sets BracedLastRound on the same test. Only the peer that planned the move has that path: the host for
// the AI's units, the owning player's peer for a player's. Every other peer runs the move from the invocation's waypoints and its
// Pathing has no path, so it keeps the full walk heat (BEXT-HEAT-probe: Stalker 6cd3107e temp heat 26 on the host, 27 on the guest at
// round 2, 2 and 3 at round 3, identical CombatLog.Heat gains; BEXT-MQ-fix compare 110, 2d62bfad heat 26 / 29).
// This adapter, only when an assembly named Extended_CE is loaded and only in a co-op combat, registers a source with UCU's co-op side
// channel (CoopInvocationSideData, patch 166): the peer that sends a movement or melee invocation for a unit it is the authority for
// sends that unit's Pathing facts with it (has a path, CostLeft, MaxCost, MoveType); every other peer stores them right before it runs
// the invocation. When that unit's move completes there, its Pathing shows the sender's facts only while Extended_CE's hooks run: from a
// prefix before every other on Mech.OnMoveComplete to a prefix after every other (before the body), and from a postfix before every
// other on AbstractActor.OnMoveComplete to a postfix after every other; the peer's own path, move type, cost and grid distance are
// restored at each end (a finalizer restores on an exception). The stand-in path is one uninitialised PathNode: Extended_CE reads only
// its count, and HBS code never sees it. The facts are used once. A peer that planned the move, single-player and Skirmish are untouched.
// Rows: `extendedce-path|installed|...`, `...|sent|<guid>|<facts>|inv=`, `...|stored|<guid>|<facts>|inv=`, and on every co-op peer
// `...|move-complete|<guid>|<facts seen>|mirrored=<bool>` for Mech.OnMoveComplete, so two peers' rows compare as text (first 200).
// Attack resolution (layer F, BEXT-GATE-fix2 compare 113): Extended_CE's ToHit.GetSelfSpeedModifier prefix (RunRules.cs:135-175) reads the
// attacker's Pathing after a move of more than 10 m (has a path, walking/melee, CostLeft < MaxCost/3 adds RunningToHitCumulativePenalty).
// On a peer without the path it falls to a distance test, so the two peers' to-hit chances can differ and, from the same hit-info seed, the
// rolls differ (2d62bfad's MG burst: one LeftLeg hit and a Gear_BEX_LegUpper crit on the guest only). So an AttackInvocation also carries
// the attacker's facts as its sender sends it, and a melee invocation's facts are kept for its attack as well. On every other peer, from a prefix
// before every other on AttackDirector.AttackSequence.GenerateToHitInfo to a finalizer, the attacker's Pathing shows those facts (the same
// window as above). Row on every co-op peer: `...|to-hit|<guid>|<facts seen>|self-speed=<modifier>|local=<modifier without the
// mirror>|mirrored=<bool>` (both modifiers `n/a` for an attacker that has not moved this round, whose branch reads a static set only in the roll).
// Melee (BEXT-J-fix compare 16; the mirror of melee invocations dates from the heat fix 850cb2c98 and was kept for to-hit in a6992aea8): a melee
// sequence re-plans the path on every peer when it runs (MechMeleeSequence.GenerateMeleePath: SetWalking, melee target and destination,
// Update, ClearMeleeTarget, MechMeleeSequence.cs:141-153), so the planner's Pathing at send time is a leftover grid (2d62bfad's melee sent
// 8.70/480 Backward while the host's own OnMoveComplete read 387/720 Walking: WalkHeat/2 taken off on the host only, heat 21 / 24). A melee
// invocation sends `melee` instead of facts; every other peer drops what it holds for that unit and reads its own Pathing at the move-complete
// and the attack's to-hit (rows `sent|<guid>|melee|inv=`, `melee-local|<guid>|inv=`, then mirrored=False on both peers).
// Not mirrored: Mech.OnSprintComplete (RunRules.cs:806-830) reads Pathing only for team.IsLocalPlayer, i.e. on the peer that owns and
// planned the sprint; BEX:T chassis do carry MASC (25 mech defs), so its MASC instability change runs on that peer only (an open co-op layer,
// not a Pathing mirror case).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;

namespace BattleTechPort
{
    public static class ExtendedCEPathingCoopSync
    {
        const string Key = "extendedce-path";
        const string Tag = "extendedce-path";
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static Action<string> log;
        static PropertyInfo battleTechGame, pathingProp, guidProp, combatOf, netGame, isCoop, currentGrid, maxDistance, hasPath;
        static FieldInfo currentPath, moveType, costLeft;
        static MethodInfo findActor, isAuthority, setMaxDistance, selfSpeed;
        static Type mechType, movementInvocation, meleeInvocation, attackInvocation, pathNode;
        static FieldInfo seqAttacker;
        static PropertyInfo toHitProp, hasMovedProp;
        static readonly Dictionary<string, Facts> stored = new Dictionary<string, Facts>();
        static readonly Dictionary<string, Facts> attackStored = new Dictionary<string, Facts>();   // the facts an attack's to-hit reads (attack and melee invocations); replaced by the next
        static Mirror active;
        static object storedCombat;
        static int rows;

        sealed class Facts
        {
            public bool Has;
            public float CostLeft, MaxCost;
            public int MoveType;
            public string Text() => (Has ? "1" : "0") + "," + CostLeft.ToString("R", CultureInfo.InvariantCulture) + "," + MaxCost.ToString("R", CultureInfo.InvariantCulture) + "," + MoveType.ToString(CultureInfo.InvariantCulture);
        }

        sealed class Mirror
        {
            public object Actor, Pathing, Path, Grid;
            public object MoveType;
            public float CostLeft, MaxDistance;
            public int Depth;
        }

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("Extended_CE", Install, logLine, Tag);
        }

        static void Install(Assembly ece)
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor"), combat = ModAssemblyHook.GameType("BattleTech.CombatGameState");
            Type pathing = ModAssemblyHook.GameType("BattleTech.Pathing"), grid = ModAssemblyHook.GameType("BattleTech.PathNodeGrid");
            Type side = ModAssemblyHook.GameType("BattleTech.CoopInvocationSideData");
            mechType = ModAssemblyHook.GameType("BattleTech.Mech");
            movementInvocation = ModAssemblyHook.GameType("BattleTech.AbstractActorMovementInvocation");
            meleeInvocation = ModAssemblyHook.GameType("BattleTech.MechMeleeInvocation");
            attackInvocation = ModAssemblyHook.GameType("BattleTech.AttackInvocation");
            pathNode = ModAssemblyHook.GameType("BattleTech.PathNode");
            Type sequence = ModAssemblyHook.GameType("BattleTech.AttackDirector+AttackSequence"), toHit = ModAssemblyHook.GameType("BattleTech.ToHit");
            seqAttacker = sequence?.GetField("attacker", Inst);
            MethodInfo toHitInfo = sequence?.GetMethod("GenerateToHitInfo", Inst, null, Type.EmptyTypes, null);
            toHitProp = combat?.GetProperty("ToHit", Inst);
            hasMovedProp = actor?.GetProperty("HasMovedThisRound", Inst);
            selfSpeed = actor == null ? null : toHit?.GetMethod("GetSelfSpeedModifier", Inst, null, new[] { actor }, null);
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", st);
            pathingProp = actor?.GetProperty("Pathing", BindingFlags.Instance | BindingFlags.Public);
            guidProp = actor?.GetProperty("GUID", BindingFlags.Instance | BindingFlags.Public);
            combatOf = actor?.GetProperty("Combat", Inst);
            netGame = combat?.GetProperty("NetworkGameInstance", Inst);
            isCoop = netGame?.PropertyType.GetProperty("IsCoopCombat", Inst);
            findActor = combat?.GetMethod("FindActorByGUID", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
            currentPath = pathing?.GetField("CurrentPath", Inst);
            moveType = pathing?.GetField("moveType", Inst);
            costLeft = pathing?.GetField("costLeft", Inst);
            currentGrid = pathing?.GetProperty("CurrentGrid", Inst);
            hasPath = pathing?.GetProperty("HasPath", Inst);
            maxDistance = grid?.GetProperty("MaxDistance", Inst);
            setMaxDistance = maxDistance?.GetSetMethod(true);
            isAuthority = side?.GetMethod("IsAuthority", st);
            MethodInfo register = side?.GetMethod("Register", st);
            MethodInfo mechMove = FindOnMoveComplete(mechType), actorMove = FindOnMoveComplete(actor);
            if (register == null || isAuthority == null || mechMove == null || actorMove == null || pathingProp == null || guidProp == null || combatOf == null
                || isCoop == null || findActor == null || currentPath == null || moveType == null || costLeft == null || currentGrid == null || hasPath == null
                || setMaxDistance == null || movementInvocation == null || pathNode == null || battleTechGame == null)
            {
                log(Tag + "|skipped|shape changed CoopInvocationSideData=" + (register != null && isAuthority != null) + " OnMoveComplete=" + (mechMove != null && actorMove != null)
                    + " Pathing=" + (currentPath != null && moveType != null && costLeft != null && currentGrid != null) + " MaxDistance=" + (setMaxDistance != null)
                    + " (needs UCU patch 166); Extended_CE's run rules read a path only the planning peer has");
                return;
            }
            register.Invoke(null, new object[] { Key, new Func<object, string>(Capture), new Action<object, string>(Apply) });
            var h = new Harmony("battletech.port.extendedce-path");
            h.Patch(mechMove,
                prefix: new HarmonyMethod(typeof(ExtendedCEPathingCoopSync).GetMethod(nameof(MechEnter), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(ExtendedCEPathingCoopSync).GetMethod(nameof(MechFinalizer), BindingFlags.Static | BindingFlags.NonPublic)));
            h.Patch(mechMove, prefix: new HarmonyMethod(typeof(ExtendedCEPathingCoopSync).GetMethod(nameof(Leave), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
            h.Patch(actorMove, postfix: new HarmonyMethod(typeof(ExtendedCEPathingCoopSync).GetMethod(nameof(ActorEnter), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
            h.Patch(actorMove, postfix: new HarmonyMethod(typeof(ExtendedCEPathingCoopSync).GetMethod(nameof(ActorLeave), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
            bool attacks = attackInvocation != null && seqAttacker != null && toHitInfo != null && toHitProp != null && selfSpeed != null && hasMovedProp != null;
            if (attacks)
                h.Patch(toHitInfo,
                    prefix: new HarmonyMethod(typeof(ExtendedCEPathingCoopSync).GetMethod(nameof(ToHitEnter), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(ExtendedCEPathingCoopSync).GetMethod(nameof(ToHitFinalizer), BindingFlags.Static | BindingFlags.NonPublic)));
            else
                log(Tag + "|attack-skipped|shape changed AttackInvocation=" + (attackInvocation != null) + " AttackSequence=" + (seqAttacker != null && toHitInfo != null)
                    + " ToHit=" + (toHitProp != null && selfSpeed != null) + "; the to-hit self-speed rule still reads a path only the planning peer has");
            log(Tag + "|installed|" + ece.GetName().Version + "|the planning peer's Pathing facts go with each movement, melee" + (attacks ? " and attack" : "")
                + " invocation; Extended_CE's OnMoveComplete hooks" + (attacks ? " and the attack's to-hit" : "") + " see them on every peer");
        }

        static MethodInfo FindOnMoveComplete(Type t)
        {
            if (t == null) return null;
            foreach (MethodInfo m in t.GetMethods(Inst | BindingFlags.DeclaredOnly))
                if (m.Name == "OnMoveComplete" && m.GetParameters().Length == 6) return m;
            return null;
        }

        // Sender: the acting unit's Pathing as it sends the move (the path its own OnMoveComplete will read).
        static string Capture(object invocation)
        {
            if (invocation == null || invocation.GetType() == typeof(object)) return null;   // full state (null) and SendNow (Now): nothing held
            string guid = InvocationActor(invocation);
            object combat = Combat();
            object actor = guid == null || combat == null ? null : Find(combat, guid);
            if (actor == null || !CoopCombat(actor) || !(bool)isAuthority.Invoke(null, new[] { actor })) return null;
            string inv = invocation.GetType().GetProperty("InvocationID", Inst)?.GetValue(invocation, null) as string ?? "?";
            if (meleeInvocation != null && meleeInvocation.IsAssignableFrom(invocation.GetType()))
            {
                // A melee re-plans its path on every peer when its sequence runs (MechMeleeSequence.GenerateMeleePath), so each peer's own
                // Pathing is what the sender's hooks will read; the facts here are the planner's leftover grid (BEXT-J-fix compare 16).
                Row("sent|" + guid + "|melee|inv=" + inv);
                return guid + ";melee";
            }
            Facts f = Read(actor);
            if (f == null) return null;
            Row("sent|" + guid + "|" + f.Text() + "|inv=" + inv);
            return guid + ";" + f.Text();
        }

        // Every other peer, right before it runs that invocation.
        static void Apply(object invocation, string data)
        {
            if (invocation == null || invocation.GetType() == typeof(object) || string.IsNullOrEmpty(data)) return;
            int semi = data.IndexOf(';');
            if (semi <= 0) { Row("bad|" + data); return; }
            string guid = data.Substring(0, semi);
            object combat = Combat();
            object actor = combat == null ? null : Find(combat, guid);
            if (actor == null || (bool)isAuthority.Invoke(null, new[] { actor })) return;
            if (!ReferenceEquals(combat, storedCombat)) { stored.Clear(); attackStored.Clear(); storedCombat = combat; }
            if (data.Substring(semi + 1) == "melee")
            {
                // No mirror for a melee: this peer's own re-planned Pathing is read at its move-complete and its attack's to-hit (rows mirrored=False).
                stored.Remove(guid); attackStored.Remove(guid);
                Row("melee-local|" + guid + "|inv=" + (invocation.GetType().GetProperty("InvocationID", Inst)?.GetValue(invocation, null) as string ?? "?"));
                return;
            }
            string[] p = data.Substring(semi + 1).Split(',');
            if (p.Length != 4) { Row("bad|" + data); return; }
            var f = new Facts
            {
                Has = p[0] == "1",
                CostLeft = float.Parse(p[1], CultureInfo.InvariantCulture),
                MaxCost = float.Parse(p[2], CultureInfo.InvariantCulture),
                MoveType = int.Parse(p[3], CultureInfo.InvariantCulture),
            };
            Type t = invocation.GetType();
            bool attack = attackInvocation != null && attackInvocation.IsAssignableFrom(t), melee = meleeInvocation != null && meleeInvocation.IsAssignableFrom(t);
            if (!attack) stored[guid] = f;
            if (attack || melee) attackStored[guid] = f;
            string inv = invocation.GetType().GetProperty("InvocationID", Inst)?.GetValue(invocation, null) as string ?? "?";
            Row("stored|" + guid + "|" + f.Text() + "|inv=" + inv);
        }

        static void MechEnter(object __instance)
        {
            try
            {
                if (!CoopCombat(__instance)) return;
                bool mirrored = Enter(__instance);
                Facts seen = Read(__instance);
                Row("move-complete|" + GuidOf(__instance) + "|" + (seen == null ? "no-pathing" : seen.Text()) + "|mirrored=" + mirrored);
            }
            catch (Exception ex) { Row("enter-failed|" + GuidOf(__instance) + "|" + ex.Message); }
        }

        // The attack's to-hit: the attacker's Pathing shows the sender's facts (attack and melee invocations) until the finalizer.
        static void ToHitEnter(object __instance)
        {
            object actor = null;
            try
            {
                actor = seqAttacker.GetValue(__instance);
                if (actor == null || !CoopCombat(actor)) return;
                // The modifier is logged only for an attacker that moved this round: the other branch tests RunRules.firingPosition, a static the
                // real to-hit sets just before the roll, so a call from here reads a stale value (BEXT-SPEED-fix, 2d62bfad). Extended_CE's prefix only
                // reads state (settings, stats, its tables, abilities) and writes __result: the extra call changes nothing and draws no random number.
                bool moved = hasMovedProp.GetValue(actor, null) is bool b && b;
                string local = moved ? SelfSpeed(actor) : "n/a";
                bool mirrored = Enter(actor, attackStored);
                Facts seen = Read(actor);
                Row("to-hit|" + GuidOf(actor) + "|" + (seen == null ? "no-pathing" : seen.Text()) + "|self-speed=" + (moved && mirrored ? SelfSpeed(actor) : local) + "|local=" + local + "|mirrored=" + mirrored);
            }
            catch (Exception ex) { Row("enter-failed|" + GuidOf(actor) + "|" + ex.Message); }
        }

        static Exception ToHitFinalizer(object __instance, Exception __exception)
        {
            try { object actor = seqAttacker.GetValue(__instance); if (active != null && ReferenceEquals(active.Actor, actor)) Leave(); }
            catch (Exception ex) { Row("restore-failed|to-hit|" + ex.Message); }
            return __exception;
        }

        static string SelfSpeed(object actor)
        {
            try { return ((float)selfSpeed.Invoke(toHitProp.GetValue(combatOf.GetValue(actor, null), null), new[] { actor })).ToString("R", CultureInfo.InvariantCulture); }
            catch (Exception ex) { return "err:" + (ex.InnerException ?? ex).GetType().Name; }
        }

        static void ActorEnter(object __instance)
        {
            try { if (CoopCombat(__instance)) Enter(__instance); }
            catch (Exception ex) { Row("enter-failed|" + GuidOf(__instance) + "|" + ex.Message); }
        }

        static void ActorLeave(object __instance)
        {
            Leave();
            if (mechType == null || !mechType.IsInstanceOfType(__instance)) Consume(__instance);
        }

        static Exception MechFinalizer(object __instance, Exception __exception)
        {
            while (active != null) Leave();
            Consume(__instance);
            return __exception;
        }

        // The sender's facts on this peer's Pathing, until Leave. False when there is nothing to mirror (a planning peer, no facts).
        static bool Enter(object actor) => Enter(actor, stored);

        static bool Enter(object actor, Dictionary<string, Facts> from)
        {
            if (active != null && ReferenceEquals(active.Actor, actor)) { active.Depth++; return true; }
            if (active != null) return false;
            string guid = GuidOf(actor);
            if (!ReferenceEquals(Combat(), storedCombat) || !from.TryGetValue(guid, out Facts f)) return false;
            object pathing = pathingProp.GetValue(actor, null);
            if (pathing == null) return false;
            var m = new Mirror { Actor = actor, Pathing = pathing, Path = currentPath.GetValue(pathing), MoveType = moveType.GetValue(pathing), CostLeft = (float)costLeft.GetValue(pathing), Depth = 1 };
            moveType.SetValue(pathing, Enum.ToObject(moveType.FieldType, f.MoveType));
            m.Grid = currentGrid.GetValue(pathing, null);
            if (m.Grid != null) { m.MaxDistance = (float)maxDistance.GetValue(m.Grid, null); setMaxDistance.Invoke(m.Grid, new object[] { f.MaxCost }); }
            var path = (IList)Activator.CreateInstance(currentPath.FieldType);
            if (f.Has) path.Add(FormatterServices.GetUninitializedObject(pathNode));
            currentPath.SetValue(pathing, path);
            costLeft.SetValue(pathing, f.CostLeft);
            active = m;
            return true;
        }

        static void Leave()
        {
            Mirror m = active;
            if (m == null || --m.Depth > 0) return;
            active = null;
            try
            {
                currentPath.SetValue(m.Pathing, m.Path);
                costLeft.SetValue(m.Pathing, m.CostLeft);
                if (m.Grid != null) setMaxDistance.Invoke(m.Grid, new object[] { m.MaxDistance });
                moveType.SetValue(m.Pathing, m.MoveType);
            }
            catch (Exception ex) { Row("restore-failed|" + GuidOf(m.Actor) + "|" + ex.Message); }
        }

        static void Consume(object actor)
        {
            string guid = GuidOf(actor);
            if (guid != "?") stored.Remove(guid);
        }

        static Facts Read(object actor)
        {
            object pathing = pathingProp.GetValue(actor, null);
            if (pathing == null) return null;
            object g = currentGrid.GetValue(pathing, null);
            return new Facts
            {
                Has = (bool)hasPath.GetValue(pathing, null) && currentPath.GetValue(pathing) is IList l && l.Count > 0,
                CostLeft = (float)costLeft.GetValue(pathing),
                MaxCost = g == null ? 0f : (float)maxDistance.GetValue(g, null),
                MoveType = Convert.ToInt32(moveType.GetValue(pathing), CultureInfo.InvariantCulture),
            };
        }

        static string InvocationActor(object invocation)
        {
            Type t = invocation.GetType();
            if (movementInvocation.IsAssignableFrom(t)) return t.GetProperty("ActorGUID", Inst)?.GetValue(invocation, null) as string;
            if (meleeInvocation != null && meleeInvocation.IsAssignableFrom(t)) return t.GetProperty("MechGUID", Inst)?.GetValue(invocation, null) as string;
            if (attackInvocation != null && attackInvocation.IsAssignableFrom(t)) return t.GetProperty("SourceGUID", Inst)?.GetValue(invocation, null) as string;
            return null;
        }

        static bool CoopCombat(object actor)
        {
            object net = netGame.GetValue(combatOf.GetValue(actor, null), null);
            return net != null && isCoop.GetValue(net, null) is bool b && b;
        }

        static object Combat()
        {
            object game = battleTechGame.GetValue(null, null);
            return game?.GetType().GetProperty("Combat", Inst)?.GetValue(game, null);
        }

        static object Find(object combat, string guid)
        {
            try { return findActor.Invoke(combat, new object[] { guid }); } catch { return null; }
        }

        static string GuidOf(object actor)
        {
            try { return guidProp.GetValue(actor, null) as string ?? "?"; } catch { return "?"; }
        }

        static void Row(string text) { if (++rows <= 200) log(Tag + "|" + text); }
    }
}
