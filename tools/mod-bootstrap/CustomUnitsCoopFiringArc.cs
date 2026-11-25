// CustomUnits' firing-arc stat on co-op peers (family R, R-fix12: CUFiringArc was 0 on the host and 360 on the guest for five vehicles, [N8] MISMATCH round 2).
// CustomUnits turns every vehicle chassis JSON into a second, mech-shaped chassis ("fake vehicle"): FakeDatabase.AllContentPacksLoaded lists each VehicleChassisDef
// manifest entry again as a ChassisDef entry at the same path and id. The same file is therefore parsed twice, by two patched FromJSON methods that write ONE entry of
// VehicleCustomInfoHelper.vehicleChasissInfosDb (key = chassis id) and replace it each time (AddOrUpdate of a fresh UnitCustomInfo, FiringArc 0):
//   - BattleTech_VehicleChassisDef_fromJSON_Patch.Postfix sets FiringArc = HasTurret ? 360 : 90 when it is still 0 (an explicit CustomParts.FiringArc is kept);
//   - BattleTech_ChassisDef_fromJSON_Patch (the mech-shaped parse) never sets it, so its entry keeps 0.
// A FakeVehicleMech caches the entry that was current at its first GetCustomInfo, and AbstractActor_InitEffectStats.Postfix adds the stat CUFiringArc from that cache.
// Which parse wrote last is a loader race, so each peer's stat is 0 or 360/90 by load order: both peers 0 in most legs, 360 on one peer in R-fix12. The only
// reader, Vehicle/Mech_IsTargetPositionInFiringArc, overrides the game's arc whenever the stat is above 10, so the peers could also judge a target in or out of arc
// differently. Every co-op check hashes every actor stat, so the split starts a resync.
// This adapter makes the value order-independent: a prefix on ChassisDef.FromJSON reads the vehicle JSON before CustomUnits rewrites it and records, per chassis id, the
// value CustomUnits intends (the explicit CustomParts.FiringArc when non-zero, else 360 with a turret, 90 without; HBS sets HasTurret = Locations.Length > 4, so the
// Locations entries are counted). A postfix after CustomUnits' own on AbstractActor.InitEffectStats, in a co-op combat only, sets the stat to that value on a
// FakeVehicleMech whose stat differs. Both peers end on the value CustomUnits intends; single player and games without CustomUnits are untouched. Only HBS methods are
// patched (the chassis parse at CustomUnits' assembly load, the actor init at the first combat); no CustomUnits method is.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomUnitsCoopFiringArc
    {
        const string OwnId = "battletech.port.customunits-coop-firingarc";
        const string Tag = "cu-firingarc-coop";
        static Action<string> log;
        static readonly Dictionary<string, float> intended = new Dictionary<string, float>(StringComparer.Ordinal);
        static readonly Dictionary<string, bool> unknownLogged = new Dictionary<string, bool>(StringComparer.Ordinal);
        static MethodInfo getArc, setArc;
        static bool arcResolved;
        static int intendedRows, setRows;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("CustomUnits", Apply, logLine, Tag);
        }

        static void Apply(Assembly customUnits)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            MethodInfo fromJson = ModAssemblyHook.GameType("BattleTech.ChassisDef")?.GetMethod("FromJSON", all, null, new[] { typeof(string) }, null);
            fakeVehicleType = customUnits.GetType("CustomUnits.FakeVehicleMech", false);
            if (fromJson == null || fakeVehicleType == null)
            {
                log(Tag + "|skipped|shape changed ChassisDef.FromJSON=" + (fromJson != null) + " CustomUnits.FakeVehicleMech=" + (fakeVehicleType != null));
                return;
            }
            // The capture must exist before the first chassis is parsed, so it is installed now (an HBS method, patched at the mod's assembly load); the stat postfix
            // only matters once actors exist, so it waits for the first combat like every adapter that does reflection beyond this one method.
            new Harmony(OwnId).Patch(fromJson, prefix: new HarmonyMethod(typeof(CustomUnitsCoopFiringArc).GetMethod(nameof(CapturePrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
            ModAssemblyHook.AtFirstCombat(InstallStat, log, Tag);
        }

        static void InstallStat()
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            MethodInfo initStats = ModAssemblyHook.GameType("BattleTech.AbstractActor")?.GetMethod("InitEffectStats", all, null, Type.EmptyTypes, null);
            if (initStats == null) { log(Tag + "|skipped|shape changed AbstractActor.InitEffectStats not found"); return; }
            new Harmony(OwnId).Patch(initStats, postfix: new HarmonyMethod(typeof(CustomUnitsCoopFiringArc).GetMethod(nameof(StatPostfix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
            log(Tag + "|installed|BattleTech.ChassisDef::FromJSON (intended arc per vehicle chassis), BattleTech.AbstractActor::InitEffectStats (co-op: FakeVehicleMech CUFiringArc)");
        }

        static Type fakeVehicleType;

        static readonly string[] VehicleLocations = { "Front", "Left", "Right", "Rear", "Turret" };

        // Runs before CustomUnits' prefix, which rewrites the JSON into a mech chassis: only a vehicle-origin file still has its "Locations" array (entries named
        // Front, Left, Right, Rear, Turret) and CustomParts. A mech chassis JSON names its locations Head, LeftArm and so on and is skipped. Hand-parsed: the bootstrap
        // references mscorlib only (no Regex, no Newtonsoft).
        static void CapturePrefix(string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json)) return;
                int d = ValueStart(json, "Description", 0);
                int idAt = d < 0 ? -1 : ValueStart(json, "Id", d);
                string key = idAt < 0 ? null : QuotedAt(json, idAt);
                int loc = ValueStart(json, "Locations", 0);
                if (string.IsNullOrEmpty(key) || loc < 0) return;
                int entries = 0, vehicleNames = 0;
                for (int at = ValueStart(json, "Location", loc); at >= 0; at = ValueStart(json, "Location", at))
                {
                    entries++;
                    string name = QuotedAt(json, at);
                    if (name != null && Array.IndexOf(VehicleLocations, name) >= 0) vehicleNames++;
                }
                if (vehicleNames == 0) return;
                float arc = 0f;
                int arcAt = ValueStart(json, "FiringArc", 0);
                if (arcAt >= 0)
                {
                    int end = arcAt;
                    while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-')) end++;
                    float.TryParse(json.Substring(arcAt, end - arcAt), NumberStyles.Float, CultureInfo.InvariantCulture, out arc);
                }
                float value = arc != 0f ? arc : (entries > 4 ? 360f : 90f);
                lock (intended) intended[key] = value;
                if (++intendedRows <= 60) log(Tag + "|intended|" + key + "|" + value.ToString("R", CultureInfo.InvariantCulture) + "|locations=" + entries + "|explicit=" + (arc != 0f));
            }
            catch (Exception ex) { log(Tag + "|capture failed|" + ex.Message); }
        }

        // Index just after `"key"` + optional white space + `:` + optional white space, searching from `from`; -1 when absent.
        static int ValueStart(string s, string key, int from)
        {
            string quoted = "\"" + key + "\"";
            for (int at = s.IndexOf(quoted, from, StringComparison.Ordinal); at >= 0; at = s.IndexOf(quoted, at + quoted.Length, StringComparison.Ordinal))
            {
                int i = at + quoted.Length;
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length || s[i] != ':') continue;
                i++;
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                return i;
            }
            return -1;
        }

        // The string literal starting at s[at] (no escapes expected in ids and location names), or null.
        static string QuotedAt(string s, int at)
        {
            if (at >= s.Length || s[at] != '"') return null;
            int end = s.IndexOf('"', at + 1);
            return end < 0 ? null : s.Substring(at + 1, end - at - 1);
        }

        static void StatPostfix(object __instance)
        {
            try
            {
                if (__instance == null || fakeVehicleType == null || !fakeVehicleType.IsInstanceOfType(__instance)) return;
                object net = Get(Get(__instance, "Combat"), "NetworkGameInstance");
                if (!(net != null && Get(net, "IsCoopCombat") is bool coop && coop)) return;
                string guid = Get(__instance, "GUID") as string;
                string chassis = Get(Get(__instance, "PilotableActorDef"), "ChassisID") as string;
                if (chassis == null) return;
                float want;
                bool known;
                lock (intended) known = intended.TryGetValue(chassis, out want);
                if (!known)
                {
                    bool first; lock (unknownLogged) { first = !unknownLogged.ContainsKey(chassis); unknownLogged[chassis] = true; }
                    if (first) log(Tag + "|no-intended|" + guid + "|" + chassis + "|stat left as CustomUnits set it");
                    return;
                }
                if (!ResolveArc()) return;
                float have = (float)getArc.Invoke(null, new[] { __instance });
                if (have == want) return;
                setArc.Invoke(null, new[] { __instance, (object)want });
                if (++setRows <= 200)
                    log(Tag + "|set|" + guid + "|" + chassis + "|" + have.ToString("R", CultureInfo.InvariantCulture) + "->" + want.ToString("R", CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { log(Tag + "|postfix failed|" + (ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex).Message); }
        }

        // CustomAmmoCategories' UnitUnaffectionsActorStats.FiringArc(unit) / FiringArc(unit, value), the accessors CustomUnits itself uses; resolved at first use because
        // CustomAmmoCategories may load after CustomUnits.
        static bool ResolveArc()
        {
            if (arcResolved) return getArc != null && setArc != null;
            Type stats = ModAssemblyHook.GameType("CustAmmoCategories.UnitUnaffectionsActorStats");
            if (stats == null) return false;
            arcResolved = true;
            foreach (MethodInfo m in stats.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "FiringArc") continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length == 1 && m.ReturnType == typeof(float)) getArc = m;
                else if (p.Length == 2 && p[1].ParameterType == typeof(float)) setArc = m;
            }
            if (getArc == null || setArc == null) log(Tag + "|skipped|shape changed UnitUnaffectionsActorStats.FiringArc get=" + (getArc != null) + " set=" + (setArc != null));
            return getArc != null && setArc != null;
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
