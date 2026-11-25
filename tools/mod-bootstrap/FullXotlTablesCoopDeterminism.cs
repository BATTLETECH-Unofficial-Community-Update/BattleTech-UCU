// FullXotlTables (BEX:T) co-op unit selection (.hermes/reports/logic-bext-fixes-20261122 G6).
// FullXotlTables prefixes UnitSpawnPointOverride.RequestUnit and, for every tagged mech or vehicle with a date, skips the original and picks
// the unit from its own era and faction tables (XotlTable.RequestUnit, FactionTable), drawing UnityEngine.Random.Range. The CMP-7 seeded
// shuffle (Contract.CoopSeededShuffle in SelectTaggedUnitDef) never runs for those units, so each co-op peer drew its own global stream and
// the two built different opposing lances from identical system tags, company tags, date and candidate pool (BEXT-G6-probe2).
// While Contract.CoopSeedSalt is set (BeginRequestResources sets it to the contract GUID in a co-op combat and to null otherwise), a
// Priority.First prefix on RequestUnit saves UnityEngine.Random.state and seeds it from FNV(salt | "xotl" | lanceName | unitIndex), the key
// CMP-7 uses for its own unit shuffle, and a Priority.Last finalizer puts the peer's own state back, so FullXotlTables' prefix and the
// original both draw inside the scope. XotlTable also keeps the last three mechs it picked (lastThreeMechs, on the Core.xotlTables instance)
// and re-rolls a pick found there, so a peer's earlier picks change its next ones (BEXT-G6-fix: equal seeds, two different enemy mechs).
// At the first RequestUnit of each co-op contract (a new salt) that list is cleared on both peers; the contract's own picks then fill it
// in the same order on both (GenerateLance runs synchronously, spawner by spawner). Outside co-op the salt is null and nothing changes.
// Rows: "xotl-coop|history-reset|<n cleared>" per contract and "xotl-coop|seed|<lance>|<index>|<seed>", up to 80.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BattleTechPort
{
    public static class FullXotlTablesCoopDeterminism
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static Action<string> log;
        static FieldInfo salt, tables, history;
        static string lastSalt;
        static PropertyInfo randomState;
        static MethodInfo initState;
        static int rows;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("FullXotlTables", Apply, log, "xotl-coop");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Apply(Assembly mod)
        {
            Type usp = ModAssemblyHook.GameType("BattleTech.Framework.UnitSpawnPointOverride"), random = ModAssemblyHook.GameType("UnityEngine.Random");
            salt = ModAssemblyHook.GameType("BattleTech.Contract")?.GetField("CoopSeedSalt", All);
            randomState = random?.GetProperty("state", BindingFlags.Static | BindingFlags.Public);
            initState = random?.GetMethod("InitState", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(int) }, null);
            tables = mod.GetType("FullXotlTables.Core")?.GetField("xotlTables", All);
            history = mod.GetType("FullXotlTables.XotlTable")?.GetField("lastThreeMechs", All);
            MethodInfo request = null;
            if (usp != null) foreach (MethodInfo m in usp.GetMethods(All | BindingFlags.DeclaredOnly)) if (m.Name == "RequestUnit" && m.GetParameters().Length == 8) request = m;
            if (request == null || salt == null || randomState == null || initState == null || tables == null || history == null)
            {
                log("xotl-coop|skipped|shape changed RequestUnit=" + (request != null) + " CoopSeedSalt=" + (salt != null) + " Random.state=" + (randomState != null) + " InitState=" + (initState != null)
                    + " Core.xotlTables=" + (tables != null) + " lastThreeMechs=" + (history != null));
                return;
            }
            new Harmony("battletech.port.xotl-coop").Patch(request,
                prefix: new HarmonyMethod(typeof(FullXotlTablesCoopDeterminism).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(FullXotlTablesCoopDeterminism).GetMethod(nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
            log("xotl-coop|installed|UnitSpawnPointOverride.RequestUnit seeded per lance and unit index while Contract.CoopSeedSalt is set");
        }

        static void Prefix(string lanceName, int unitIndex, out object __state)
        {
            __state = null;
            string s = salt.GetValue(null) as string;
            if (s == null) return;
            if (s != lastSalt)
            {
                lastSalt = s;
                var list = history.GetValue(tables.GetValue(null)) as System.Collections.IList;
                int n = list == null ? -1 : list.Count;
                list?.Clear();
                log("xotl-coop|history-reset|" + n);
            }
            uint hash = 2166136261u;
            string text = s + "|xotl|" + lanceName + "|" + unitIndex;
            for (int i = 0; i < text.Length; i++) hash = (hash ^ text[i]) * 16777619u;
            __state = randomState.GetValue(null, null);
            initState.Invoke(null, new object[] { (int)hash });
            if (rows++ < 80) log("xotl-coop|seed|" + lanceName + "|" + unitIndex + "|" + hash.ToString("X8"));
        }

        static Exception Finalizer(Exception __exception, object __state)
        {
            if (__state != null) randomState.SetValue(null, __state, null);
            return __exception;
        }
    }
}
