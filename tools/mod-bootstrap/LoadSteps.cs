// The load-step registry (post-4B housekeeping, family L1; design .hermes/plans/2026-11-23-post4b-coop-adapter-generalization.md).
// What a combat-save or co-op snapshot load loses or re-adds is mod-specific knowledge (which index to rebuild, which static state the save
// never held), but the plumbing is one of three steps in the load, and this file is where each step has one hook and an ordered list of
// declarations. A new mod's lost state is named by the census (ModStateCoopCensus) and fixed by a row here, not by a new adapter file.
//  Phase 1, after hydrate: one Priority.Last postfix on Mech, Vehicle and Turret.Hydrate(refs, combat) runs AfterHydrate's steps on each
//    hydrated actor, before InitFromSave and InitAbilities read it.
//  Phase 2, after _Init on a load: one postfix on CombatGameState._Init (after the Harmony ids the steps name) runs AfterInit's steps for a
//    combat that is loading from a save.
//  Phase 3, guest snapshot apply: Carries, one CarryDecl per mod's lost state; ModStateCarry registers each with UCU's co-op side channel
//    (CoopInvocationSideData, patch 166) in this order, then the census, last. The guest applies each on receipt of the host's full state.
// Fault M timing (lessons/patch-mod-methods-after-mod-init): Register only registers callbacks. The two game-method hooks are patched at the
// first combat-save load (CreateCombatFromSave prefix, as ComponentOrderLoadCompat was), and the carries resolve their mod types at the first
// combat (AtFirstCombat), after every mod's Init. A step or declaration that matches nothing is logged `row-missing`/`absent`: not silence.
// Not here, and why: code that must re-run a mod's helpers or has its own timing stays hand-written (see the inventory,
// .hermes/reports/logic-post4b-load-1/inventory.md): MonsterMashupCoopLinkSync, StrategicOperationsCoopCommandSync (CommandAbilities),
// CustomAmmoCategoriesLoadModes (around AssignAmmoToWeapons), CustomAmmoCategoriesCoopArtillerySync (also rides invocations and calls
// the mod's own mode methods), CacDeferredEffectCoopSync and CacDynamicMapCoopSync (GameObjects, mod helpers), and the load-compat
// adapters that patch a different method or a mod's own method.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class LoadSteps
    {
        const string Tag = "load-steps";
        const string OwnId = "battletech.port.load-steps";

        sealed class Step
        {
            public string Name;
            public string[] RequiresAny;       // installed only when one of these assemblies is loaded (null: always)
            public string[] After;             // Harmony ids the phase-2 postfix runs after
            public Action<object, Action<string>> Run;
            public Step(string name, string[] requiresAny, string[] after, Action<object, Action<string>> run) { Name = name; RequiresAny = requiresAny; After = after; Run = run; }
        }

        // ---- the declarations, in order ----

        // Phase 1: the argument is the hydrated actor.
        static readonly Step[] AfterHydrate =
        {
            new Step("component-order-load", null, null, ComponentOrderLoadCompat.AfterHydrate),   // any co-op combat-save load (W-L7 compares 106, 319)
        };

        // Phase 2: the argument is the combat that is loading from a save.
        static readonly Step[] AfterInit =
        {
            new Step("effect-cache-load", new[] { "RogueTechPerfFixes", "CustomStatisticEffects" },
                new[] { EffectCacheLoadCompat.RtpfId, EffectCacheLoadCompat.CseId }, EffectCacheLoadCompat.AfterInit),   // issue 24 fault U
        };

        // Phase 3: what a guest's snapshot load drops and the host keeps. Not carried, by name (types the encoder refuses, or scratch):
        // StrategicOperations ModState.CommandAbilities (Ability objects; StrategicOperationsCoopCommandSync rebuilds them), PendingStrafeWaves
        // (an Ability, a Lance, the DataManager and defs), DeferredInvokeSpawns / DeferredInvokeBattleArmor (closures), the AI command queues
        // AiCmds, AiDealWithBattleArmorCmds and StrategicActorTargetInvocationCmds (read only where the AI thinks, the host), and per-attack or
        // HUD scratch (SwarmSuccessChance, DeSwarmMovementInfo, PendingPlayerCmdParams, overlay colours).
        static readonly CarryDecl[] Carries =
        {
            new CarryDecl
            {
                Tag = "so-coop", Assembly = "StrategicOperations",
                Basis = "basis: written on ability/attack resolution on every peer (inferred); cleared by ModState.ResetAll at OnCombatGameDestroyed",
                Statics = new[] { new CarryStatic { Type = "StrategicOperations.Framework.ModState", Names = new[] {
                    "PositionLockMount", "PositionLockSwarm", "PositionLockGarrison", "BADamageTrackers", "PairingInfos", "CurrentBattleArmorSquads",
                    "AirliftTrackers", "ResupplyShutdownPhases", "TeamsWithResupply", "CurrentCommandUnits", "CommandUses", "DeploymentAssetsStats",
                    "StoredCmdParams", "PlayerSpawnGUIDs", "DeferredDespawnersFromStrafe", "GarrisonFriendlyTeam", "CachedUnitCoordinates" } } },
            },
            new CarryDecl
            {
                Tag = "cae-coop", Assembly = "CustomActivatableEquipment",
                Basis = "basis: AddDamagedLocation from damage on every peer, committed at OnNewRound; cleared by RepairHelper.Clear at OnCombatGameDestroyed (carried, not rebuilt from the damageRound stats: the rebuild is not exact)",
                Statics = new[] { new CarryStatic { Type = "CustomActivatableEquipment.RepairHelper", Names = new[] { "DamagedStructureLocationsThisTurn" } } },
            },
            new CarryDecl
            {
                Tag = "cac-jamq", Assembly = "CustomAmmoCategories", Strict = true,
                Basis = "basis: written by the attack resolution on every peer (seeded by CoopRngSweepDeterminism), drained at TurnDirector.BeginNewRound; emptied by ClearJammingInfo at OnCombatGameDestroyed (CBTBE-9 compare 264)",
                Statics = new[] { new CarryStatic { Type = "CustAmmoCategories.CustomAmmoCategories", Names = new[] { "jammAMSQueue", "jammQueue", "ammoExposionQueue" } } },
            },
            new CarryDecl
            {
                Tag = "panic-coop", Assembly = "PanicSystem",
                Basis = "basis: written by PanicSystem's own patches on every peer, emptied by TurnDamageTracker.Reset at OnCombatGameDestroyed (fault W2); a panic level is set on its field, not through PilotTracker's setter, because the snapshot already carries the panic effects",
                Statics = new[]
                {
                    new CarryStatic { Type = "PanicSystem.Components.TurnDamageTracker", Optional = true, Names = new[] {
                        "activationVictims", "attacker", "turnStartArmor", "turnStartStructure", "turnExternalHeatAccumulator", "ejectedActors" } },
                    // PanicSystem 4.0.0 (BEX:T) has no TurnDamageTracker: the external heat it counts until the next panic check.
                    new CarryStatic { Type = "PanicSystem.Patches.Mech_AddExternalHeat_Patch", Optional = true, Names = new[] { "heatDamage" } },
                    new CarryStatic { Type = "PanicSystem.Components.Controller", Names = new[] { "TrackedActors" } },
                },
            },
            new CarryDecl
            {
                // L2-b round 5 (Issue 24 reopened, accepted as family L's): Abilifier keeps each pilot's resolve in the process static
                // PilotResolveTracker._instance.pilotResolveDict (rGUID -> PilotResolveInfo: PilotResolve, CanInspire, Predicting, PilotMaxResolve,
                // PredictedResolve) and selectedAbilityResolveCost. Neither is in a save, and the host plays on during a rejoin hold, so a guest that
                // loads late misses resolve changes (round 3's baseline +10, then the host-only Inspired). The singleton's instance fields are all
                // encodable (an int and a Dictionary<string, PilotResolveInfo> of ints and bools): the guest's instance is rebuilt from the host's.
                Tag = "abl-resolve", Assembly = "Abilifier",
                Basis = "basis: ModifyResolve at the round-begin prefix on every peer; Initialize at Abilifier's Init and reset at Contract_CompleteContract; not in a save (Abilifier.dll PilotResolveTracker)",
                Statics = new[] { new CarryStatic { Type = "Abilifier.Framework.PilotResolveTracker", Names = new[] { "_instance" } } },
            },
            new CarryDecl
            {
                Tag = "actor-fields", Assembly = "Assembly-CSharp",
                Basis = "basis: [JsonSerialized] only, so a save drops them (CBTBE-6b compares 104, 206; CBTBE-7): Mech._tempHeat and _isBecomingProne, AbstractActor._flaggedForKnockdown, _flaggedForDeath, _hasHandledDeath; occupiedDesignMask stays with CoopActorTransientStateSync",
                ActorFields = new[]
                {
                    new CarryActorFields { Type = "BattleTech.Mech", Names = new[] { "_tempHeat", "_isBecomingProne" } },
                    new CarryActorFields { Type = "BattleTech.AbstractActor", Names = new[] { "_flaggedForKnockdown", "_flaggedForDeath", "_hasHandledDeath" } },
                },
            },
        };

        // ---- registration ----

        static Action<string> log;
        static bool hooksInstalled;
        static readonly List<Step> hydrateSteps = new List<Step>();
        static readonly List<Step> initSteps = new List<Step>();
        static PropertyInfo loadingProp;

        // Callback registrations only (fault M). Called once from ModBootstrap.
        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("Assembly-CSharp", game =>
            {
                ArmLoadHooks(game);
                ModAssemblyHook.AtFirstCombat(InstallCarries, logLine, Tag);
            }, logLine, Tag);
        }

        // Game method only: the first combat-save load installs phases 1 and 2 before any actor hydrates.
        static void ArmLoadHooks(Assembly game)
        {
            MethodInfo create = game.GetType("BattleTech.GameInstance", false)?.GetMethod("CreateCombatFromSave", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (create == null) { log(Tag + "|row-missing|GameInstance.CreateCombatFromSave not found: phases 1 and 2 not installed"); return; }
            PatchBeforeLoad(create);
            log(Tag + "|armed|GameInstance.CreateCombatFromSave");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void PatchBeforeLoad(MethodInfo create)
        {
            new Harmony(OwnId).Patch(create, prefix: new HarmonyMethod(typeof(LoadSteps).GetMethod(nameof(BeforeCombatLoad), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        static void BeforeCombatLoad()
        {
            if (hooksInstalled) return;
            hooksInstalled = true;
            try { InstallLoadHooks(); }
            catch (Exception ex) { log(Tag + "|FAILED|" + ex); }
        }

        static void InstallLoadHooks()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type combat = ModAssemblyHook.GameType("BattleTech.CombatGameState");
            Type refs = ModAssemblyHook.GameType("BattleTech.Save.Test.SerializableReferenceContainer");
            foreach (Step s in AfterHydrate) if (Present(s)) hydrateSteps.Add(s);
            foreach (Step s in AfterInit) if (Present(s)) initSteps.Add(s);
            var harmony = new Harmony(OwnId);
            if (hydrateSteps.Count > 0)
            {
                string patched = "";
                foreach (string name in new[] { "BattleTech.Mech", "BattleTech.Vehicle", "BattleTech.Turret" })
                {
                    MethodInfo m = refs == null || combat == null ? null : ModAssemblyHook.GameType(name)?.GetMethod("Hydrate", inst, null, new[] { refs, combat }, null);
                    if (m == null) { log(Tag + "|row-missing|phase 1|" + name + ".Hydrate(refs, combat) not found"); continue; }
                    PatchPostfix(harmony, m, nameof(HydratePostfix), Priority.Last, null);
                    patched += " " + name;
                }
                log(Tag + "|installed|phase 1 after-hydrate|steps=" + Names(hydrateSteps) + "|Priority.Last postfix on" + patched + ".Hydrate");
            }
            if (initSteps.Count > 0)
            {
                loadingProp = combat?.GetProperty("IsLoadingFromSave", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo init = combat?.GetMethod("_Init", inst);
                if (init == null || loadingProp == null) { log(Tag + "|row-missing|phase 2|CombatGameState._Init=" + (init != null) + " IsLoadingFromSave=" + (loadingProp != null)); return; }
                var after = new List<string>();
                foreach (Step s in initSteps) if (s.After != null) foreach (string id in s.After) if (!after.Contains(id)) after.Add(id);
                PatchPostfix(harmony, init, nameof(InitPostfix), -1, after.ToArray());   // priority unset, as EffectCacheLoadCompat patched it
                log(Tag + "|installed|phase 2 after-init|steps=" + Names(initSteps) + "|postfix on CombatGameState._Init after " + string.Join(",", after.ToArray()));
            }
        }

        static bool Present(Step s)
        {
            if (s.RequiresAny == null) return true;
            foreach (string name in s.RequiresAny) if (ModStateCarry.FindAssembly(name) != null) return true;
            log(Tag + "|absent|" + s.Name + "|none of " + string.Join(",", s.RequiresAny) + " is loaded");
            return false;
        }

        static string Names(List<Step> steps)
        {
            var names = new List<string>();
            foreach (Step s in steps) names.Add(s.Name);
            return string.Join(",", names.ToArray());
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void PatchPostfix(Harmony harmony, MethodInfo target, string postfix, int priority, string[] after)
        {
            var hm = new HarmonyMethod(typeof(LoadSteps).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic));
            if (priority >= 0) hm.priority = priority;
            if (after != null && after.Length > 0) hm.after = after;
            harmony.Patch(target, postfix: hm);
        }

        static void HydratePostfix(object __instance) => Run(hydrateSteps, __instance);

        static void InitPostfix(object __instance)
        {
            try { if (!(loadingProp.GetValue(__instance, null) is bool loading && loading)) return; }
            catch (Exception ex) { log(Tag + "|FAILED|phase 2 loading check|" + ex.Message); return; }
            Run(initSteps, __instance);
        }

        static void Run(List<Step> steps, object arg)
        {
            foreach (Step s in steps)
            {
                try { s.Run(arg, log); }
                catch (Exception ex) { log(Tag + "|step-failed|" + s.Name + "|" + ex); }
            }
        }

        // First combat, after every mod's Init: phase 3 in order, the census last (its apply must see the carried values).
        static void InstallCarries()
        {
            long start = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            int members = 0, installed = 0;
            foreach (CarryDecl d in Carries)
            {
                int n = ModStateCarry.Install(d, log);
                members += n;
                if (n > 0) installed++;
            }
            log(Tag + "|installed|phase 3 guest-apply|declarations=" + Carries.Length + "|installed=" + installed + "|members=" + members
                + "|ms=" + (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond - start));
            ModStateCoopCensus.Arm(log);
        }
    }
}
