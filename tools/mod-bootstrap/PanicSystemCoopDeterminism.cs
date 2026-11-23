// PanicSystem panic and ejection saves in co-op (gh issue 24 fault W; .hermes/reports/issue24-faultW-read-1.md Q1).
// PanicSystem rolls each panic save (SavingThrows.SavedVsPanic: Random.Range(1, 100)), each ejection save (SavedVsEject) and a few
// smaller draws (klutz, eject phrase, the panic effects' stackItemUID) on the global UnityEngine.Random, from four game methods: its
// postfixes on AttackDirector.OnAttackComplete, ActorMovementSequence.CompleteOrders and MechMeleeSequence.CompleteOrders
// (TurnDamageTracker.hintAttackComplete), and its AbstractActor.OnActivationEnd prefix (the bleeding check and completedTurnFor).
// UnityEngine.Random is not in step between co-op peers, so the same volley left one peer's Centurion Unsettled (AccuracyModifier +1)
// and not the other's (B-L3-fix-6: attack 65975020 guest only, attack 38944742 host only; compares 210 and 311).
// This adapter, only when an assembly named PanicSystem is loaded and only in a co-op combat, seeds UnityEngine.Random around those
// game methods from the contract GUID, the scope, a key both peers share (the attack sequence id, the sequence's owning actor, the
// activating actor) and the round and phase, and puts the peer's own state back in a finalizer. Harmony runs finalizers after every
// postfix, so PanicSystem's priority-0 postfixes are inside the scope. Only game methods are patched, at the first combat
// (ModAssemblyHook.AtFirstCombat), so no PanicSystem type is touched (fault M). Single player is untouched.
// OnActivationEnd: TisButAScratchCoopDeterminism already seeds it (owner battletech.port.tbas-coop). Two seeded scopes on one method
// do not nest cleanly (Harmony orders finalizers like prefixes, so the outer one would restore first), so this adapter opens its
// own OnActivationEnd scope only when tbas-coop's prefix is not on the method, checked once at the first call; with it there, the
// panic draws there are already seeded (B-L3-fix-6: the TBM5's bleeding-forced save agreed on both peers under tbas-coop's scope).
// PanicSystem's in-memory TrackedActors (panic levels) and TurnDamageTracker state are not in a resync snapshot (fault W2):
// PanicSystemTrackerCoopSync sends the host's to a peer that loads one (CBTBE-5 compare 101: a panic effect on one peer under equal seeds).
using System;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class PanicSystemCoopDeterminism
    {
        const string OwnId = "battletech.port.panic-coop";
        const string Tag = "panic-coop";
        const string TbasId = "battletech.port.tbas-coop";
        static Action<string> log;
        static PropertyInfo randomState, battleTechGame;
        static MethodInfo initState, activationEnd;
        static int rows;
        static int activationEndMode;   // 0 not checked, 1 covered by tbas-coop, 2 own scope

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("PanicSystem", ps => ModAssemblyHook.AtFirstCombat(Apply, logLine, Tag), logLine, Tag);
        }

        static void Apply()
        {
            const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type random = ModAssemblyHook.GameType("UnityEngine.Random");
            randomState = random?.GetProperty("state", stat);
            initState = random?.GetMethod("InitState", stat, null, new[] { typeof(int) }, null);
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", stat);
            Type message = ModAssemblyHook.GameType("MessageCenterMessage");
            MethodInfo attackComplete = message == null ? null
                : ModAssemblyHook.GameType("BattleTech.AttackDirector")?.GetMethod("OnAttackComplete", inst, null, new[] { message }, null);
            MethodInfo moveComplete = ModAssemblyHook.GameType("BattleTech.ActorMovementSequence")?.GetMethod("CompleteOrders", inst, null, Type.EmptyTypes, null);
            MethodInfo meleeComplete = ModAssemblyHook.GameType("BattleTech.MechMeleeSequence")?.GetMethod("CompleteOrders", inst, null, Type.EmptyTypes, null);
            activationEnd = ModAssemblyHook.GameType("BattleTech.AbstractActor")?.GetMethod("OnActivationEnd", inst, null, new[] { typeof(string), typeof(int) }, null);
            if (randomState == null || initState == null || battleTechGame == null || attackComplete == null || moveComplete == null
                || meleeComplete == null || activationEnd == null)
            {
                log(Tag + "|skipped|shape changed Random.state=" + (randomState != null) + " InitState=" + (initState != null) + " BattleTechGame="
                    + (battleTechGame != null) + " AttackDirector.OnAttackComplete=" + (attackComplete != null) + " ActorMovementSequence.CompleteOrders="
                    + (moveComplete != null) + " MechMeleeSequence.CompleteOrders=" + (meleeComplete != null) + " AbstractActor.OnActivationEnd=" + (activationEnd != null));
                return;
            }
            var harmony = new Harmony(OwnId);
            HarmonyMethod finalizer = Mine(nameof(Finalizer), Priority.Last);
            harmony.Patch(attackComplete, prefix: Mine(nameof(AttackCompletePrefix), Priority.First), finalizer: finalizer);
            harmony.Patch(moveComplete, prefix: Mine(nameof(MoveCompletePrefix), Priority.First), finalizer: finalizer);
            harmony.Patch(meleeComplete, prefix: Mine(nameof(MeleeCompletePrefix), Priority.First), finalizer: finalizer);
            harmony.Patch(activationEnd, prefix: Mine(nameof(ActivationEndPrefix), Priority.First), finalizer: finalizer);
            log(Tag + "|installed|UnityEngine.Random seeded in co-op combat around AttackDirector.OnAttackComplete, ActorMovementSequence.CompleteOrders,"
                + " MechMeleeSequence.CompleteOrders, AbstractActor.OnActivationEnd (unless tbas-coop seeds it)");
        }

        static HarmonyMethod Mine(string name, int priority)
            => new HarmonyMethod(typeof(PanicSystemCoopDeterminism).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };

        // __state holds the peer's own UnityEngine.Random.State (boxed) while a scope is open.
        static void AttackCompletePrefix(object message, ref object __state)
            => Open("attack-complete", Convert.ToString(Get(message, "sequenceId")), ref __state);

        static void MoveCompletePrefix(object __instance, ref object __state)
            => Open("move", Get(Get(__instance, "owningActor"), "GUID") as string, ref __state);

        static void MeleeCompletePrefix(object __instance, ref object __state)
            => Open("melee", Get(Get(__instance, "owningActor"), "GUID") as string, ref __state);

        static void ActivationEndPrefix(object __instance, ref object __state)
        {
            __state = null;
            if (activationEndMode == 0)
            {
                bool covered = false;
                try
                {
                    Patches info = Harmony.GetPatchInfo(activationEnd);
                    if (info != null) foreach (Patch p in info.Prefixes) if (p.owner == TbasId) covered = true;
                }
                catch (Exception e) { log(Tag + "|activation-end|check-failed|" + e.Message); }
                activationEndMode = covered ? 1 : 2;
                log(Tag + "|activation-end|" + (covered ? "covered by tbas-coop" : "own scope"));
            }
            if (activationEndMode == 2) Open("activation-end", Get(__instance, "GUID") as string, ref __state);
        }

        static void Open(string scope, string key, ref object __state)
        {
            __state = null;
            try
            {
                object combat = Get(battleTechGame.GetValue(null, null), "Combat");
                object net = Get(combat, "NetworkGameInstance");
                if (net == null || !(Get(net, "IsCoopCombat") is bool coop) || !coop) return;
                object turn = Get(combat, "TurnDirector");
                string where = key + "|r" + Get(turn, "CurrentRound") + "|p" + Get(turn, "CurrentPhase");
                string full = (Get(Get(combat, "ActiveContract"), "GUID") as string) + "|panic|" + scope + "|" + where;
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < full.Length; i++) h = (h ^ full[i]) * 1099511628211UL;
                __state = randomState.GetValue(null, null);
                initState.Invoke(null, new object[] { (int)(uint)(h ^ (h >> 32)) });
                if (++rows <= 40) log(Tag + "|seed|" + scope + "|" + where);
            }
            catch (Exception e) { log(Tag + "|prefix-failed|" + scope + "|" + e.Message); }   // a saved state is still put back
        }

        static void Finalizer(object __state)
        {
            if (__state == null) return;
            try { randomState.SetValue(null, __state, null); }
            catch (Exception e) { log(Tag + "|restore-failed|" + e.Message); }
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
