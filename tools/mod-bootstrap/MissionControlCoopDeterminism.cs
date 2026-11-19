// MissionControl adapter for co-op (gh issue 24). MissionControl rolls its contract setup per peer: the encounter rule and the
// additional lances (global UnityEngine.Random, drawn in its prefix on Contract.BeginRequestResources before UCU seeds anything),
// and the spawn points and encounter objects it adds at EncounterLayerParent.InitializeContract (System.Guid.NewGuid, random
// placement). In a co-op battle each peer then ran a different encounter: other target faction, other enemy chassis, other unit
// GUIDs, so the MECHS compare failed in round 1.
// While a co-op combat contract starts (Contract.IsCoopCombat), and only when an assembly named MissionControl is loaded:
//  - UnityEngine.Random is seeded from the contract GUID around Contract.BeginRequestResources and around
//    EncounterLayerParent.InitializeContract (our prefix runs before MissionControl's, our finalizer after), and the peer's own
//    state is put back after each;
//  - every method of MissionControl that calls Guid.NewGuid gets a deterministic GUID instead: FNV-1a over contract GUID, the
//    method's name and a per-method counter (the same hash family as Contract.CoopSeededShuffle).
// Single-player, and a co-op battle without MissionControl, are untouched; nothing outside MissionControl's own methods and the
// two game entry points is patched. System.Guid.NewGuid itself is never patched.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace BattleTechPort
{
    public static class MissionControlCoopDeterminism
    {
        static Action<string> log;
        static string salt;                       // contract GUID of the co-op contract being run; null = not co-op, mint random GUIDs
        static readonly Dictionary<string, int> counters = new Dictionary<string, int>();
        static Type randomType, randomStateType;
        static MethodInfo initState;
        static PropertyInfo stateProp;
        static MethodInfo newGuidFor, guidNewGuid;
        static readonly Dictionary<MethodBase, int> expectedCalls = new Dictionary<MethodBase, int>();   // NewGuid calls in the file's IL, by method
        static readonly Dictionary<MethodBase, int> replacedCalls = new Dictionary<MethodBase, int>();   // calls the transpiler replaced
        static readonly List<string> skipped = new List<string>();                                       // MissionControl sites not resolved to one method
        static Assembly mcAssembly;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("MissionControl", Apply, logLine, "missioncontrol-coop");
        }

        static void Apply(Assembly missionControl)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            mcAssembly = missionControl;
            Type contract = ModAssemblyHook.GameType("BattleTech.Contract");
            Type layerParent = ModAssemblyHook.GameType("BattleTech.EncounterLayerParent");
            randomType = ModAssemblyHook.GameType("UnityEngine.Random");
            randomStateType = randomType?.GetNestedType("State", BindingFlags.Public | BindingFlags.NonPublic);
            initState = randomType?.GetMethod("InitState", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
            stateProp = randomType?.GetProperty("state", BindingFlags.Public | BindingFlags.Static);
            MethodInfo begin = contract?.GetMethod("BeginRequestResources", all);
            MethodInfo init = layerParent?.GetMethod("InitializeContract", all);
            guidNewGuid = typeof(Guid).GetMethod("NewGuid", Type.EmptyTypes);
            newGuidFor = typeof(MissionControlCoopDeterminism).GetMethod(nameof(NewGuidFor), BindingFlags.Static | BindingFlags.NonPublic);
            if (begin == null || init == null || initState == null || stateProp == null || randomStateType == null)
            {
                log("missioncontrol-coop|skipped|shape changed BeginRequestResources=" + (begin != null) + " InitializeContract=" + (init != null)
                    + " Random.InitState=" + (initState != null) + " Random.state=" + (stateProp != null));
                return;
            }
            var harmony = new Harmony("battletech.port.missioncontrol-coop");
            HarmonyMethod Mine(string name, int priority) => new HarmonyMethod(typeof(MissionControlCoopDeterminism).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };
            harmony.Patch(begin, prefix: Mine(nameof(BeginPrefix), Priority.First), finalizer: Mine(nameof(Finalizer), Priority.Last));
            harmony.Patch(init, prefix: Mine(nameof(InitPrefix), Priority.First), finalizer: Mine(nameof(Finalizer), Priority.Last));
            int sites = 0, verified = 0;
            var transpiler = Mine(nameof(Transpiler), Priority.Normal);
            List<MethodBase> intended = MethodsCallingNewGuid(missionControl);
            foreach (MethodBase m in intended)
            {
                try
                {
                    harmony.Patch(m, transpiler: transpiler);
                    sites++;
                    int before = expectedCalls.TryGetValue(m, out int e) ? e : -1, replaced = replacedCalls.TryGetValue(m, out int r) ? r : 0;
                    bool ok = before > 0 && replaced == before;
                    if (ok) verified++;
                    log("missioncontrol-coop|site|" + FullName(m) + "|NewGuid calls before=" + before + " replaced=" + replaced + " after=" + (before - replaced) + (ok ? "|ok" : "|NOT FULLY REPLACED"));
                }
                catch (Exception ex) { log("missioncontrol-coop|site-failed|" + FullName(m) + "|" + ex.Message); }
            }
            // Unit placement at the drop: MissionControl's encounter-result nodes pick edges, offsets and positions with UnityEngine.Random in
            // Run, after Contract.Begin and outside UnitSpawnPointGameLogic.Spawn (in one test run the player lance spawned at z -748 on the
            // host and +727 on the guest). Each Run is seeded from the contract GUID, the node type and the node's own key (lanceKey, objectKey
            // or lanceGuid), plus the number of times that same node has run, so one peer running an extra node cannot shift another node's seed.
            int nodes = 0;
            var nodeNames = new List<string>();
            foreach (string typeName in SeededNodeTypes)
            {
                Type nodeType = missionControl.GetType("MissionControl.Logic." + typeName, false);
                MethodInfo run = nodeType?.GetMethod("Run", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (run == null) { nodeNames.Add(typeName + "=MISSING"); continue; }
                try
                {
                    harmony.Patch(run, prefix: Mine(nameof(NodePrefix), Priority.First), finalizer: Mine(nameof(NodeFinalizer), Priority.Last));
                    nodes++;
                    nodeNames.Add(typeName);
                }
                catch (Exception ex) { nodeNames.Add(typeName + "=FAILED " + ex.Message); }
            }
            log("missioncontrol-coop|nodes|seeded Run of " + nodes + "/" + SeededNodeTypes.Length + ": " + string.Join(", ", nodeNames.ToArray()));
            log("missioncontrol-coop|patched|" + missionControl.GetName().Version + "|seed around BeginRequestResources and InitializeContract; Guid.NewGuid sites: intended=" + (intended.Count + skipped.Count)
                + " patched=" + sites + " verified=" + verified + " skipped=" + skipped.Count + (skipped.Count > 0 ? " [" + string.Join("; ", skipped.ToArray()) + "]" : ""));
        }

        // MissionControl's own methods whose IL calls System.Guid.NewGuid, read with Mono.Cecil from the loaded assembly's file
        // (the runtime metadata token of a method is the same token Cecil reads).
        static string FullName(MethodBase m)
        {
            return m.DeclaringType?.FullName + "::" + m.Name + "(" + string.Join(",", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name)) + ")";
        }

        static List<MethodBase> MethodsCallingNewGuid(Assembly missionControl)
        {
            var found = new List<MethodBase>();
            string path = missionControl.Location;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))   // loaded from bytes: the mod folder holds the file
                path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "MissionControl", "MissionControl.dll");
            var def = Mono.Cecil.AssemblyDefinition.ReadAssembly(path);
            foreach (var type in def.MainModule.GetTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    int calls = 0;
                    foreach (var ins in method.Body.Instructions)
                    {
                        var target = ins.Operand as Mono.Cecil.MethodReference;
                        if (target != null && target.Name == "NewGuid" && target.DeclaringType.FullName == "System.Guid") calls++;
                    }
                    if (calls == 0) continue;
                    // by name and parameter types: ModTek loads a rewritten image whose metadata tokens differ from the file's
                    MethodBase resolved = Resolve(missionControl, type, method);
                    if (resolved != null) { found.Add(resolved); expectedCalls[resolved] = calls; }
                    else skipped.Add(type.FullName + "::" + method.Name + "(" + calls + " NewGuid, unresolved or ambiguous)");
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
            IEnumerable<MethodBase> candidates = method.IsConstructor
                ? (IEnumerable<MethodBase>)t.GetConstructors(all)
                : t.GetMethods(all);
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
            foreach (CodeInstruction ins in instructions)
            {
                if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && ins.operand as MethodInfo == guidNewGuid)
                {
                    replacedCalls[original] = (replacedCalls.TryGetValue(original, out int n) ? n : 0) + 1;
                    // the pushed string takes over the replaced call's branch labels and exception-block markers
                    var push = new CodeInstruction(OpCodes.Ldstr, site);
                    push.labels.AddRange(ins.labels);
                    push.blocks.AddRange(ins.blocks);
                    yield return push;
                    yield return new CodeInstruction(OpCodes.Call, newGuidFor);
                }
                else yield return ins;
            }
        }

        // Called in place of Guid.NewGuid() inside MissionControl's methods.
        static Guid NewGuidFor(string site)
        {
            string key = salt;
            if (key == null) return Guid.NewGuid();
            int n;
            lock (counters) { counters.TryGetValue(site, out n); counters[site] = n + 1; }
            string text = key + "|guid|" + site + "|" + n;
            ulong a = Fnv64(text, 14695981039346656037UL), b = Fnv64(text, 1099511628211UL ^ 0x9E3779B97F4A7C15UL);
            var bytes = new byte[16];
            Array.Copy(BitConverter.GetBytes(a), 0, bytes, 0, 8);
            Array.Copy(BitConverter.GetBytes(b), 0, bytes, 8, 8);
            var guid = new Guid(bytes);
            log("missioncontrol-coop|guid|" + site + "|" + n + "|" + guid);
            return guid;
        }

        static ulong Fnv64(string text, ulong basis)
        {
            ulong h = basis;
            for (int i = 0; i < text.Length; i++) h = (h ^ text[i]) * 1099511628211UL;
            return h;
        }

        static void BeginPrefix(object __instance, ref object __state)
        {
            __state = null;
            try
            {
                salt = null;
                if (!IsCoopCombat(__instance)) return;
                salt = Get(__instance, "GUID") as string;
                lock (counters) counters.Clear();
                lock (nodeRuns) nodeRuns.Clear();
                if (salt == null) return;
                __state = Seed("begin");
                log("missioncontrol-coop|seed|BeginRequestResources|contract=" + salt);
                try { SyncDropData(__instance); }
                catch (Exception ex)
                {
                    Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    string trace = inner.StackTrace ?? "";
                    int eol = trace.IndexOf((char)10);
                    string top = (eol < 0 ? trace : trace.Substring(0, eol)).Trim();
                    log("missioncontrol-coop|drop-failed|" + inner.GetType().Name + ": " + inner.Message + " @ " + top);
                }
            }
            catch (Exception ex) { log("missioncontrol-coop|begin-prefix-failed|" + ex.Message); }
        }

        // MissionControl learns the player's drop (difficulty, skull rating, tonnage) only from the lance screen's UI, in a postfix on
        // LanceConfiguratorPanel.ContinueConfirmClicked, which only the host clicks: a guest's values stay 0 and its additional-lance
        // logic runs on other data.  Both peers compute them here from the contract's own lance with the game's function behind that
        // widget (SimGameBattleSimulator.GetLanceTonnageRating) and MissionControl's own tonnage sum (chassis tonnage).
        // Host: MissionControl's UI values are logged beside the computed ones and left alone; they must be equal.
        // Guest: set when the lance is filled, else logged and left alone.
        static void SyncDropData(object contract)
        {
            object net = Get(Get(contract, "BattleTechGame"), "NetworkGameInstance");
            bool host = Get(net, "IsCampaignHost") is bool h && h;
            Type mcType = mcAssembly.GetType("MissionControl.MissionControl", false);
            object mc = mcType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null, null);
            if (mc == null) { log("missioncontrol-coop|drop|role=" + (host ? "host" : "guest") + "|no MissionControl.Instance"); return; }
            object lances = Get(contract, "Lances");
            var units = lances?.GetType().GetMethod("GetLanceUnits", new[] { typeof(string) })?.Invoke(lances, new object[] { "" }) as Array;
            int total = units?.Length ?? 0, withMech = 0, hydrated = 0, unresolved = 0, fromCompany = 0, fromDataManager = 0;
            var diag = new System.Text.StringBuilder();
            object sim = Get(Get(contract, "BattleTechGame"), "Simulation");
            Type mechDefType = ModAssemblyHook.GameType("BattleTech.MechDef");
            object mechs = Activator.CreateInstance(typeof(List<>).MakeGenericType(mechDefType));
            double chassisTonnage = 0;
            if (units != null)
                foreach (object u in units)
                {
                    // A guest's lance arrives as JSON and the contract's multiplayer path hydrates each unit just after this prefix. Read-only
                    // here: hydrate a throwaway copy (SpawnableUnit's copy constructor, then FromJSON on the copy, which builds new
                    // MechDef/PilotDef objects from the JSON) and leave the contract's own units untouched for the game to hydrate.
                    object src = u;
                    if (Get(u, "IsUnitializedJson") is bool raw && raw)
                    {
                        src = u.GetType().GetConstructor(new[] { u.GetType() })?.Invoke(new[] { u });
                        src?.GetType().GetMethod("FromJSON", Type.EmptyTypes)?.Invoke(src, null);
                        hydrated++;
                    }
                    object mech = src == null ? null : Get(src, "Unit");
                    if (mech == null) continue;
                    if (!ReferenceEquals(src, u) || Get(mech, "Chassis") == null)   // a copy, or a unit the game already deserialized from JSON without resolving its chassis
                    {
                        // The JSON copy's MechDef holds ids, not the resolved chassis and components the lance screen's computation reads
                        // (GetLanceTonnageRating threw a NullReferenceException on it). The guest's company is the host's snapshot, so the same
                        // mech sits in its ActiveMechs under the same GUID: use that resolved object, read-only.
                        string guid = Get(mech, "GUID") as string, defId = Get(u, "UnitId") as string;
                        object resolved = FindActiveMech(sim, guid);
                        string how = "company";
                        if (resolved == null || Get(resolved, "Chassis") == null)
                        {
                            // fallback: the data manager's own MechDef for the unit id (stock definition, chassis resolved when loaded)
                            resolved = FindDataManagerMech(contract, defId);
                            how = "dataManager";
                        }
                        bool usable = resolved != null && Get(resolved, "Chassis") != null;
                        diag.Append(" [guid=" + guid + " id=" + defId + " via=" + (usable ? how : "NONE") + "]");
                        if (!usable) { unresolved++; continue; }
                        mech = resolved;
                        if (how == "company") fromCompany++; else fromDataManager++;
                    }
                    withMech++;
                    mechs.GetType().GetMethod("Add").Invoke(mechs, new[] { mech });
                    chassisTonnage += Convert.ToDouble(Get(Get(mech, "Chassis"), "Tonnage"));
                }
            MethodInfo rating = ModAssemblyHook.GameType("BattleTech.SimGameBattleSimulator")?.GetMethod("GetLanceTonnageRating", BindingFlags.Static | BindingFlags.Public);
            int difficulty = -1;
            if (rating != null && sim != null && withMech > 0)
            {
                var args = new object[] { sim, mechs, 0f };
                difficulty = (int)rating.Invoke(null, args);
            }
            double skull = difficulty * 0.5;
            string uiNow = "uiDifficulty=" + Get(mc, "PlayerLanceDropDifficultyValue") + " uiSkull=" + Get(mc, "PlayerLanceDropSkullRating") + " uiTonnage=" + Get(mc, "PlayerLanceDropTonnage");
            string computed = "computedDifficulty=" + difficulty + " computedSkull=" + skull + " computedTonnage=" + chassisTonnage;
            string filled = "lanceUnits=" + total + " withMechDef=" + withMech + " hydratedHere=" + hydrated + " resolvedFromCompany=" + fromCompany + "/" + total + " resolvedFromDataManager=" + fromDataManager + " unresolved=" + unresolved + diag;
            string role = host ? "host" : "guest";
            if (host)
            {
                bool same = withMech > 0 && Convert.ToDouble(Get(mc, "PlayerLanceDropDifficultyValue")) == difficulty
                    && Math.Abs(Convert.ToDouble(Get(mc, "PlayerLanceDropTonnage")) - chassisTonnage) < 0.01;
                log("missioncontrol-coop|drop|role=host|" + filled + "|" + uiNow + "|" + computed + "|" + (same ? "EQUAL" : "DIFFERENT: the computation is not MissionControl's, not trusted"));
                return;
            }
            // All or nothing: a total over part of the lance would put a wrong difficulty on the guest.  The difficulty also reads what a company mech can change
            // (CalculateTonnage: assigned armor and the inventory on top of the chassis tonnage), so only mechs taken from the company are exact; the
            // DataManager route gives the stock definition and is logged as approximate, never written.
            if (withMech == 0 || difficulty < 0 || unresolved > 0 || withMech != total || fromDataManager > 0)
            {
                log("missioncontrol-coop|drop|role=guest|FAILED SYNC, nothing written|" + filled + "|" + uiNow + "|" + computed + (fromDataManager > 0 ? " (approximate: stock MechDef used for " + fromDataManager + " unit(s))" : ""));
                return;
            }
            SetProp(mc, "PlayerLanceDropDifficultyValue", difficulty);
            SetProp(mc, "PlayerLanceDropSkullRating", skull);
            SetProp(mc, "PlayerLanceDropTonnage", chassisTonnage);
            log("missioncontrol-coop|drop|role=guest|" + filled + "|" + uiNow + "|" + computed + "|set");
        }

        static object FindDataManagerMech(object contract, string defId)
        {
            if (string.IsNullOrEmpty(defId)) return null;
            object dm = Get(contract, "dataManager") ?? Get(Get(contract, "BattleTechGame"), "DataManager");
            object store = Get(dm, "MechDefs");
            MethodInfo get = store?.GetType().GetMethod("Get", new[] { typeof(string) });
            return get?.Invoke(store, new object[] { defId });
        }

        static object FindActiveMech(object sim, string guid)
        {
            if (sim == null || string.IsNullOrEmpty(guid)) return null;
            var active = Get(sim, "ActiveMechs") as System.Collections.IDictionary;
            if (active == null) return null;
            foreach (object m in active.Values)
                if (string.Equals(Get(m, "GUID") as string, guid, StringComparison.Ordinal)) return m;
            return null;
        }

        static void SetProp(object o, string name, double value)
        {
            PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            p.SetValue(o, Convert.ChangeType(value, p.PropertyType), null);
        }

        static void InitPrefix(ref object __state)
        {
            __state = null;
            try
            {
                if (salt == null) return;
                __state = Seed("init");
                log("missioncontrol-coop|seed|InitializeContract|contract=" + salt);
            }
            catch (Exception ex) { log("missioncontrol-coop|init-prefix-failed|" + ex.Message); }
        }

        static void Finalizer(object __state)
        {
            if (__state == null) return;
            try { stateProp.SetValue(null, __state, null); }
            catch (Exception ex) { log("missioncontrol-coop|restore-failed|" + ex.Message); }
        }

        // MissionControl result nodes that place units with UnityEngine.Random; the key field each one names itself by.
        static readonly string[] SeededNodeTypes =
        {
            "SpawnLanceAtEdgeOfBoundary", "SpawnLanceAnywhere", "SpawnLanceMembersAroundTarget", "SpawnObjectAnywhere",
            "AddLanceToAllyTeam", "AddLanceToTargetTeam", "AddLanceToPlayer2Team"
        };
        static readonly string[] NodeKeyFields = { "lanceKey", "objectKey", "lanceGuid" };
        static readonly Dictionary<string, int> nodeRuns = new Dictionary<string, int>();

        // Prefix on a node's Run: seed from salt|node|type|key|run number of this node; the state is put back by NodeFinalizer.
        static void NodePrefix(object __instance, ref object __state)
        {
            __state = null;
            try
            {
                if (salt == null) return;
                string type = __instance.GetType().Name, key = "";
                foreach (string field in NodeKeyFields)
                    if (Get(__instance, field) is string s && s.Length > 0) { key = s; break; }
                string id = type + "|" + key;
                int run;
                lock (nodeRuns) { nodeRuns.TryGetValue(id, out run); nodeRuns[id] = run + 1; }
                object outer = stateProp.GetValue(null, null);
                ulong h = Fnv64(salt + "|node|" + id + "|" + run, 14695981039346656037UL);
                initState.Invoke(null, new object[] { (int)(uint)(h ^ (h >> 32)) });
                __state = outer;
                log("missioncontrol-coop|node-seed|" + id + "|run=" + run);
            }
            catch (Exception ex) { log("missioncontrol-coop|node-prefix-failed|" + ex.Message); }
        }

        // After the node's Run: restore the peer's own stream and log where the node left its lance (the node's `lance` object), when it has one.
        static void NodeFinalizer(object __instance, object __state)
        {
            if (__state == null) return;
            try
            {
                stateProp.SetValue(null, __state, null);
                object lance = Get(__instance, "lance");
                object transform = Get(lance, "transform");
                object pos = transform?.GetType().GetProperty("position")?.GetValue(transform, null);
                if (pos != null)
                    log("missioncontrol-coop|node-done|" + __instance.GetType().Name + "|lance at " + pos.GetType().GetMethod("ToString", new[] { typeof(string) }).Invoke(pos, new object[] { "F2" }));
            }
            catch (Exception ex) { log("missioncontrol-coop|node-finalizer-failed|" + ex.Message); }
        }

        // Seeds UnityEngine.Random from the contract GUID and a phase key; returns the previous state (boxed Random.State).
        static object Seed(string phase)
        {
            object outer = stateProp.GetValue(null, null);
            ulong h = Fnv64(salt + "|" + phase, 14695981039346656037UL);
            initState.Invoke(null, new object[] { (int)(uint)(h ^ (h >> 32)) });
            return outer;
        }

        static bool IsCoopCombat(object contract)
        {
            object net = Get(Get(contract, "BattleTechGame"), "NetworkGameInstance");
            return net != null && Get(net, "IsCoopCombat") is bool b && b;
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
