// StrategicOperations' battle-armor, airlift, resupply and strafe state after a co-op resync or rejoin load (gh issue 24, snapshot-field
// sweep rows "StrategicOperations ModState"; Sam 2026-11-22: fix the not-carried class before 4B).
// StrategicOperations keeps which battle armor rides or swarms which unit (PositionLockMount, PositionLockSwarm, PositionLockGarrison,
// BADamageTrackers, PairingInfos, CurrentBattleArmorSquads), airlifts (AirliftTrackers), resupply (ResupplyShutdownPhases, TeamsWithResupply),
// command units and uses (CurrentCommandUnits, CommandUses, DeploymentAssetsStats, StoredCmdParams, PlayerSpawnGUIDs), strafes in flight
// (DeferredDespawnersFromStrafe), garrisons (GarrisonFriendlyTeam) and unit coordinates (CachedUnitCoordinates) in static ModState only;
// ModState.ResetAll at OnCombatGameDestroyed (SO CombatGameState_OnCombatGameDestroyed postfix) empties it on a guest's reload, and the host
// keeps it. Its entries are keyed by GUID, so ModStateCarry sends the host's values to a peer that has just loaded a snapshot.
// Not carried, and logged `so-coop|refused|...` at install: ModState.CommandAbilities (Ability objects), PendingStrafeWaves (holds an Ability,
// a Lance, the DataManager and defs), DeferredInvokeSpawns / DeferredInvokeBattleArmor (closures), the AI command queues AiCmds,
// AiDealWithBattleArmorCmds and StrategicActorTargetInvocationCmds (read only where the AI thinks, the host), and per-attack or HUD scratch
// (SwarmSuccessChance, DeSwarmMovementInfo, PendingPlayerCmdParams, overlay colours).
// Basis (inferred, untested): the carried entries are written when an ability or attack resolves, on every peer; no one-peer writer was
// traced, so nothing is sent with an invocation.
using System;
using System.Reflection;

namespace BattleTechPort
{
    public static class StrategicOperationsCoopStateSync
    {
        const string Tag = "so-coop";

        static readonly string[] Fields =
        {
            "PositionLockMount", "PositionLockSwarm", "PositionLockGarrison", "BADamageTrackers", "PairingInfos", "CurrentBattleArmorSquads",
            "AirliftTrackers", "ResupplyShutdownPhases", "TeamsWithResupply", "CurrentCommandUnits", "CommandUses", "DeploymentAssetsStats",
            "StoredCmdParams", "PlayerSpawnGUIDs", "DeferredDespawnersFromStrafe", "GarrisonFriendlyTeam", "CachedUnitCoordinates",
            // Listed to be refused and logged by name (not carryable): see the header.
            "CommandAbilities", "PendingStrafeWaves", "DeferredInvokeSpawns", "DeferredInvokeBattleArmor",
        };

        public static void Register(Action<string> logLine)
        {
            ModAssemblyHook.When("StrategicOperations", a => ModStateCarry.Install(Tag, a.GetType("StrategicOperations.Framework.ModState", false), Fields,
                "basis: written on ability/attack resolution on every peer (inferred); cleared by ModState.ResetAll at OnCombatGameDestroyed", logLine), logLine, Tag);
        }
    }
}
