// Co-op determinism for what mods draw while a unit spawns (gh issue 24). In a co-op battle each peer spawns the same units, but a mod that
// hooks UnitSpawnPointGameLogic.Spawn or GetSpawnPosition draws from each peer's own UnityEngine.Random stream: FieldRepairs rolls random
// criticals and structure damage in a prefix on Spawn, MissionControl picks positions in a postfix on GetSpawnPosition. The peers then
// differ in a unit's position and pre-applied damage and the MECHS compare fails at round 1.
// General adapter, so it does nothing unless a mod really hooks the spot: during a co-op combat contract (Contract.IsCoopCombat) and only
// when Harmony reports at least one patch owner other than this adapter on Spawn or GetSpawnPosition, UnityEngine.Random is seeded from
// the contract GUID and the spawn point's GUID around each Spawn (our prefix runs before the others, our finalizer after them) and the
// peer's own state is put back afterwards. Single-player, an unmodded co-op battle, and a modded one where nothing hooks those methods
// are untouched. Which owners triggered it is logged once per contract.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CoopSpawnDeterminism
    {
        const string OwnId = "battletech.port.coop-spawn-seed";
        static Action<string> log;
        static string salt;                                   // contract GUID of the running co-op contract; null = not co-op
        static string ownersLoggedFor;
        static MethodBase spawn, getSpawnPosition;
        static MethodInfo initState;
        static PropertyInfo stateProp;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("Assembly-CSharp", Apply, logLine, "coop-spawn-seed");
        }

        static void Apply(Assembly game)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type contract = game.GetType("BattleTech.Contract", false);
            Type point = game.GetType("BattleTech.UnitSpawnPointGameLogic", false);
            Type random = ModAssemblyHook.GameType("UnityEngine.Random");
            initState = random?.GetMethod("InitState", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
            stateProp = random?.GetProperty("state", BindingFlags.Public | BindingFlags.Static);
            MethodBase begin = contract?.GetMethod("BeginRequestResources", all);
            spawn = point?.GetMethod("Spawn", all, null, new[] { typeof(bool) }, null);
            getSpawnPosition = point?.GetMethod("GetSpawnPosition", all, null, Type.EmptyTypes, null);
            if (begin == null || spawn == null || getSpawnPosition == null || initState == null || stateProp == null)
            {
                log("coop-spawn-seed|skipped|shape changed BeginRequestResources=" + (begin != null) + " Spawn=" + (spawn != null)
                    + " GetSpawnPosition=" + (getSpawnPosition != null) + " Random.InitState=" + (initState != null) + " Random.state=" + (stateProp != null));
                return;
            }
            var harmony = new Harmony(OwnId);
            HarmonyMethod Mine(string name, int priority) => new HarmonyMethod(typeof(CoopSpawnDeterminism).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };
            harmony.Patch(begin, prefix: Mine(nameof(BeginPrefix), Priority.First));          // records the contract GUID only
            harmony.Patch(spawn, prefix: Mine(nameof(SpawnPrefix), Priority.First), finalizer: Mine(nameof(Finalizer), Priority.Last));
            log("coop-spawn-seed|patched|seed around UnitSpawnPointGameLogic.Spawn when another mod hooks Spawn or GetSpawnPosition, in co-op combat only");
        }

        static void BeginPrefix(object __instance)
        {
            try
            {
                salt = null;
                object net = Get(Get(__instance, "BattleTechGame"), "NetworkGameInstance");
                if (net != null && Get(net, "IsCoopCombat") is bool coop && coop) salt = Get(__instance, "GUID") as string;
                ownersLoggedFor = null;
            }
            catch (Exception ex) { log("coop-spawn-seed|begin-prefix-failed|" + ex.Message); }
        }

        static void SpawnPrefix(object __instance, ref object __state)
        {
            __state = null;
            try
            {
                if (salt == null) return;
                var owners = ForeignOwners();
                if (owners.Count == 0) return;
                if (ownersLoggedFor != salt)
                {
                    ownersLoggedFor = salt;
                    log("coop-spawn-seed|owners|contract=" + salt + "|" + string.Join(", ", owners.ToArray()));
                    log("coop-spawn-seed|order|contract=" + salt + "|" + RunOrder());
                }
                string point = Get(__instance, "GUID") as string ?? "";
                object outer = stateProp.GetValue(null, null);
                ulong h = Fnv64(salt + "|spawn|" + point);
                initState.Invoke(null, new object[] { (int)(uint)(h ^ (h >> 32)) });
                __state = outer;
            }
            catch (Exception ex) { log("coop-spawn-seed|spawn-prefix-failed|" + ex.Message); }
        }

        static void Finalizer(object __state)
        {
            if (__state == null) return;
            try { stateProp.SetValue(null, __state, null); }
            catch (Exception ex) { log("coop-spawn-seed|restore-failed|" + ex.Message); }
        }

        // "<owner> on <method>" for every Harmony patch on Spawn or GetSpawnPosition that is not this adapter's own.
        static List<string> ForeignOwners()
        {
            var found = new List<string>();
            foreach (MethodBase m in new[] { spawn, getSpawnPosition })
            {
                Patches info = Harmony.GetPatchInfo(m);
                if (info == null) continue;
                foreach (var list in new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers })
                    foreach (Patch p in list)
                        if (p.owner != OwnId)
                        {
                            string text = p.owner + " on " + m.Name;
                            if (!found.Contains(text)) found.Add(text);
                        }
            }
            return found;
        }

        // The order Harmony runs the patches on Spawn in: prefixes first to last, then transpilers, then finalizers, each as owner(priority,index).
        // Our prefix must come before FieldRepairs' (whose rolls are drawn in its prefix) and our finalizer after every other finalizer.
        static string RunOrder()
        {
            Patches info = Harmony.GetPatchInfo(spawn);
            if (info == null) return "no patch info";
            string Row(IEnumerable<Patch> list)
            {
                var parts = new List<string>();
                foreach (Patch p in list) parts.Add(p.owner + "(prio=" + p.priority + ",idx=" + p.index + ")");
                return string.Join(" > ", parts.ToArray());
            }
            return "prefixes: " + Row(info.Prefixes) + " | finalizers: " + Row(info.Finalizers);
        }

        static ulong Fnv64(string text)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < text.Length; i++) h = (h ^ text[i]) * 1099511628211UL;
            return h;
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
