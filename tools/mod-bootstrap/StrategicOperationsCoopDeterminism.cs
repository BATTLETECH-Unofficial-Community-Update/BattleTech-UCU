// StrategicOperations co-op state that is not a random draw (reduced from the per-mod R adapter, post-4B family R; the maintainer, 2026-11-23
// D-20261123-4 = A). R1/R2/R3 in CoopRngSweepDeterminism now seed and redirect the draws this file used to; what remains here is the one
// piece the generic mechanism must not own:
// Support teams after a combat-save load (issue 24 N8, .hermes/reports/issue24-n8-teamturn-fix-1/report.md): retail links Team.SupportTeam
// only on a fresh build and never saves it, so after a load every team's SupportTeam is null and StrategicOperations'
// EncounterLayerData.BuildItemRegistry postfix adds a new "Opfor Support" AITeam per team. On a co-op guest's resync reload that gave 10
// turn actors the host does not have (CBTBE-3b, -5, -6: host 21, guest 31; 108 "Duplicate guid detected" ERRORs per three reloads).
// Before that postfix, in a co-op contract on a load, each team is relinked to the support team the snapshot already holds: Player 1 and
// Player 2 to HBS's fixed support GUIDs, any other team to the GUID the host minted for its support team on the fresh build. That GUID is
// keyed by the supported team's own GUID (NewGuidForTeam), not by a call counter, so the guest can compute it; the generic Guid.NewGuid
// redirect would key it by call order and the relink would miss. This file therefore keeps that one Guid site (IsKeyedSite tells the
// generic redirect to leave it alone) and nothing else.
// Single player, an unmodded co-op battle and a co-op battle without StrategicOperations are untouched; every other
// System.Guid.NewGuid is the generic redirect's.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace BattleTechPort
{
    public static class StrategicOperationsCoopDeterminism
    {
        const string OwnId = "battletech.port.strategicoperations-coop";
        const string SupportTeamSite = "StrategicOperations.Framework.Utils::CreateOrUpdateAISupportTeam";
        static Action<string> log;
        static string salt;                                   // contract GUID of the co-op contract being run; null = not co-op
        static readonly Dictionary<string, int> counters = new Dictionary<string, int>();
        static MethodInfo guidNewGuid, newGuidForTeam;
        static string lastSkipped;
        static Type teamType, aiTeamType;
        static MethodInfo setSupportTeam;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("StrategicOperations", Apply, logLine, "strategicoperations-coop");
        }

        static void Apply(Assembly mod)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Assembly game = ModAssemblyHook.GameType("BattleTech.Contract")?.Assembly;
            Type contract = game?.GetType("BattleTech.Contract", false);
            Type combat = game?.GetType("BattleTech.CombatGameState", false);
            MethodBase begin = contract?.GetMethod("BeginRequestResources", all);
            MethodBase first = combat?.GetMethod("FirstTimeInit", all);
            guidNewGuid = typeof(Guid).GetMethod("NewGuid", Type.EmptyTypes);
            newGuidForTeam = typeof(StrategicOperationsCoopDeterminism).GetMethod(nameof(NewGuidForTeam), BindingFlags.Static | BindingFlags.NonPublic);
            if (begin == null || first == null)
            {
                log("strategicoperations-coop|skipped|shape changed BeginRequestResources=" + (begin != null) + " FirstTimeInit=" + (first != null));
                return;
            }
            var harmony = new Harmony(OwnId);
            HarmonyMethod Mine(string name, int priority) => new HarmonyMethod(typeof(StrategicOperationsCoopDeterminism).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };
            harmony.Patch(begin, prefix: Mine(nameof(BeginPrefix), Priority.First));           // records the contract GUID only
            harmony.Patch(first, prefix: Mine(nameof(FirstTimeInitPrefix), Priority.First));   // and from its arguments when Begin has not run
            MethodBase buildRegistry = game.GetType("BattleTech.EncounterLayerData", false)?.GetMethod("BuildItemRegistry", all);
            teamType = game.GetType("BattleTech.Team", false);
            aiTeamType = game.GetType("AITeam", false) ?? game.GetType("BattleTech.AITeam", false);   // AITeam is in the global namespace (AITeam.cs:17)
            setSupportTeam = teamType?.GetMethod("SetSupportTeam", BindingFlags.Instance | BindingFlags.Public);
            if (buildRegistry == null || aiTeamType == null || setSupportTeam == null)
                log("strategicoperations-coop|support-relink|skipped|shape changed EncounterLayerData.BuildItemRegistry=" + (buildRegistry != null)
                    + " AITeam=" + (aiTeamType != null) + " Team.SetSupportTeam=" + (setSupportTeam != null));
            else
            {
                harmony.Patch(buildRegistry, postfix: Mine(nameof(SupportTeamRelinkPostfix), Priority.First));   // before StrategicOperations' own postfix
                log("strategicoperations-coop|support-relink|installed|EncounterLayerData.BuildItemRegistry");
            }
            // The keyed support-team GUID: CreateOrUpdateAISupportTeam(Team).
            Type utils = mod.GetType("StrategicOperations.Framework.Utils", false);
            MethodBase create = null;
            if (utils != null)
                foreach (MethodInfo m in utils.GetMethods(all))
                    if (m.Name == "CreateOrUpdateAISupportTeam" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "Team") { create = m; break; }
            if (create == null) log("strategicoperations-coop|support-relink|guid-site-missing|CreateOrUpdateAISupportTeam(Team)");
            else
            {
                try { harmony.Patch(create, transpiler: Mine(nameof(Transpiler), Priority.Normal)); }
                catch (Exception e) { log("strategicoperations-coop|support-relink|guid-site-failed|" + e.Message); }
            }
        }

        internal static bool IsKeyedSite(MethodBase m)
            => m != null && m.DeclaringType != null && m.DeclaringType.Name == "Utils" && m.Name == "CreateOrUpdateAISupportTeam";

        static void BeginPrefix(object __instance)
        {
            try
            {
                object net = Get(Get(__instance, "BattleTechGame"), "NetworkGameInstance");
                string guid = Get(__instance, "GUID") as string;
                if (net != null && Get(net, "IsCoopCombat") is bool coop && coop) Record(guid, "BeginRequestResources");
                else { salt = null; lastSkipped = guid; }
            }
            catch (Exception ex) { log("strategicoperations-coop|begin-prefix-failed|" + ex.Message); }
        }

        // FirstTimeInit(Contract contract, GameInstance game, string localPlayerTeamGuid): the same contract GUID and the co-op flag, for the case
        // that team creation runs before BeginRequestResources has recorded it.
        static void FirstTimeInitPrefix(object[] __args)
        {
            try
            {
                if (salt != null || __args == null || __args.Length < 2) return;
                object net = Get(__args[1], "NetworkGameInstance");
                string guid = Get(__args[0], "GUID") as string;
                if (net != null && Get(net, "IsCoopCombat") is bool coop && coop) Record(guid, "FirstTimeInit");
                else if (guid != lastSkipped) { lastSkipped = guid; log("strategicoperations-coop|skipped|not co-op combat|contract=" + (guid ?? "null")); }
            }
            catch (Exception ex) { log("strategicoperations-coop|first-time-init-prefix-failed|" + ex.Message); }
        }

        static void Record(string guid, string source)
        {
            if (string.IsNullOrEmpty(guid)) return;
            if (salt != guid) { lock (counters) counters.Clear(); }
            salt = guid;
            log("strategicoperations-coop|salt|" + source + "|contract=" + guid);
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            string site = original.DeclaringType?.FullName + "::" + original.Name;
            foreach (CodeInstruction ins in instructions)
            {
                if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && ins.operand as MethodInfo == guidNewGuid)
                {
                    var push = new CodeInstruction(OpCodes.Ldstr, site);
                    push.labels.AddRange(ins.labels);
                    push.blocks.AddRange(ins.blocks);
                    yield return push;
                    yield return new CodeInstruction(OpCodes.Ldarg_0);   // the Team
                    yield return new CodeInstruction(OpCodes.Call, newGuidForTeam);
                }
                else yield return ins;
            }
        }

        // EncounterLayerData.BuildItemRegistry(CombatGameState combat) postfix, ahead of StrategicOperations': see the header.
        static void SupportTeamRelinkPostfix(object combat)
        {
            try
            {
                if (salt == null || !(Get(combat, "IsLoadingFromSave") is bool loading) || !loading) return;
                var actors = Get(Get(combat, "TurnDirector"), "TurnActors") as System.Collections.IList;
                if (actors == null) return;
                var support = new Dictionary<string, object>();
                foreach (object a in actors)
                    if (aiTeamType.IsInstanceOfType(a) && Get(a, "GUID") is string g && !support.ContainsKey(g)) support[g] = a;
                int linked = 0, left = 0;
                foreach (object team in actors)
                {
                    if (!teamType.IsInstanceOfType(team) || Get(team, "SupportTeam") != null) continue;
                    string guid = Get(team, "GUID") as string;
                    if (string.IsNullOrEmpty(guid)) continue;
                    string want = guid == "bf40fd39-ccf9-47c4-94a6-061809681140" ? "9ed02e70-beff-4131-952e-49d366e2f7cc"
                        : guid == "757173dd-b4e1-4bb5-9bee-d78e623cc867" ? "ec3652ee-2f77-4a96-a9ee-55309fbf3d8d"
                        : KeyedTeamGuid(SupportTeamSite, guid).ToString();
                    if (support.TryGetValue(want, out object s) && !ReferenceEquals(s, team))
                    {
                        setSupportTeam.Invoke(team, new[] { s });
                        linked++;
                    }
                    else left++;
                }
                log("strategicoperations-coop|support-relink|load|linked=" + linked + "|left=" + left + "|actors=" + actors.Count + "|contract=" + salt);
            }
            catch (Exception ex) { log("strategicoperations-coop|support-relink|failed|" + ex.Message); }
        }

        static Guid KeyedTeamGuid(string site, string teamGuid)
        {
            string text = salt + "|so-guid|" + site + "|team|" + teamGuid;
            ulong a = Fnv64(text, 14695981039346656037UL), b = Fnv64(text, 1099511628211UL ^ 0x9E3779B97F4A7C15UL);
            var bytes = new byte[16];
            Array.Copy(BitConverter.GetBytes(a), 0, bytes, 0, 8);
            Array.Copy(BitConverter.GetBytes(b), 0, bytes, 8, 8);
            return new Guid(bytes);
        }

        // Called in place of Guid.NewGuid() inside CreateOrUpdateAISupportTeam(Team): keyed by the supported team's own GUID.
        static Guid NewGuidForTeam(string site, object team)
        {
            string teamGuid = Get(team, "GUID") as string;
            if (salt == null || string.IsNullOrEmpty(teamGuid))
            {
                int missed;
                lock (counters) { counters.TryGetValue("no-salt", out missed); counters["no-salt"] = ++missed; }   // every solo game gets here
                if (missed <= 5) log("strategicoperations-coop|guid-no-contract|" + site + "|minted a random GUID|n=" + missed);
                return Guid.NewGuid();
            }
            var guid = KeyedTeamGuid(site, teamGuid);
            log("strategicoperations-coop|guid|" + site + "|key=team " + teamGuid + " (" + (Get(team, "DisplayName") as string ?? "?") + ")|" + guid);
            return guid;
        }

        static ulong Fnv64(string text, ulong basis)
        {
            ulong h = basis;
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
