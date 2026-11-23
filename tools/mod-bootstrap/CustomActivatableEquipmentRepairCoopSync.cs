// CustomActivatableEquipment's per-round damaged locations after a co-op resync or rejoin load (gh issue 24, snapshot-field sweep row
// "CAE RepairHelper.DamagedStructureLocationsThisTurn"; Sam 2026-11-22: fix the not-carried class before 4B).
// RepairHelper.DamagedStructureLocationsThisTurn (Dictionary<ICombatant, HashSet<int>>) gathers the locations a unit was damaged in since its
// last commit (AddDamagedLocation, from the damage patches on every peer); CommitCAEDamageData, in an AbstractActor.OnNewRound prefix, reads it
// to fire repair components with an AtEndOfTurn trigger and clears the unit's set. RepairHelper.Clear at OnCombatGameDestroyed empties it on a
// guest's reload, and its keys are actor objects the reload replaces; the host keeps it, so only the host repairs at the next round.
// Carried, not rebuilt from the saved `<location>_damageRound` stats: the Mech overload stores the chassis location
// (MechStructureRules.GetChassisLocationFromArmorLocation) while the stat is named for the armor location, front and rear both map to one
// chassis location, and the stat holds the round of the last hit, while the set holds hits since the unit's last commit at OnNewRound, which
// does not line up with a round number for a hit taken in the phase before that commit. The rebuild is not exact, so ModStateCarry sends the
// host's dictionary (keys by GUID) to a peer that has just loaded a snapshot.
using System;

namespace BattleTechPort
{
    public static class CustomActivatableEquipmentRepairCoopSync
    {
        const string Tag = "cae-coop";

        public static void Register(Action<string> logLine)
        {
            ModAssemblyHook.When("CustomActivatableEquipment", a => ModStateCarry.Install(Tag, a.GetType("CustomActivatableEquipment.RepairHelper", false),
                new[] { "DamagedStructureLocationsThisTurn" },
                "basis: AddDamagedLocation from damage on every peer, committed at OnNewRound; cleared by RepairHelper.Clear at OnCombatGameDestroyed", logLine), logLine, Tag);
        }
    }
}
