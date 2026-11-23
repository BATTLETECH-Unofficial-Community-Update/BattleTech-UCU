// FieldRepairs adapter for co-op (gh issue 24). FieldRepairs rolls random pre-applied damage for every unit (armor and structure, ammo,
// component criticals, pilot skill damage) when the game applies its PoorlyMaintained effect to a unit. Its rolls come from one shared
// static System.Random, FieldRepairs.Mod.Random (public static readonly, created unseeded in Mod's static constructor), and its component
// and ammo-box picks from UnityEngine.Random (Utilities.GetRandomElement in the Mech/Turret/VehicleRepairState constructors), so each
// co-op peer rolled other damage (a Wheeled APC with 7 LRM rounds on the host and 120 on the guest; in h3 Valkyrie RightLeg 56 against 70).
// While a co-op combat contract runs (NetworkGameInstance.IsCoopCombat) and only when an assembly named FieldRepairs is loaded, each of
// PoorlyMaintainedEffect.ApplyEffectsToMech/Turret/Vehicle/Building and CombatGameState.FirstTimeInit (FieldRepairs' building theme) is a
// seeded scope: a System.Random seeded from the contract GUID, the patched method and the target unit's own GUID. Our prefix runs first and
// our finalizer last, so FieldRepairs' own prefixes and postfixes run inside it. The unit GUIDs agree on both peers, so a seed depends on no
// call order and no counter.
// Every `ldsfld FieldRepairs.Mod::Random` in FieldRepairs' own methods becomes a call that returns the current scope's instance (the
// peer's own instance outside a scope). The readonly field is never written, so a reference the JIT folded into compiled code cannot
// bypass it. UnityEngine.Random is seeded inside the three RepairState constructors when they run in a scope, and its state is saved and
// put back around each, so nothing outside the constructor sees a change.
// Each scope logs one `scope` line (seed, first draw, draws made on this scope's instance, draws that reached another scope's
// instance while this one was current, and what a reflection read of Mod.Random returned), and per contract one `count` line at
// CombatGameState.OnCombatGameDestroyed.
// Single-player, an unmodded co-op battle, and a co-op battle without FieldRepairs are untouched: outside a scope every redirected read returns
// the field's own value, and the field holds a pass-through proxy that only logs.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace BattleTechPort
{
    public static class FieldRepairsCoopDeterminism
    {
        const string OwnId = "battletech.port.fieldrepairs-coop";
        static Action<string> log;
        static string salt;                                   // contract GUID of the running co-op contract; null = not co-op
        static string lastSkipped;                            // contract GUID last logged as not co-op (one line per contract)
        static FieldInfo randomField;                         // FieldRepairs.Mod.Random
        const string mode = "transpile";
        static LoggingRandom current;                         // innermost seeded scope's instance
        static Random scoped;                                 // what the redirected reads return while a scope runs
        static readonly MethodInfo currentRandom = typeof(FieldRepairsCoopDeterminism).GetMethod(nameof(CurrentRandom), BindingFlags.Static | BindingFlags.NonPublic);
        static MethodInfo initState;                          // UnityEngine.Random.InitState(int)
        static PropertyInfo stateProp;                        // UnityEngine.Random.state
        static int scopes, draws, staleDraws, staleLogged, unseeded, unityScopes;   // per contract

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("FieldRepairs", Apply, logLine, "fieldrepairs-coop");
        }

        static void Apply(Assembly fieldRepairs)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type mod = fieldRepairs.GetType("FieldRepairs.Mod", false);
            randomField = mod?.GetField("Random", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assembly game = ModAssemblyHook.GameType("BattleTech.Contract")?.Assembly;
            Type contract = game?.GetType("BattleTech.Contract", false);
            Type effect = game?.GetType("BattleTech.PoorlyMaintainedEffect", false);
            Type combat = game?.GetType("BattleTech.CombatGameState", false);
            MethodBase begin = contract?.GetMethod("BeginRequestResources", all);
            MethodBase destroyed = combat?.GetMethod("OnCombatGameDestroyed", all);
            if (randomField == null || randomField.FieldType != typeof(Random) || begin == null || effect == null || combat == null)
            {
                log("fieldrepairs-coop|skipped|shape changed Mod.Random=" + (randomField != null) + " type=" + (randomField == null ? "-" : randomField.FieldType.Name)
                    + " BeginRequestResources=" + (begin != null) + " PoorlyMaintainedEffect=" + (effect != null) + " CombatGameState=" + (combat != null));
                return;
            }
            var harmony = new Harmony(OwnId);
            HarmonyMethod Mine(string name, int priority) => new HarmonyMethod(typeof(FieldRepairsCoopDeterminism).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };
            harmony.Patch(begin, prefix: Mine(nameof(BeginPrefix), Priority.First));
            if (destroyed != null) harmony.Patch(destroyed, postfix: Mine(nameof(DestroyedPostfix), Priority.Last));
            var sites = new List<string>();
            foreach (string name in new[] { "ApplyEffectsToMech", "ApplyEffectsToTurret", "ApplyEffectsToVehicle", "ApplyEffectsToBuilding" })
                Wrap(harmony, effect.GetMethod(name, all), name, sites, Mine);
            Wrap(harmony, combat.GetMethod("FirstTimeInit", all), "FirstTimeInit", sites, Mine);
            // The peer's own instance goes behind a proxy that reports every draw made outside a seeded scope during a co-op contract, so a
            // FieldRepairs hook that draws and is not wrapped shows up as "unseeded-draw|<caller chain>" instead of another blind run.
            try
            {
                object own = randomField.GetValue(null);
                randomField.SetValue(null, new ProxyRandom((Random)own));
                sites.Add("proxy(unseeded-draw watch)");
            }
            catch (Exception ex) { sites.Add("proxy=FAILED " + ex.Message); }
            string redirect = "";
            int methods = 0, reads = 0;
            var transpiler = Mine(nameof(Transpiler), Priority.Normal);
            foreach (Type t in Types(fieldRepairs))
                foreach (MethodBase m in Methods(t, all))
                {
                    int n = CountReads(m);
                    if (n == 0) continue;
                    try { harmony.Patch(m, transpiler: transpiler); methods++; reads += n; }
                    catch (Exception e) { log("fieldrepairs-coop|site-failed|" + t.FullName + "::" + m.Name + "|" + e.Message); }
                }
            redirect = "|Mod.Random reads redirected: " + reads + " in " + methods + " methods";
            Type unityRandom = ModAssemblyHook.GameType("UnityEngine.Random");
            initState = unityRandom?.GetMethod("InitState", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
            stateProp = unityRandom?.GetProperty("state", BindingFlags.Public | BindingFlags.Static);
            var ctors = new List<string>();
            foreach (string name in new[] { "FieldRepairs.MechRepairState", "FieldRepairs.TurretRepairState", "FieldRepairs.VehicleRepairState" })
            {
                ConstructorInfo[] found = fieldRepairs.GetType(name, false)?.GetConstructors(all) ?? new ConstructorInfo[0];
                if (initState == null || stateProp == null) { ctors.Add(name + "=NO UnityEngine.Random"); continue; }
                if (found.Length != 1) { ctors.Add(name + "=ctors:" + found.Length); continue; }
                try { harmony.Patch(found[0], prefix: Mine(nameof(UnityPrefix), Priority.First), finalizer: Mine(nameof(UnityFinalizer), Priority.Last)); ctors.Add(name.Substring(13)); }
                catch (Exception e) { ctors.Add(name + "=FAILED " + e.Message); }
            }
            redirect += "|UnityEngine.Random seeded in: " + string.Join(", ", ctors.ToArray());
            log("fieldrepairs-coop|patched|" + fieldRepairs.GetName().Version + "|mode=" + mode + "|Mod.Random static readonly=" + randomField.IsInitOnly
                + "|scopes: " + string.Join(", ", sites.ToArray()) + redirect + "|summary at OnCombatGameDestroyed=" + (destroyed != null));
        }

        static void Wrap(Harmony harmony, MethodBase method, string name, List<string> sites, Func<string, int, HarmonyMethod> mine)
        {
            if (method == null) { sites.Add(name + "=MISSING"); return; }
            try
            {
                harmony.Patch(method, prefix: mine(nameof(ScopePrefix), Priority.First), finalizer: mine(nameof(ScopeFinalizer), Priority.Last));
                sites.Add(name);
            }
            catch (Exception ex) { sites.Add(name + "=FAILED " + ex.Message); }
        }

        static IEnumerable<Type> Types(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, x => x != null); }
        }

        static IEnumerable<MethodBase> Methods(Type t, BindingFlags all)
        {
            if (t.ContainsGenericParameters) yield break;
            foreach (MethodBase m in t.GetMethods(all)) if (!m.IsAbstract && m.GetMethodBody() != null && !m.ContainsGenericParameters) yield return m;
            foreach (MethodBase m in t.GetConstructors(all)) if (m.GetMethodBody() != null) yield return m;
        }

        static int CountReads(MethodBase m)
        {
            try
            {
                int n = 0;
                foreach (CodeInstruction ins in PatchProcessor.GetOriginalInstructions(m)) if (ins.opcode == OpCodes.Ldsfld && ins.operand as FieldInfo == randomField) n++;
                return n;
            }
            catch { return 0; }
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.opcode == OpCodes.Ldsfld && ins.operand as FieldInfo == randomField)
                {
                    var call = new CodeInstruction(OpCodes.Call, currentRandom);   // takes over the read's branch labels and exception-block markers
                    call.labels.AddRange(ins.labels);
                    call.blocks.AddRange(ins.blocks);
                    yield return call;
                }
                else yield return ins;
            }
        }

        // Called in place of `ldsfld FieldRepairs.Mod::Random`.
        static Random CurrentRandom() { return scoped ?? (Random)randomField.GetValue(null); }

        static void BeginPrefix(object __instance)
        {
            try
            {
                salt = null;   // counts are not reset here: FirstTimeInit's scope can run before this (rp-modded8); DestroyedPostfix resets them
                object net = Get(Get(__instance, "BattleTechGame"), "NetworkGameInstance");
                string guid = Get(__instance, "GUID") as string;
                if (net != null && Get(net, "IsCoopCombat") is bool coop && coop) salt = guid;
                else if (guid != lastSkipped) { lastSkipped = guid; log("fieldrepairs-coop|skipped|not co-op combat|contract=" + (guid ?? "null")); }
            }
            catch (Exception ex) { log("fieldrepairs-coop|begin-prefix-failed|" + ex.Message); }
        }

        static void DestroyedPostfix()
        {
            try
            {
                if (salt != null || scopes > 0)
                    log("fieldrepairs-coop|count|contract=" + (salt ?? "null") + "|mode=" + mode + "|scopes=" + scopes + "|draws=" + draws + "|stale-draws=" + staleDraws
                        + "|unseeded-draws=" + unseeded + "|unity-scopes=" + unityScopes);
                ResetCounts();
            }
            catch (Exception ex) { log("fieldrepairs-coop|count-failed|" + ex.Message); }
        }

        static void ResetCounts() { scopes = draws = staleDraws = staleLogged = unseeded = unityScopes = 0; }

        sealed class Scope
        {
            public LoggingRandom seeded;
            public LoggingRandom prevCurrent;
            public Random prevScoped;
        }

        // Prefix on the wrapped methods: open a scope seeded from salt|method|target GUID.
        static void ScopePrefix(MethodBase __originalMethod, object[] __args, ref object __state)
        {
            __state = null;
            try
            {
                string site = __originalMethod.Name;
                string useSalt = salt, saltSource = "contract-begin";
                // CombatGameState.FirstTimeInit(Contract contract, GameInstance game, string localPlayerTeamGuid) runs before BeginRequestResources has
                // recorded the contract GUID (rp-modded8: scope-skipped-no-contract and one pre-contract-draw per peer). Its own arguments carry both the
                // contract (GUID shared by both peers) and the game's network instance (co-op flag), so seed from those and log which salt was used.
                if (useSalt == null && site == "FirstTimeInit" && __args != null && __args.Length >= 2)
                {
                    object net = Get(__args[1], "NetworkGameInstance");
                    bool coop = net != null && Get(net, "IsCoopCombat") is bool c && c;
                    string guid = Get(__args[0], "GUID") as string;
                    Note("firsttimeinit-salt-probe", "coop=" + coop + " contractGuid=" + (guid ?? "null") + " network=" + (net != null));
                    if (coop && !string.IsNullOrEmpty(guid)) { useSalt = guid; saltSource = "FirstTimeInit-contract-argument"; }
                    else if (!coop && guid != lastSkipped) { lastSkipped = guid; log("fieldrepairs-coop|skipped|not co-op combat|contract=" + (guid ?? "null")); }
                }
                if (useSalt == null) { Note("scope-skipped-no-contract", site); return; }
                // PoorlyMaintainedEffect calls all four ApplyEffectsTo* methods with the target cast to each type; only the matching one is non-null.
                object target = __args != null && __args.Length > 0 ? __args[0] : null;
                if (site != "FirstTimeInit" && target == null) { Note("scope-null-target", site); return; }
                string unit = site == "FirstTimeInit" ? "" : Get(target, "GUID") as string ?? "?";
                string key = useSalt + "|fieldrepairs|" + site + "|" + unit;
                ulong h = Fnv64(key);
                var seeded = new LoggingRandom((int)(uint)(h ^ (h >> 32)), site + "|" + unit, key);
                Note("scope-enter", site + "|" + unit + "|salt=" + saltSource + "|mode=" + mode, 400);
                var st = new Scope { seeded = seeded, prevCurrent = current, prevScoped = scoped };
                scoped = seeded;
                current = seeded;
                scopes++;
                __state = st;
            }
            catch (Exception ex) { log("fieldrepairs-coop|scope-failed|" + ex.GetType().Name + ": " + ex.Message); }
        }

        // Diagnostics: first 5 of each kind (scope entries, scopes skipped for want of a contract GUID), so a silent run shows which case it was.
        static readonly Dictionary<string, int> notes = new Dictionary<string, int>();
        static void Note(string kind, string text, int limit = 5)
        {
            int n;
            lock (notes) { notes.TryGetValue(kind, out n); notes[kind] = ++n; }
            if (n <= limit) log("fieldrepairs-coop|" + kind + "|" + text + "|n=" + n);
        }

        // Closes the scope. `reflect` is what a reflection read of Mod.Random returns at the end of the scope (the proxy);
        // `draws` counts the draws FieldRepairs' code made on this scope's instance; `stale-during` counts draws that reached
        // another scope's instance while this one was current (the JIT-folded-reference symptom). One line per scope, on both peers.
        static void ScopeFinalizer(object __state)
        {
            var st = __state as Scope;
            if (st == null) return;
            try
            {
                object f = randomField.GetValue(null);
                string reflect = ReferenceEquals(f, st.seeded) ? "seeded" : f is ProxyRandom ? "proxy" : f is LoggingRandom ? "other-scope" : "other";
                scoped = st.prevScoped;
                current = st.prevCurrent;
                LoggingRandom s = st.seeded;
                s.closed = true;
                log("fieldrepairs-coop|scope|mode=" + mode + "|" + s.site + "|seed=" + s.seed + "|first=" + (s.first ?? "none") + "|draws=" + s.own
                    + "|stale-during=" + s.staleDuring + "|reflect=" + reflect);
            }
            catch (Exception ex) { log("fieldrepairs-coop|restore-failed|" + ex.Message); }
        }

        // UnityEngine.Random inside a RepairState constructor that runs in a scope: seeded from the scope's key, the
        // constructor's type and its ordinal within the scope; the peer's own state is put back in the finalizer.
        sealed class UnityScope { public object outer; public int seed; public string name; public int n; public LoggingRandom owner; }

        static void UnityPrefix(MethodBase __originalMethod, ref object __state)
        {
            __state = null;
            LoggingRandom owner = current;
            if (owner == null || owner.closed) return;
            try
            {
                int n = owner.unityScopes++;
                string name = __originalMethod.DeclaringType?.Name ?? "?";
                // SplitMix64 finalizer over FNV: keys that differ only in the ordinal would otherwise give nearby seeds.
                ulong z = Fnv64(owner.key + "|unity|" + name + "|" + n) + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                var u = new UnityScope { outer = stateProp.GetValue(null, null), seed = (int)(uint)(z ^ (z >> 32)), name = name, n = n, owner = owner };
                initState.Invoke(null, new object[] { u.seed });
                unityScopes++;
                __state = u;
            }
            catch (Exception ex) { log("fieldrepairs-coop|unity-prefix-failed|" + ex.Message); }
        }

        static void UnityFinalizer(object __state)
        {
            var u = __state as UnityScope;
            if (u == null) return;
            try
            {
                string exit = StateDigest(stateProp.GetValue(null, null));
                stateProp.SetValue(null, u.outer, null);
                log("fieldrepairs-coop|unity-scope|" + u.name + "|" + u.owner.site + "|n=" + u.n + "|seed=" + u.seed + "|exit-state=" + exit);
            }
            catch (Exception ex) { log("fieldrepairs-coop|unity-restore-failed|" + ex.Message); }
        }

        // FNV over UnityEngine.Random.State's fields: equal seeds and equal draw counts give equal digests on both peers.
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

        // A seeded System.Random for one scope. It logs its first draw (seed included) and counts its draws; a draw that reaches it while it
        // is not the current scope's instance is counted as stale against the current scope.
        sealed class LoggingRandom : Random
        {
            public readonly string site, key;
            public readonly int seed;
            public string first;
            public int own, staleDuring, unityScopes;
            public bool closed;
            public LoggingRandom(int seed, string site, string key) : base(seed) { this.seed = seed; this.site = site; this.key = key; }
            void Hit(string call, object value)
            {
                if (!ReferenceEquals(this, current))
                {
                    staleDraws++;
                    LoggingRandom cur = current;
                    if (cur != null) cur.staleDuring++;
                    if (++staleLogged <= 20)
                        log("fieldrepairs-coop|stale-draw|instance=" + site + (closed ? " (closed)" : "") + "|current=" + (cur == null ? "none" : cur.site) + "|" + call + "=" + value);
                    return;
                }
                own++;
                draws++;
                if (first != null) return;
                first = call + "=" + value;
                log("fieldrepairs-coop|first-draw|" + site + "|seed=" + seed + "|" + first);
            }
            public override int Next() { int v = base.Next(); Hit("Next()", v); return v; }
            public override int Next(int max) { int v = base.Next(max); Hit("Next(" + max + ")", v); return v; }
            public override int Next(int min, int max) { int v = base.Next(min, max); Hit("Next(" + min + "," + max + ")", v); return v; }
            public override double NextDouble() { double v = base.NextDouble(); Hit("NextDouble()", v.ToString("R")); return v; }
            public override void NextBytes(byte[] buffer) { base.NextBytes(buffer); Hit("NextBytes(" + (buffer == null ? 0 : buffer.Length) + ")", "-"); }
        }

        // Wraps the peer's own FieldRepairs instance. A draw that reaches it during a co-op contract (salt set) was made outside every scope:
        // logged with its caller chain, the first 5 times per chain and then every 50th.
        sealed class ProxyRandom : Random
        {
            readonly Random inner;
            readonly Dictionary<string, int> seen = new Dictionary<string, int>();
            public ProxyRandom(Random inner) { this.inner = inner; }
            void Watch()
            {
                if (salt == null) { Note("pre-contract-draw", Caller()); return; }
                unseeded++;
                try
                {
                    var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
                    var chain = new List<string>();
                    for (int i = 0; frames != null && i < frames.Length && chain.Count < 4; i++)
                    {
                        MethodBase m = frames[i].GetMethod();
                        if (m == null || m.DeclaringType == null || m.DeclaringType == typeof(ProxyRandom)) continue;
                        chain.Add(m.DeclaringType.Name + "::" + m.Name);
                    }
                    string caller = string.Join(" <- ", chain.ToArray());
                    int n;
                    lock (seen) { seen.TryGetValue(caller, out n); seen[caller] = ++n; }
                    if (n <= 5 || n % 50 == 0) log("fieldrepairs-coop|unseeded-draw|" + caller + "|n=" + n);
                }
                catch (Exception ex) { log("fieldrepairs-coop|unseeded-draw|watch-failed " + ex.Message); }
            }
            static string Caller()
            {
                try
                {
                    var frames = new System.Diagnostics.StackTrace(3, false).GetFrames();
                    for (int i = 0; frames != null && i < frames.Length; i++)
                    {
                        MethodBase m = frames[i].GetMethod();
                        if (m != null && m.DeclaringType != null && m.DeclaringType != typeof(ProxyRandom)) return m.DeclaringType.Name + "::" + m.Name;
                    }
                }
                catch { }
                return "?";
            }
            public override int Next() { Watch(); return inner.Next(); }
            public override int Next(int max) { Watch(); return inner.Next(max); }
            public override int Next(int min, int max) { Watch(); return inner.Next(min, max); }
            public override double NextDouble() { Watch(); return inner.NextDouble(); }
            public override void NextBytes(byte[] buffer) { Watch(); inner.NextBytes(buffer); }
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
