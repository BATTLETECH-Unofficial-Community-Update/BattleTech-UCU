// StrategicOperations adapter for co-op (gh issue 24). StrategicOperations builds its own AI teams ("Opfor Support" per faction team, a custom
// team) and its strafe, flare and command-lance units with System.Guid.NewGuid, so each co-op peer ended with other team GUIDs. Retail's
// MECHS compare carries each mech's "Team GUID" (MechStatsReporter) and N8 keys a team entry by GUID: with nine random team GUIDs per peer
// every team pair differed even when every unit's armor, structure, position and effects were equal (pair-rp-modded9: state dump equal for all
// 9 actors, 18 `*team:` pairs and MECHS still NOT MATCHING).
// While a co-op combat runs (the contract GUID is recorded at Contract.BeginRequestResources or, when that has not run yet, from
// CombatGameState.FirstTimeInit's own arguments) and only when an assembly named StrategicOperations is loaded, every StrategicOperations
// method that calls Guid.NewGuid gets a deterministic GUID instead: FNV over contract GUID, the method and a per-method counter (the same
// scheme as the MissionControl adapter; the order in which a method is reached is the same on both peers because the calls are made from
// invocations and the team list, both identical). Each minted GUID is logged with its site and counter so a drift shows on one peer.
// Random rolls (issue24-faultA, h3): StrategicOperations draws from its own StrategicOperations.Mod.Random (public static readonly, a
// time-seeded `new System.Random()` in Mod's static constructor). At Team.AddUnit, AI_Utils.GenerateAIStrategicAbilities rolls for AI
// battle armor and strategic abilities: in h3 the guest spawned two Dragoon BA squads the host never had (15 vs 17 actors). Its other
// readers (deswarm, swarm, strafe targets, BA death and mount, damage-location prefixes) run in combat. Every
// `ldsfld StrategicOperations.Mod::Random` in StrategicOperations' own methods becomes a call that, during a co-op contract, returns a
// fresh System.Random for that one read, seeded from contract GUID | site (method and read index) | key GUID (the method's actor, or the
// sequence's owner, attacker or team) | the number of earlier reads of that site with that key. Each read is followed by one NextDouble().
// Outside a co-op contract the call returns the mod's own instance, so single-player draws are the same draws. UnityEngine.Random is also
// seeded inside GenerateAIStrategicAbilities (its BA spawn-weight and mount picks go through Utilities.GetRandomElement), and its state is put
// back afterwards.
// Support teams after a combat-save load (issue 24 N8, .hermes/reports/issue24-n8-teamturn-fix-1/report.md): retail links Team.SupportTeam
// only on a fresh build (EncounterLayerData.cs:1236 guard, :1274, :1298) and never saves it (Team.cs:205), so after a load every team's
// SupportTeam is null and StrategicOperations' EncounterLayerData.BuildItemRegistry postfix (Utils.FetchAISupportTeam ->
// CreateOrUpdateAISupportTeam) adds a new "Opfor Support" AITeam per team. On a co-op guest's resync reload that gave 10 turn actors the
// host does not have (CBTBE-3b, -5, -6: host 21, guest 31; N8 *team/*turn:global; 108 "Duplicate guid detected" ERRORs per three reloads).
// Before that postfix, in a co-op contract on a load, each team is relinked to the support team the snapshot already holds: Player 1 and
// Player 2 to HBS's fixed support GUIDs, any other team to the GUID this adapter gave its support team on the fresh build (NewGuidForTeam).
// The host decides the roster; the guest creates nothing. A team with no such loaded actor is left as it was.
// Single-player, an unmodded co-op battle and a co-op battle without StrategicOperations are untouched; System.Guid.NewGuid is never patched.
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
        static Action<string> log;
        static string salt;                                   // contract GUID of the co-op contract being run; null = not co-op
        static readonly Dictionary<string, int> counters = new Dictionary<string, int>();
        static readonly Dictionary<MethodBase, int> expectedCalls = new Dictionary<MethodBase, int>();
        static readonly Dictionary<MethodBase, int> replacedCalls = new Dictionary<MethodBase, int>();
        static readonly List<string> skipped = new List<string>();
        static MethodInfo guidNewGuid, newGuidFor, newGuidForTeam;
        static int noSalt;
        static string lastSkipped;                            // contract GUID last logged as not co-op (one line per contract)
        // Mod.Random redirection
        static FieldInfo randomField;                         // StrategicOperations.Mod.Random
        static MethodInfo seededRandom;
        static MethodInfo initState;                          // UnityEngine.Random.InitState(int)
        static PropertyInfo stateProp;                        // UnityEngine.Random.state
        static readonly Dictionary<MethodBase, int> expectedReads = new Dictionary<MethodBase, int>();
        static readonly Dictionary<MethodBase, int> replacedReads = new Dictionary<MethodBase, int>();
        static readonly List<string> skippedReads = new List<string>();
        static readonly Dictionary<string, int> readCounts = new Dictionary<string, int>();   // site|key -> reads so far this contract
        static readonly Dictionary<string, int[]> siteStats = new Dictionary<string, int[]>();   // site -> {scopes, draws}
        static int drawLines, unityScopes;
        const int DrawLineCap = 5000;                         // per contract; the count lines still carry every scope

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
            newGuidFor = typeof(StrategicOperationsCoopDeterminism).GetMethod(nameof(NewGuidFor), BindingFlags.Static | BindingFlags.NonPublic);
            newGuidForTeam = typeof(StrategicOperationsCoopDeterminism).GetMethod(nameof(NewGuidForTeam), BindingFlags.Static | BindingFlags.NonPublic);
            if (begin == null || first == null)
            {
                log("strategicoperations-coop|skipped|shape changed BeginRequestResources=" + (begin != null) + " FirstTimeInit=" + (first != null));
                return;
            }
            var harmony = new Harmony(OwnId);
            HarmonyMethod Mine(string name, int priority) => new HarmonyMethod(typeof(StrategicOperationsCoopDeterminism).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };
            harmony.Patch(begin, prefix: Mine(nameof(BeginPrefix), Priority.First));
            harmony.Patch(first, prefix: Mine(nameof(FirstTimeInitPrefix), Priority.First));
            var transpiler = Mine(nameof(Transpiler), Priority.Normal);
            int sites = 0, verified = 0;
            List<MethodBase> intended = MethodsMatching(mod, IsNewGuidCall, expectedCalls, skipped, "NewGuid");
            foreach (MethodBase m in intended)
            {
                try
                {
                    harmony.Patch(m, transpiler: transpiler);
                    sites++;
                    int before = expectedCalls.TryGetValue(m, out int e) ? e : -1, replaced = replacedCalls.TryGetValue(m, out int r) ? r : 0;
                    bool ok = before > 0 && replaced == before;
                    if (ok) verified++;
                    log("strategicoperations-coop|site|" + FullName(m) + "|NewGuid calls before=" + before + " replaced=" + replaced + (ok ? "|ok" : "|NOT FULLY REPLACED"));
                }
                catch (Exception ex) { log("strategicoperations-coop|site-failed|" + FullName(m) + "|" + ex.Message); }
            }
            log("strategicoperations-coop|patched|" + mod.GetName().Version + "|Guid.NewGuid sites: intended=" + (intended.Count + skipped.Count) + " patched=" + sites
                + " verified=" + verified + " skipped=" + skipped.Count + (skipped.Count > 0 ? " [" + string.Join("; ", skipped.ToArray()) + "]" : ""));
            MethodBase destroyed = combat.GetMethod("OnCombatGameDestroyed", all);
            if (destroyed != null) harmony.Patch(destroyed, postfix: Mine(nameof(DestroyedPostfix), Priority.Last));
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
            ApplyRandom(mod, harmony, Mine, destroyed != null);
        }

        // Mod.Random: every ldsfld in the mod's own methods goes through SeededRandom.
        static void ApplyRandom(Assembly mod, Harmony harmony, Func<string, int, HarmonyMethod> mine, bool summary)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            randomField = mod.GetType("StrategicOperations.Mod", false)?.GetField("Random", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            seededRandom = typeof(StrategicOperationsCoopDeterminism).GetMethod(nameof(SeededRandom), BindingFlags.Static | BindingFlags.NonPublic);
            if (randomField == null || randomField.FieldType != typeof(Random))
            {
                log("strategicoperations-coop|random|skipped|shape changed Mod.Random=" + (randomField != null) + " type=" + (randomField == null ? "-" : randomField.FieldType.Name));
                return;
            }
            var transpiler = mine(nameof(RandomTranspiler), Priority.Normal);
            int sites = 0, verified = 0, reads = 0;
            List<MethodBase> intended = MethodsMatching(mod, IsRandomRead, expectedReads, skippedReads, "Mod.Random reads");
            foreach (MethodBase m in intended)
            {
                try
                {
                    harmony.Patch(m, transpiler: transpiler);
                    sites++;
                    int before = expectedReads.TryGetValue(m, out int e) ? e : -1, replaced = replacedReads.TryGetValue(m, out int r) ? r : 0;
                    bool ok = before > 0 && replaced == before;
                    if (ok) verified++;
                    reads += replaced;
                    log("strategicoperations-coop|random-site|" + FullName(m) + "|Mod.Random reads before=" + before + " replaced=" + replaced + (ok ? "|ok" : "|NOT FULLY REPLACED"));
                }
                catch (Exception ex) { log("strategicoperations-coop|random-site-failed|" + FullName(m) + "|" + ex.Message); }
            }
            // UnityEngine.Random inside GenerateAIStrategicAbilities (BA spawn weights and the mounted unit are picked with GetRandomElement).
            Type unityRandom = ModAssemblyHook.GameType("UnityEngine.Random");
            initState = unityRandom?.GetMethod("InitState", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
            stateProp = unityRandom?.GetProperty("state", BindingFlags.Public | BindingFlags.Static);
            MethodInfo generate = mod.GetType("StrategicOperations.Framework.AI_Utils", false)?.GetMethod("GenerateAIStrategicAbilities", all);
            string unity;
            if (generate == null || initState == null || stateProp == null) unity = "NOT seeded: GenerateAIStrategicAbilities=" + (generate != null) + " Random.InitState=" + (initState != null) + " Random.state=" + (stateProp != null);
            else
            {
                try { harmony.Patch(generate, prefix: mine(nameof(UnityPrefix), Priority.First), finalizer: mine(nameof(UnityFinalizer), Priority.Last)); unity = "seeded in AI_Utils::GenerateAIStrategicAbilities"; }
                catch (Exception ex) { unity = "FAILED " + ex.Message; }
            }
            log("strategicoperations-coop|random|seed=on|Mod.Random reads: intended=" + (intended.Count + skippedReads.Count) + " methods, patched=" + sites + " verified=" + verified
                + " reads=" + reads + " skipped=" + skippedReads.Count + (skippedReads.Count > 0 ? " [" + string.Join("; ", skippedReads.ToArray()) + "]" : "")
                + "|UnityEngine.Random " + unity + "|summary at OnCombatGameDestroyed=" + summary);
        }

        static bool IsRandomRead(Mono.Cecil.Cil.Instruction ins)
        {
            var f = ins.Operand as Mono.Cecil.FieldReference;
            return ins.OpCode == Mono.Cecil.Cil.OpCodes.Ldsfld && f != null && f.Name == "Random" && f.DeclaringType.FullName == "StrategicOperations.Mod";
        }

        static string FullName(MethodBase m)
        {
            return m.DeclaringType?.FullName + "::" + m.Name + "(" + string.Join(",", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name)) + ")";
        }

        static void BeginPrefix(object __instance)
        {
            try
            {
                object net = Get(Get(__instance, "BattleTechGame"), "NetworkGameInstance");
                string guid = Get(__instance, "GUID") as string;
                if (net != null && Get(net, "IsCoopCombat") is bool coop && coop) Record(guid, "BeginRequestResources");
                else
                {
                    salt = null;
                    if (guid != lastSkipped) { lastSkipped = guid; log("strategicoperations-coop|skipped|not co-op combat|contract=" + (guid ?? "null")); }
                }
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
            if (salt != guid)
            {
                lock (counters) counters.Clear();
                noSalt = 0;
                ResetRandomCounts();
            }
            salt = guid;
            log("strategicoperations-coop|salt|" + source + "|contract=" + guid + "|random seed=on");
        }

        static void ResetRandomCounts()
        {
            lock (readCounts) { readCounts.Clear(); siteStats.Clear(); drawLines = 0; unityScopes = 0; }
        }

        // Per contract, one count line per Mod.Random site (scopes = reads seeded, draws = draws made on them), at combat end.
        static void DestroyedPostfix()
        {
            try
            {
                lock (readCounts)
                {
                    if (salt == null && siteStats.Count == 0) return;
                    int scopes = 0, draws = 0;
                    var keys = new List<string>(siteStats.Keys);
                    keys.Sort(StringComparer.Ordinal);   // the same order on both peers
                    foreach (string site in keys)
                    {
                        int[] s = siteStats[site];
                        scopes += s[0]; draws += s[1];
                        log("strategicoperations-coop|count|contract=" + (salt ?? "null") + "|" + site + "|scopes=" + s[0] + "|draws=" + s[1]);
                    }
                    log("strategicoperations-coop|count|contract=" + (salt ?? "null") + "|total|sites=" + siteStats.Count + "|scopes=" + scopes + "|draws=" + draws
                        + "|unity-scopes=" + unityScopes + "|draw-lines=" + Math.Min(drawLines, DrawLineCap) + (drawLines > DrawLineCap ? " (capped)" : ""));
                }
                ResetRandomCounts();
            }
            catch (Exception ex) { log("strategicoperations-coop|count-failed|" + ex.Message); }
        }

        // Replaces each `ldsfld StrategicOperations.Mod::Random` with SeededRandom("<type>::<method>#<read index>", <arg 0 or this>).
        static IEnumerable<CodeInstruction> RandomTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            string site = original.DeclaringType?.FullName + "::" + original.Name;
            ParameterInfo[] ps = original.GetParameters();
            bool arg0 = original.IsStatic
                ? ps.Length > 0 && !ps[0].ParameterType.IsByRef && !ps[0].ParameterType.IsValueType
                : original.DeclaringType != null && !original.DeclaringType.IsValueType;
            int k = 0;
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.opcode == OpCodes.Ldsfld && ins.operand as FieldInfo == randomField)
                {
                    replacedReads[original] = (replacedReads.TryGetValue(original, out int n) ? n : 0) + 1;
                    var push = new CodeInstruction(OpCodes.Ldstr, site + "#" + k++);   // takes over the read's branch labels and exception-block markers
                    push.labels.AddRange(ins.labels);
                    push.blocks.AddRange(ins.blocks);
                    yield return push;
                    yield return new CodeInstruction(arg0 ? OpCodes.Ldarg_0 : OpCodes.Ldnull);
                    yield return new CodeInstruction(OpCodes.Call, seededRandom);
                }
                else yield return ins;
            }
        }

        // Called in place of `ldsfld StrategicOperations.Mod::Random`. Outside a co-op contract: the mod's own instance (the same draws).
        static Random SeededRandom(string site, object keyed)
        {
            string s = salt;
            if (s == null) return (Random)randomField.GetValue(null);
            string key = KeyOf(keyed);
            int n;
            lock (readCounts)
            {
                readCounts.TryGetValue(site + "|" + key, out n);
                readCounts[site + "|" + key] = n + 1;
                Stat(site)[0]++;
            }
            return new ScopedRandom(SeedOf(s + "|so-random|" + site + "|" + key + "|" + n), site, key, n);
        }

        // FNV, then a SplitMix64 finalizer: keys that differ only in the read ordinal gave seeds a few hundred apart, and System.Random's
        // first draws from nearby seeds are correlated.
        static int SeedOf(string text)
        {
            ulong z = Fnv64(text, 14695981039346656037UL) + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (int)(uint)(z ^ (z >> 32));
        }

        static int[] Stat(string site)
        {
            if (!siteStats.TryGetValue(site, out int[] s)) siteStats[site] = s = new int[2];
            return s;
        }

        // The GUID both peers share for a read: the method's actor (AbstractActor, Mech, Turret, Vehicle), or for a sequence its owner
        // (ActorMovementSequence.owningActor, MechJumpSequence.OwningMech) or TB_StrafeSequence's Attacker, else its StrafingTeam.
        static string KeyOf(object o)
        {
            if (o == null) return "none";
            if (o.GetType().Name.EndsWith("Sequence", StringComparison.Ordinal))
            {
                foreach (string member in new[] { "owningActor", "OwningActor", "OwningMech", "Attacker", "StrafingTeam" })
                    if (Get(Get(o, member), "GUID") is string g && g.Length > 0) return g;
                return "sequence:" + o.GetType().Name;
            }
            return Get(o, "GUID") as string ?? ("type:" + o.GetType().Name);
        }

        // One read's instance: logs its first draw with the seed (one line per read, on both peers) and counts draws for the contract's count line.
        sealed class ScopedRandom : Random
        {
            readonly string site, key;
            readonly int seed, n;
            bool logged;
            public ScopedRandom(int seed, string site, string key, int n) : base(seed) { this.seed = seed; this.site = site; this.key = key; this.n = n; }
            void Hit(string call, object value)
            {
                int lines;
                lock (readCounts) { Stat(site)[1]++; if (logged) return; logged = true; lines = ++drawLines; }
                if (lines <= DrawLineCap) log("strategicoperations-coop|draw|" + site + "|key=" + key + "|n=" + n + "|seed=" + seed + "|" + call + "=" + value);
                else if (lines == DrawLineCap + 1) log("strategicoperations-coop|draw-lines-capped|" + DrawLineCap + " this contract; the count lines keep counting");
            }
            public override int Next() { int v = base.Next(); Hit("Next()", v); return v; }
            public override int Next(int max) { int v = base.Next(max); Hit("Next(" + max + ")", v); return v; }
            public override int Next(int min, int max) { int v = base.Next(min, max); Hit("Next(" + min + "," + max + ")", v); return v; }
            public override double NextDouble() { double v = base.NextDouble(); Hit("NextDouble()", v.ToString("R")); return v; }
            public override void NextBytes(byte[] buffer) { base.NextBytes(buffer); Hit("NextBytes(" + (buffer == null ? 0 : buffer.Length) + ")", "-"); }
        }

        // UnityEngine.Random inside GenerateAIStrategicAbilities(AbstractActor): seeded from contract | actor GUID | call ordinal for that actor;
        // the peer's own state is put back in the finalizer, which logs the seed and a digest of the state the scope left.
        sealed class UnityScope { public object outer; public int seed, n; public string key; }

        static void UnityPrefix(object[] __args, ref object __state)
        {
            __state = null;
            string s = salt;
            if (s == null) return;
            try
            {
                string key = KeyOf(__args != null && __args.Length > 0 ? __args[0] : null);
                int n;
                lock (readCounts)
                {
                    readCounts.TryGetValue("unity|" + key, out n);
                    readCounts["unity|" + key] = n + 1;
                    unityScopes++;
                }
                var u = new UnityScope { outer = stateProp.GetValue(null, null), seed = SeedOf(s + "|so-unity|AI_Utils::GenerateAIStrategicAbilities|" + key + "|" + n), n = n, key = key };
                initState.Invoke(null, new object[] { u.seed });
                __state = u;
            }
            catch (Exception ex) { log("strategicoperations-coop|unity-prefix-failed|" + ex.Message); }
        }

        static void UnityFinalizer(object __state)
        {
            var u = __state as UnityScope;
            if (u == null) return;
            try
            {
                string exit = StateDigest(stateProp.GetValue(null, null));
                stateProp.SetValue(null, u.outer, null);
                log("strategicoperations-coop|unity-scope|AI_Utils::GenerateAIStrategicAbilities|key=" + u.key + "|n=" + u.n + "|seed=" + u.seed + "|exit-state=" + exit);
            }
            catch (Exception ex) { log("strategicoperations-coop|unity-restore-failed|" + ex.Message); }
        }

        static string StateDigest(object state)
        {
            if (state == null) return "null";
            ulong h = 14695981039346656037UL;
            foreach (FieldInfo fi in state.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                string v = Convert.ToString(fi.GetValue(state), System.Globalization.CultureInfo.InvariantCulture);
                for (int i = 0; i < v.Length; i++) h = (h ^ v[i]) * 1099511628211UL;
                h = (h ^ '|') * 1099511628211UL;
            }
            return h.ToString("x16");
        }

        static bool IsNewGuidCall(Mono.Cecil.Cil.Instruction ins)
        {
            var target = ins.Operand as Mono.Cecil.MethodReference;
            return target != null && target.Name == "NewGuid" && target.DeclaringType.FullName == "System.Guid";
        }

        // StrategicOperations' own methods whose IL holds a matching instruction, read with Mono.Cecil from the mod's file.
        static Mono.Cecil.AssemblyDefinition cecil;
        static List<MethodBase> MethodsMatching(Assembly mod, Func<Mono.Cecil.Cil.Instruction, bool> match, Dictionary<MethodBase, int> expected, List<string> unresolved, string what)
        {
            var found = new List<MethodBase>();
            if (cecil == null)
            {
                string path = mod.Location;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                    path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "StrategicOperations", "StrategicOperations.dll");
                cecil = Mono.Cecil.AssemblyDefinition.ReadAssembly(path);
            }
            foreach (var type in cecil.MainModule.GetTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    int calls = 0;
                    foreach (var ins in method.Body.Instructions) if (match(ins)) calls++;
                    if (calls == 0) continue;
                    MethodBase resolved = Resolve(mod, type, method);   // by name and parameter types: ModTek loads a rewritten image with other tokens
                    if (resolved != null) { found.Add(resolved); expected[resolved] = calls; }
                    else unresolved.Add(type.FullName + "::" + method.Name + "(" + calls + " " + what + ", unresolved or ambiguous)");
                }
            return found;
        }

        static MethodBase Resolve(Assembly assembly, Mono.Cecil.TypeDefinition type, Mono.Cecil.MethodDefinition method)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type t = assembly.GetType(type.FullName.Replace('/', '+'), false);
            if (t == null) return null;
            var want = new List<string>();
            foreach (var p in method.Parameters) want.Add(p.ParameterType.Name);
            IEnumerable<MethodBase> candidates = method.IsConstructor ? (IEnumerable<MethodBase>)t.GetConstructors(all) : t.GetMethods(all);
            MethodBase match = null;
            foreach (MethodBase c in candidates)
            {
                if (c.Name != method.Name) continue;
                ParameterInfo[] ps = c.GetParameters();
                if (ps.Length != want.Count) continue;
                bool same = true;
                for (int i = 0; i < ps.Length && same; i++) same = ps[i].ParameterType.Name == want[i] || ps[i].ParameterType.IsGenericParameter;
                if (!same) continue;
                if (match != null) return null;   // ambiguous: leave it alone rather than patch the wrong one
                match = c;
            }
            return match;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            string site = original.DeclaringType?.FullName + "::" + original.Name;
            // CreateOrUpdateAISupportTeam(Team team): one support team per supported team, so the supported team is the stable key (its own GUID),
            // not a counter that depends on the order in which teams are reached on each peer.
            ParameterInfo[] ps = original.GetParameters();
            bool keyed = original.IsStatic && original.Name == "CreateOrUpdateAISupportTeam" && ps.Length >= 1 && ps[0].ParameterType.Name == "Team";
            foreach (CodeInstruction ins in instructions)
            {
                if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && ins.operand as MethodInfo == guidNewGuid)
                {
                    replacedCalls[original] = (replacedCalls.TryGetValue(original, out int n) ? n : 0) + 1;
                    var push = new CodeInstruction(OpCodes.Ldstr, site);   // takes over the replaced call's branch labels and exception-block markers
                    push.labels.AddRange(ins.labels);
                    push.blocks.AddRange(ins.blocks);
                    yield return push;
                    if (keyed)
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, newGuidForTeam);
                    }
                    else yield return new CodeInstruction(OpCodes.Call, newGuidFor);
                }
                else yield return ins;
            }
        }

        // Called in place of Guid.NewGuid() inside StrategicOperations' methods.
        static Guid NewGuidFor(string site)
        {
            string key = salt;
            if (key == null)
            {
                int missed = ++noSalt;
                if (missed <= 5) log("strategicoperations-coop|guid-no-contract|" + site + "|minted a random GUID|n=" + missed);
                return Guid.NewGuid();
            }
            int count;
            lock (counters) { counters.TryGetValue(site, out count); counters[site] = count + 1; }
            string text = key + "|so-guid|" + site + "|" + count;
            ulong a = Fnv64(text, 14695981039346656037UL), b = Fnv64(text, 1099511628211UL ^ 0x9E3779B97F4A7C15UL);
            var bytes = new byte[16];
            Array.Copy(BitConverter.GetBytes(a), 0, bytes, 0, 8);
            Array.Copy(BitConverter.GetBytes(b), 0, bytes, 8, 8);
            var guid = new Guid(bytes);
            log("strategicoperations-coop|guid|" + site + "|counter=" + count + "|" + guid);
            return guid;
        }

        const string SupportTeamSite = "StrategicOperations.Framework.Utils::CreateOrUpdateAISupportTeam";
        static Type teamType, aiTeamType;
        static MethodInfo setSupportTeam;

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
            if (salt == null || string.IsNullOrEmpty(teamGuid)) return NewGuidFor(site);
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
