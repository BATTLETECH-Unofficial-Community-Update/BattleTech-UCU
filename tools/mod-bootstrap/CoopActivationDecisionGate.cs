// Co-op turn-director decision gate (gh issue 24, fault L1; UCU patch 169; .hermes/reports/issue24-faultL1-fix-1.md).
// Patch 169 makes TurnDirector.OnTurnActorActivateComplete, in a co-op combat, publish a decision barrier and return, and
// TurnDirector.Update calls it again to choose once the barrier is answered (CMP169DeferDecision is false on that pass). A
// mod prefix that replaces the method's body (CBTBehaviorsEnhanced chooses itself and sets __runOriginal = false) skips
// that call, and each peer chooses at its own presentation-timed moment again. This prefix runs first: when the game is
// not over and CMP169DeferDecision() deferred the choice, it skips the original and, through __runOriginal, every later
// prefix that honours it (CBTBE returns at once). On the deciding pass it lets everything run. General adapter: patched
// unconditionally; outside a co-op combat CMP169DeferDecision is false and nothing changes. A DLL without patch 169 is
// left alone.
using System;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CoopActivationDecisionGate
    {
        const string OwnId = "battletech.port.coop-activation-gate";
        static Action<string> log;
        static MethodInfo deferDecision;
        static MethodInfo isMissionOver;
        static bool failedLogged;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("Assembly-CSharp", Apply, logLine, "coop-activation-gate");
        }

        static void Apply(Assembly game)
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type turnDirector = game.GetType("BattleTech.TurnDirector", false);
            MethodBase target = turnDirector?.GetMethod("OnTurnActorActivateComplete", inst, null, Type.EmptyTypes, null);
            deferDecision = turnDirector?.GetMethod("CMP169DeferDecision", inst, null, Type.EmptyTypes, null);
            isMissionOver = turnDirector?.GetProperty("IsMissionOver", inst)?.GetGetMethod(true);
            if (target == null || deferDecision == null || deferDecision.ReturnType != typeof(bool) || isMissionOver == null)
            {
                log("coop-activation-gate|skipped|shape changed OnTurnActorActivateComplete=" + (target != null)
                    + " CMP169DeferDecision=" + (deferDecision != null) + " IsMissionOver=" + (isMissionOver != null));
                return;
            }
            var harmony = new Harmony(OwnId);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(CoopActivationDecisionGate).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
            log("coop-activation-gate|installed|TurnDirector.OnTurnActorActivateComplete defers to the co-op decision barrier (patch 169) ahead of other prefixes");
        }

        static bool Prefix(object __instance, ref bool __runOriginal)
        {
            try
            {
                if (!__runOriginal || (bool)isMissionOver.Invoke(__instance, null)) return true;
                if (!(bool)deferDecision.Invoke(__instance, null)) return true;
                __runOriginal = false;
                return false;
            }
            catch (Exception ex)
            {
                if (!failedLogged)
                {
                    failedLogged = true;
                    log("coop-activation-gate|prefix-failed|" + ex);
                }
                return true;
            }
        }
    }
}
