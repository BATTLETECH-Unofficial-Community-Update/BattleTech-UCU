// CustomUnits' per-viewer stat in the co-op compares (gh issue 24, fault H; .hermes/reports/issue24-faultH-fix-1.md).
// CustomMechRepresentation.OnPlayerVisibilityChanged adds the stat CUSeenByPlayer to the unit's StatCollection and sets it when
// the local player first sees the unit; only its boss-appear animation reads it. Each peer sees through its own player, so a
// host that has seen an enemy the guest has not holds True where the guest holds False (or no stat at all), and the CMP-9
// MECHS compare started a resync for it (D-fix-h1, round 2, the enemy Viper). Only when an assembly named CustomUnits is loaded,
// this names CUSeenByPlayer in CoopCompareExclusions (UCU patch 164 and CoopDesyncDetector read it), so neither compare
// hashes it. The stat itself, and everything CustomUnits does with it, is unchanged.
using System;
using System.Reflection;

namespace BattleTechPort
{
    public static class CustomUnitsCoopCompare
    {
        const string Stat = "CUSeenByPlayer";

        public static void Register(Action<string> logLine)
        {
            ModAssemblyHook.When("CustomUnits", cu => Apply(cu, logLine), logLine, "customunits-coop-compare");
        }

        static void Apply(Assembly customUnits, Action<string> log)
        {
            FieldInfo name = customUnits.GetType("CustomUnits.CustomMechRepresentation", false)?.GetField("SeenByPlayer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            string value = name?.GetValue(null) as string;
            if (value != Stat)
            {
                log("customunits-coop-compare|skipped|shape changed SeenByPlayer=" + (value ?? "missing"));
                return;
            }
            CoopCompareExclusions.Add(Stat);
            log("customunits-coop-compare|installed|" + Stat + " left out of the co-op compares");
        }
    }
}
