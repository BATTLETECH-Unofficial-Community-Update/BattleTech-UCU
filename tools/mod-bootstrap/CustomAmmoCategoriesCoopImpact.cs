// CustomAmmoCategories missile and shot placement in co-op (gh issue 24 fault Y; .hermes/reports/issue24-faultV3-read-1.md Q2 and Q5 option A).
// CAC replaces the game's AttackSequence.GenerateHitInfo with a prefix (AttackSequence_GenerateHitInfo.Prefix, __runOriginal = false). For
// indirect fire its ImpactPositionHelper.GetImpactPosition forces location 65536 (Invalid) and calls GetMissPositionIndirect, which places each
// missile with RandomOnSphere = the global UnityEngine.Random.onUnitSphere. The same code draws getMissInCircleToPosition, RandomInCone, the
// HitToLegs coin flips and GetSafeMissBuildingPosition from that generator, and its stray test then draws NetworkRandom only when a stray
// connects. UnityEngine.Random is not in step between co-op peers (the game draws it every frame), so the same missile landed on a different
// unit or location on each peer: 53 of 80 in W-L4 attack 4302290 (Heavy LRM Carrier, 4 x LRM20, on the Blackjack). The to-hit rolls matched.
// Damage, armour and stability then diverged on five mechs and a MECHS compare forced a resync. UCU's host-authoritative stray shots
// (CoopStrayShots) sit inside retail's GetIndividualHits and friends, which CAC's replacement never reaches.
// This adapter, only when an assembly named CustomAmmoCategories is loaded and only in a co-op combat, seeds UnityEngine.Random around the
// game's own BattleTech.AttackDirector.AttackSequence.GenerateHitInfo(Weapon, int groupIdx, int weaponIdx, int numberOfShots, bool indirectFire,
// float dodgedDamage) from contract GUID, attack sequence id, group and weapon index (all equal on both peers), and puts the peer's own state
// back in a finalizer. CAC's skipping prefix runs inside that bracket. Only a game method is patched, at the first combat
// (ModAssemblyHook.AtFirstCombat), so no CAC type is touched (fault M). Single player is untouched. No other adapter patches GenerateHitInfo,
// so this is the only scope on the method.
// Seed scopes on the rest of CAC's attack and map path (gh issue 24, CBTBE-6b compare 1; .hermes/reports/issue24-hornet-fix-1): the same
// bracket (save the state, InitState from an FNV hash of contract GUID + "|" + a key both peers share, restore in a Priority.Last finalizer),
// each with its own key:
//  - AttackSequence.GenerateToHitInfo() (game method): "cactohit|<sequence id>". CAC's postfix on it (AttackSequence_GenerateHitInfoAMS) runs
//    MainStray, the AMS interception (genreateAMSInterceptionInfo: the AMS hit rolls, so the shots fired and the ammo spent), frag strays,
//    AoE and ApplyVariance inside it. CBTBE-6b: one LRM5 volley, all 5 missiles downed on both peers, 14 AMS shots on the host and 9 on the
//    guest. The per-weapon brackets nested inside put back the state they found, so the outer sequence is unchanged by them.
//  - CustAmmoCategories.JammingEnabler.jammAMS (AMS jam roll): "cacjam|<sequence id>" inside AttackDirector.OnAttackComplete, "cacjam|r<round>"
//    inside TurnDirector.BeginNewRound (the two callers; a context prefix on each game method records which), else "cacjam|r<round>p<phase>".
//  - ResolveDamageHelper.ResolveJamming (weapon jam at damage resolution): "cacjamw|<sequence id>|<group>|<weapon>".
//  - WeaponRealizer.JammingEnabler.AttemptToAddJam/AttemptToRemoveJam and CustomAmmoCategories.AttemptToRemoveJam (OnActivationEnd):
//    "cacunjam|<method>|<actor>|<weapon uid>|r<round>p<phase>".
//  - DynamicMapHelper.calculateAbortPos (mines on a move; DetonateMineFieldsInRange nests in it) and calculateJumpDamage (mines on a jump):
//    "cacmine|abort|<actor>|r|p|<last waypoint x,z>" and "cacmine|jump|<actor>|r|p|<x,z>".
//  - DynamicMapHelper.FireTick (fire spread at round end; TryExpand nests in it): "cacfire|tick|r<round>p<phase>".
//  - MapTerrainHexCell.TryBurnCellSync, the MineField constructor (a laid minefield's misfires) and DeferredEffect.addEffect: keyed by hex
//    centre or target and effect. These three run from a frame queue (DyncamicMapAsyncProcessor.Update, DeferredEffect.LateUpdate), so the seed
//    makes the roll equal but not the frame it lands in.
// CAC methods are patched here only at the first combat, after CAC's Init (fault M). A scope that finds no co-op combat does nothing.
// Rows: "cac-impact-coop|<kind>-seed|<ident>|<seed>", up to 200 per kind.
// Left open (the read, Q5 option A):
//  - Direct fire's GetMissPositionDirect may raycast each peer's local scene (FineTuneTerrainHit, Physics.RaycastAll). Peers whose local colliders
//    differ could still split, the same family as the retail line-of-sight finding in patch 53.
//  - A missile trajectory starts at weaponRep.vfxTransforms[i].position (AdvWeaponHitInfoRec.GenerateTrajectory), a local animation pose; the AMS
//    range test samples that spline.
//  - CustomAmmoCategories.prosessExposion runs on a System.Threading.Timer thread 1.5 s after OnAttackComplete (JammingEnabler.Postfix): not
//    bracketed, a state swap off the main thread would race the main thread's draws.
//  - A resync that rebuilds a sequence's hit info calls GenerateHitInfo again with the same key, so it reproduces the same values; a leg should
//    confirm that rather than assume it.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomAmmoCategoriesCoopImpact
    {
        const string OwnId = "battletech.port.cac-impact-coop";
        const string Tag = "cac-impact-coop";
        static Action<string> log;
        static PropertyInfo randomState, battleTechGame;
        static MethodInfo initState;
        static FieldInfo sequenceId;
        static int rows;
        // Per patched method: the key part (after the contract GUID) and the row kind; null key = no scope.
        static readonly Dictionary<string, KeyValuePair<string, Func<object, object[], string>>> scopes = new Dictionary<string, KeyValuePair<string, Func<object, object[], string>>>();
        static readonly Dictionary<string, int> kindRows = new Dictionary<string, int>();
        static string jamContext;   // set inside OnAttackComplete / BeginNewRound, for jammAMS

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            // Patched at the first combat, after CustomAmmoCategories' Init (ModAssemblyHook.AtFirstCombat says why).
            ModAssemblyHook.When("CustomAmmoCategories", cac => ModAssemblyHook.AtFirstCombat(() => Apply(cac), logLine, Tag), logLine, Tag);
        }

        static void Apply(Assembly cac)
        {
            const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type random = ModAssemblyHook.GameType("UnityEngine.Random");
            randomState = random?.GetProperty("state", stat);
            initState = random?.GetMethod("InitState", stat, null, new[] { typeof(int) }, null);
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", stat);
            Type weapon = ModAssemblyHook.GameType("BattleTech.Weapon");
            Type sequence = ModAssemblyHook.GameType("BattleTech.AttackDirector+AttackSequence");
            sequenceId = sequence?.GetField("id", inst);
            MethodInfo generate = weapon == null ? null
                : sequence?.GetMethod("GenerateHitInfo", inst, null, new[] { weapon, typeof(int), typeof(int), typeof(int), typeof(bool), typeof(float) }, null);
            if (randomState == null || initState == null || battleTechGame == null || sequence == null || sequenceId == null || sequenceId.FieldType != typeof(int)
                || generate == null)
            {
                log(Tag + "|skipped|shape changed Random.state=" + (randomState != null) + " InitState=" + (initState != null) + " BattleTechGame="
                    + (battleTechGame != null) + " AttackDirector+AttackSequence=" + (sequence != null) + " id=" + (sequenceId != null)
                    + " GenerateHitInfo=" + (generate != null));
                return;
            }
            var harmony = new Harmony(OwnId);
            harmony.Patch(generate,
                prefix: new HarmonyMethod(typeof(CustomAmmoCategoriesCoopImpact).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(CustomAmmoCategoriesCoopImpact).GetMethod(nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
            log(Tag + "|installed|AttackDirector+AttackSequence.GenerateHitInfo seeded in co-op combat Random.state=True InitState=True BattleTechGame=True"
                + " AttackDirector+AttackSequence=True id=True GenerateHitInfo=True");

            // The seed scopes on the rest of the attack and map path (header). Each is installed or reported missing on its own.
            Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor");
            Type combatant = ModAssemblyHook.GameType("BattleTech.ICombatant");
            Type hitInfo = ModAssemblyHook.GameType("BattleTech.WeaponHitInfo");
            Type vector3 = ModAssemblyHook.GameType("UnityEngine.Vector3");
            Type effectData = ModAssemblyHook.GameType("BattleTech.EffectData");
            Type wrJam = cac.GetType("WeaponRealizer.JammingEnabler", false);
            Type cacMain = cac.GetType("CustAmmoCategories.CustomAmmoCategories", false);
            Type hex = cac.GetType("CustAmmoCategories.MapTerrainHexCell", false);
            Type mapHelper = cac.GetType("CustAmmoCategories.DynamicMapHelper", false);
            var found = new List<string>();
            Scope(harmony, found, "tohit", sequence.GetMethod("GenerateToHitInfo", inst, null, Type.EmptyTypes, null),
                (i, a) => "cactohit|" + sequenceId.GetValue(i), (i, a) => sequenceId.GetValue(i).ToString());
            Scope(harmony, found, "jam", cac.GetType("CustAmmoCategories.JammingEnabler", false)?.GetMethod("jammAMS", stat, null, Type.EmptyTypes, null),
                (i, a) => "cacjam|" + (jamContext ?? RoundPhase()), (i, a) => jamContext ?? RoundPhase());
            Scope(harmony, found, "jamw", hitInfo == null || combatant == null ? null
                    : cac.GetType("CustAmmoCategories.ResolveDamageHelper", false)?.GetMethod("ResolveJamming", stat, null, new[] { combatant, hitInfo.MakeByRefType() }, null),
                (i, a) => "cacjamw|" + HitKey(a[1]), (i, a) => HitKey(a[1]));
            foreach (MethodInfo m in new[] {
                wrJam?.GetMethod("AttemptToAddJam", stat, null, new[] { actor, weapon }, null),
                wrJam?.GetMethod("AttemptToRemoveJam", stat, null, new[] { actor, weapon }, null),
                cacMain?.GetMethod("AttemptToRemoveJam", stat, null, new[] { actor, weapon }, null) })
            {
                string name = m == null ? null : m.DeclaringType.Name + "." + m.Name;
                Scope(harmony, found, "unjam", m, (i, a) => "cacunjam|" + name + "|" + Guid(a[0]) + "|" + Get(a[1], "uid") + "|" + RoundPhase(),
                    (i, a) => Guid(a[0]) + "/" + Get(a[1], "uid"));
            }
            Scope(harmony, found, "mine", mapHelper?.GetMethods(stat).FirstOrNull(x => x.Name == "calculateAbortPos"),
                (i, a) => "cacmine|abort|" + Guid(a[0]) + "|" + RoundPhase() + "|" + LastWaypoint(a[2]), (i, a) => Guid(a[0]) + "/" + LastWaypoint(a[2]));
            Scope(harmony, found, "mine", vector3 == null ? null : mapHelper?.GetMethod("calculateJumpDamage", stat, null, new[] { actor, vector3 }, null),
                (i, a) => "cacmine|jump|" + Guid(a[0]) + "|" + RoundPhase() + "|" + XZ(a[1]), (i, a) => Guid(a[0]) + "/" + XZ(a[1]));
            Scope(harmony, found, "fire", mapHelper?.GetMethod("FireTick", stat, null, Type.EmptyTypes, null),
                (i, a) => "cacfire|tick|" + RoundPhase(), (i, a) => RoundPhase());
            Scope(harmony, found, "fire", hex?.GetMethod("TryBurnCellSync", inst, null, new[] { weapon, typeof(float), typeof(int), typeof(int) }, null),
                (i, a) => "cacfire|burn|" + XZ(Get(i, "center")) + "|" + Guid(Get(a[0], "parent")) + "|" + RoundPhase(), (i, a) => XZ(Get(i, "center")));
            Scope(harmony, found, "mine", hex == null ? null
                    : cac.GetType("CustAmmoCategories.MineField", false)?.GetConstructors(inst).FirstOrNull(c => c.GetParameters().Length == 4 && c.GetParameters()[0].ParameterType == hex),
                (i, a) => "cacmine|lay|" + XZ(Get(a[0], "center")) + "|" + RoundPhase(), (i, a) => "lay/" + XZ(Get(a[0], "center")));
            Scope(harmony, found, "defer", combatant == null || effectData == null ? null
                    : cac.GetType("CustAmmoCategories.DeferredEffect", false)?.GetMethod("addEffect", inst, null, new[] { combatant, effectData, typeof(float) }, null),
                (i, a) => "cacdefer|" + Guid(a[0]) + "|" + Get(Get(a[1], "Description"), "Id") + "|" + RoundPhase(), (i, a) => Guid(a[0]));
            // Context for jammAMS: which caller it runs under.
            MethodInfo attackComplete = ModAssemblyHook.GameType("BattleTech.AttackDirector")?.GetMethod("OnAttackComplete", inst);
            MethodInfo newRound = ModAssemblyHook.GameType("BattleTech.TurnDirector")?.GetMethod("BeginNewRound", inst, null, new[] { typeof(int) }, null);
            var ctxFinal = new HarmonyMethod(typeof(CustomAmmoCategoriesCoopImpact).GetMethod(nameof(ContextFinalizer), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last };
            if (attackComplete != null)
                harmony.Patch(attackComplete, prefix: new HarmonyMethod(typeof(CustomAmmoCategoriesCoopImpact).GetMethod(nameof(AttackCompleteContext), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First }, finalizer: ctxFinal);
            if (newRound != null)
                harmony.Patch(newRound, prefix: new HarmonyMethod(typeof(CustomAmmoCategoriesCoopImpact).GetMethod(nameof(NewRoundContext), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First }, finalizer: ctxFinal);
            log(Tag + "|scopes|" + string.Join(" ", found.ToArray()) + " jam-context=" + (attackComplete != null) + "/" + (newRound != null));
        }

        static MethodBase FirstOrNull<T>(this T[] items, Func<T, bool> pick) where T : MethodBase
        {
            foreach (T t in items) if (pick(t)) return t;
            return null;
        }

        static void Scope(Harmony harmony, List<string> found, string kind, MethodBase method, Func<object, object[], string> key, Func<object, object[], string> ident)
        {
            if (method == null) { found.Add(kind + ":missing"); return; }
            scopes[Id(method)] = new KeyValuePair<string, Func<object, object[], string>>(kind, key);
            idents[Id(method)] = ident;
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(CustomAmmoCategoriesCoopImpact).GetMethod(nameof(ScopePrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(CustomAmmoCategoriesCoopImpact).GetMethod(nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
            found.Add(kind + ":" + method.DeclaringType.Name + "." + method.Name);
        }

        static readonly Dictionary<string, Func<object, object[], string>> idents = new Dictionary<string, Func<object, object[], string>>();

        // The scope's lookup key: Harmony's __originalMethod is the same method, not always the same MethodBase instance.
        static string Id(MethodBase m) => m.DeclaringType.FullName + "::" + m;

        // __state holds the peer's own UnityEngine.Random.State (boxed) when this call opens a scope in a co-op combat.
        static void Prefix(object __instance, int groupIdx, int weaponIdx, ref object __state)
        {
            __state = null;
            try
            {
                object combat = CoopCombat();
                if (combat == null) return;
                int id = (int)sequenceId.GetValue(__instance);
                string key = (Get(Get(combat, "ActiveContract"), "GUID") as string) + "|cacimpact|" + id + "|" + groupIdx + "|" + weaponIdx;
                int seed = Fnv(key);
                __state = randomState.GetValue(null, null);
                initState.Invoke(null, new object[] { seed });
                if (++rows <= 200) log(Tag + "|seed|" + id + "|" + groupIdx + "|" + weaponIdx + "|" + seed);
            }
            catch (Exception e) { log(Tag + "|failed|prefix " + e.Message); }   // a saved state is still put back
        }

        static void ScopePrefix(MethodBase __originalMethod, object __instance, object[] __args, ref object __state)
        {
            __state = null;
            try
            {
                object combat = CoopCombat();
                if (combat == null || __originalMethod == null || !scopes.TryGetValue(Id(__originalMethod), out KeyValuePair<string, Func<object, object[], string>> scope)) return;
                string part = scope.Value(__instance, __args);
                if (part == null) return;
                int seed = Fnv((Get(Get(combat, "ActiveContract"), "GUID") as string) + "|" + part);
                __state = randomState.GetValue(null, null);
                initState.Invoke(null, new object[] { seed });
                kindRows.TryGetValue(scope.Key, out int n);
                kindRows[scope.Key] = ++n;
                if (n <= 200) log(Tag + "|" + scope.Key + "-seed|" + idents[Id(__originalMethod)](__instance, __args) + "|" + seed);
            }
            catch (Exception e) { log(Tag + "|failed|scope " + __originalMethod?.Name + " " + e.Message); }
        }

        static void Finalizer(object __state)
        {
            if (__state == null) return;
            try { randomState.SetValue(null, __state, null); }
            catch (Exception e) { log(Tag + "|failed|restore " + e.Message); }
        }

        static void AttackCompleteContext(object message, ref string __state)
        {
            __state = jamContext;
            try { jamContext = Get(message, "sequenceId")?.ToString(); } catch { jamContext = null; }
        }

        static void NewRoundContext(int round, ref string __state)
        {
            __state = jamContext;
            jamContext = "r" + round.ToString(CultureInfo.InvariantCulture);
        }

        static void ContextFinalizer(string __state) => jamContext = __state;

        static object CoopCombat()
        {
            object combat = Get(battleTechGame.GetValue(null, null), "Combat");
            object net = Get(combat, "NetworkGameInstance");
            return net != null && Get(net, "IsCoopCombat") is bool coop && coop ? combat : null;
        }

        static string RoundPhase()
        {
            object td = Get(Get(battleTechGame.GetValue(null, null), "Combat"), "TurnDirector");
            return "r" + Get(td, "CurrentRound") + "p" + Get(td, "CurrentPhase");
        }

        static string Guid(object o) => Get(o, "GUID") as string ?? "-";

        static string HitKey(object boxedHitInfo)
            => Get(boxedHitInfo, "attackSequenceId") + "|" + Get(boxedHitInfo, "attackGroupIndex") + "|" + Get(boxedHitInfo, "attackWeaponIndex");

        static string LastWaypoint(object waypoints)
        {
            if (!(waypoints is IList list) || list.Count == 0) return "none";
            return list.Count + "@" + XZ(Get(list[list.Count - 1], "Position"));
        }

        // A position as both peers hold it (orders carry the same floats), to a tenth of a metre.
        static string XZ(object v)
        {
            if (v == null) return "-";
            object x = Get(v, "x"), z = Get(v, "z");
            return x is float fx && z is float fz ? fx.ToString("F1", CultureInfo.InvariantCulture) + "," + fz.ToString("F1", CultureInfo.InvariantCulture) : "-";
        }

        static int Fnv(string key)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < key.Length; i++) h = (h ^ key[i]) * 1099511628211UL;
            return (int)(uint)(h ^ (h >> 32));
        }

        static object Get(object o, string name)
        {
            if (o == null) return null;
            const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, f | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
                FieldInfo fi = t.GetField(name, f | BindingFlags.DeclaredOnly);
                if (fi != null) return fi.GetValue(o);
            }
            return null;
        }
    }
}
