// CustomUnits' current contract on the co-op guest (gh issue 24, fault A; .hermes/reports/issue24-faultA-vehicle-read-1.md).
// CustomUnits prefixes MetadataDatabase.GetMatchingUnitDefs and adds "NoBiome_" + the contract's biome to the excluded tags, but only when its static
// CustomUnits.SimGameState_StartContract.CurrentContract is set, and only its postfix on SimGameState.StartContract sets it. A co-op guest launches the
// contract through GameContractMessageHandler.LaunchContract and never calls StartContract, so its static is null (or stale from an earlier battle): its unit
// queries keep the NoBiome_ defs the host drops, the candidate lists differ in length, UCU's seeded shuffle draws differently, and the peers spawn different
// units (A-on-h15: vehicledef_RHINO on the host, vehicledef_CARRIER_HEAVYMLRS on the guest for one spawn point).
// This adapter, only when an assembly named CustomUnits is loaded and only in a co-op combat (Contract.IsCoopCombat), sets that static to the starting contract
// in a prefix on Contract.BeginRequestResources, before any unit is generated, on both peers. Single player and games without CustomUnits are untouched.
using System;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomUnitsCoopContract
    {
        const string OwnId = "battletech.port.customunits-coop-contract";
        static Action<string> log;
        static FieldInfo currentField;
        static PropertyInfo currentProp;
        static int logged;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("CustomUnits", Apply, logLine, "customunits-coop-contract");
        }

        static void Apply(Assembly customUnits)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type holder = customUnits.GetType("CustomUnits.SimGameState_StartContract", false);
            currentField = holder?.GetField("CurrentContract", stat);
            currentProp = currentField == null ? holder?.GetProperty("CurrentContract", stat) : null;
            Type contract = ModAssemblyHook.GameType("BattleTech.Contract");
            MethodInfo begin = contract?.GetMethod("BeginRequestResources", all);
            Type target = currentField?.FieldType ?? currentProp?.PropertyType;
            if (begin == null || target == null || contract == null || !target.IsAssignableFrom(contract) || (currentProp != null && currentProp.GetSetMethod(true) == null))
            {
                log("customunits-coop-contract|skipped|shape changed holder=" + (holder != null) + " CurrentContract=" + (target != null)
                    + " BeginRequestResources=" + (begin != null));
                return;
            }
            new Harmony(OwnId).Patch(begin, prefix: new HarmonyMethod(typeof(CustomUnitsCoopContract).GetMethod(nameof(BeginPrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
            log("customunits-coop-contract|installed|CurrentContract set at Contract.BeginRequestResources in co-op combat");
        }

        static void BeginPrefix(object __instance)
        {
            try
            {
                object net = Get(Get(__instance, "BattleTechGame"), "NetworkGameInstance");
                if (!(net != null && Get(net, "IsCoopCombat") is bool coop && coop)) return;
                object before = currentField != null ? currentField.GetValue(null) : currentProp.GetValue(null, null);
                if (ReferenceEquals(before, __instance)) return;
                if (currentField != null) currentField.SetValue(null, __instance);
                else currentProp.SetValue(null, __instance, null);
                if (++logged <= 8) log("customunits-coop-contract|set|" + (Get(__instance, "GUID") as string) + "|was " + (before == null ? "null" : "another contract"));
            }
            catch (Exception ex) { log("customunits-coop-contract|prefix failed|" + ex.Message); }
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
