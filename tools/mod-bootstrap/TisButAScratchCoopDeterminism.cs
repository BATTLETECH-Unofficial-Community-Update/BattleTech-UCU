// TisButAScratch injury rolls in co-op (gh issue 24 fault V1; .hermes/reports/issue24-faultV-read-1.md Q1).
// TisButAScratch picks each combat injury in its Pilot.InjurePilot postfix: the location (PilotInjuryManager.rollInjury,
// Random.Range over locations 2-7 plus re-injury weights), then the injury at that location, then its effects (bleeding rate,
// stat effects, the closest bleeding effect), all from the global UnityEngine.Random. Its bleed-out tick, a prefix on
// AbstractActor.OnActivationEnd, draws from it too (ApplyClosestBleedingEffect, and an InjurePilot of its own). UnityEngine.Random
// is not in step between co-op peers (the game draws it every frame, the AI where it thinks), so one injury gave the host a head
// injury and the guest a torso bleed (A-on-h19: Barotrauma vs Internal Bleeding on the same hit) and the MECHS compare resynced.
// This adapter, only when an assembly named TisButAScratch is loaded and only in a co-op combat, seeds UnityEngine.Random around
// those two game methods from contract GUID, scope, the unit's GUID (with the pilot's injuries before the call and the damage for
// InjurePilot), round and phase, and puts the peer's own state back in a finalizer, so an exception never leaves it changed. A
// nested scope (the tick's InjurePilot) restores the enclosing one. Every draw inside a scope, TisButAScratch's or another mod's,
// is then the same on every peer. Only game methods are patched, at the first combat (ModAssemblyHook.AtFirstCombat), so no
// TisButAScratch type is touched before its Init. Single player is untouched.
// shortcut: TisButAScratch's in-memory combatInjuriesMap and LastInjuryIDs (location weights) are not in a resync snapshot; after a
// mid-combat rejoin they can still differ. Upgrade if a leg shows an injury mismatch after a rejoin.
using System;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class TisButAScratchCoopDeterminism
    {
        const string OwnId = "battletech.port.tbas-coop";
        const string Tag = "tbas-coop";
        static Action<string> log;
        static PropertyInfo randomState, battleTechGame;
        static MethodInfo initState;
        static int rows;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("TisButAScratch", tbas => ModAssemblyHook.AtFirstCombat(Apply, logLine, Tag), logLine, Tag);
        }

        static void Apply()
        {
            const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type random = ModAssemblyHook.GameType("UnityEngine.Random");
            randomState = random?.GetProperty("state", stat);
            initState = random?.GetMethod("InitState", stat, null, new[] { typeof(int) }, null);
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", stat);
            MethodInfo injure = ModAssemblyHook.GameType("BattleTech.Pilot")?.GetMethod("InjurePilot", inst);
            MethodInfo activationEnd = ModAssemblyHook.GameType("BattleTech.AbstractActor")?.GetMethod("OnActivationEnd", inst, null, new[] { typeof(string), typeof(int) }, null);
            if (randomState == null || initState == null || battleTechGame == null || injure == null || activationEnd == null)
            {
                log(Tag + "|skipped|shape changed Random.state=" + (randomState != null) + " InitState=" + (initState != null) + " BattleTechGame="
                    + (battleTechGame != null) + " Pilot.InjurePilot=" + (injure != null) + " AbstractActor.OnActivationEnd=" + (activationEnd != null));
                return;
            }
            var harmony = new Harmony(OwnId);
            HarmonyMethod finalizer = Mine(nameof(Finalizer), Priority.Last);
            harmony.Patch(injure, prefix: Mine(nameof(InjurePrefix), Priority.First), finalizer: finalizer);
            harmony.Patch(activationEnd, prefix: Mine(nameof(ActivationEndPrefix), Priority.First), finalizer: finalizer);
            log(Tag + "|installed|UnityEngine.Random seeded in co-op combat around Pilot.InjurePilot, AbstractActor.OnActivationEnd");
        }

        static HarmonyMethod Mine(string name, int priority)
            => new HarmonyMethod(typeof(TisButAScratchCoopDeterminism).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };

        // __state holds the peer's own UnityEngine.Random.State (boxed) while a scope is open.
        static void InjurePrefix(object __instance, int dmg, ref object __state)
            => Open("injure", Get(Get(__instance, "ParentActor"), "GUID") + "|" + Get(__instance, "Injuries") + "|" + dmg, ref __state);

        static void ActivationEndPrefix(object __instance, ref object __state)
            => Open("activation-end", Get(__instance, "GUID") as string, ref __state);

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
                string full = (Get(Get(combat, "ActiveContract"), "GUID") as string) + "|tbas|" + scope + "|" + where;
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
