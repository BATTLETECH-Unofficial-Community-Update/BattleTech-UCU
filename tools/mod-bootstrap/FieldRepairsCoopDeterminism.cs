// FieldRepairs adapter for co-op (gh issue 24). FieldRepairs rolls random pre-applied damage for every unit (armor and structure, ammo,
// component criticals, pilot skill damage) when the game applies its PoorlyMaintained effect to a unit. All of its draws come from one
// shared static System.Random, FieldRepairs.Mod.Random (static readonly, created unseeded in Mod's static constructor), so each co-op peer
// rolled other damage (in one test run, a Wheeled APC with 7 LRM rounds on the host and 120 on the guest, a Javelin with a centre torso at
// 50 against 22). UnityEngine.Random seeding never reached it.
// While a co-op combat contract runs (Contract.IsCoopCombat) and only when an assembly named FieldRepairs is loaded, the shared
// instance is swapped for a System.Random seeded from the contract GUID, the patched method and the target unit's own GUID around each
// of the methods that call into FieldRepairs' prefixes: PoorlyMaintainedEffect.ApplyEffectsToMech/Turret/Vehicle/Building and
// CombatGameState.FirstTimeInit (FieldRepairs' building states). Our prefix runs first and our finalizer last, so FieldRepairs' own
// prefixes and postfixes draw from the seeded instance, and the peer's instance is put back afterwards. The unit GUIDs agree on both
// peers, so the seed depends on no call order and no counter. The first draw from each seeded instance is logged (site, unit, call and
// value); equal lines on both peers for the same unit show that the swap took effect.
// Single-player, an unmodded co-op battle, and a co-op battle without FieldRepairs are untouched.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class FieldRepairsCoopDeterminism
    {
        const string OwnId = "battletech.port.fieldrepairs-coop";
        static Action<string> log;
        static string salt;                                   // contract GUID of the running co-op contract; null = not co-op
        static FieldInfo randomField;                         // FieldRepairs.Mod.Random

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
            if (randomField == null || randomField.FieldType != typeof(Random) || begin == null || effect == null || combat == null)
            {
                log("fieldrepairs-coop|skipped|shape changed Mod.Random=" + (randomField != null) + " type=" + (randomField == null ? "-" : randomField.FieldType.Name)
                    + " BeginRequestResources=" + (begin != null) + " PoorlyMaintainedEffect=" + (effect != null) + " CombatGameState=" + (combat != null));
                return;
            }
            var harmony = new Harmony(OwnId);
            HarmonyMethod Mine(string name, int priority) => new HarmonyMethod(typeof(FieldRepairsCoopDeterminism).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };
            harmony.Patch(begin, prefix: Mine(nameof(BeginPrefix), Priority.First));
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
            log("fieldrepairs-coop|patched|" + fieldRepairs.GetName().Version + "|Mod.Random static readonly=" + randomField.IsInitOnly + "|swap around: " + string.Join(", ", sites.ToArray()));
        }

        static void Wrap(Harmony harmony, MethodBase method, string name, List<string> sites, Func<string, int, HarmonyMethod> mine)
        {
            if (method == null) { sites.Add(name + "=MISSING"); return; }
            try
            {
                harmony.Patch(method, prefix: mine(nameof(SwapPrefix), Priority.First), finalizer: mine(nameof(SwapFinalizer), Priority.Last));
                sites.Add(name);
            }
            catch (Exception ex) { sites.Add(name + "=FAILED " + ex.Message); }
        }

        static void BeginPrefix(object __instance)
        {
            try
            {
                salt = null;
                object net = Get(Get(__instance, "BattleTechGame"), "NetworkGameInstance");
                if (net != null && Get(net, "IsCoopCombat") is bool coop && coop) salt = Get(__instance, "GUID") as string;
            }
            catch (Exception ex) { log("fieldrepairs-coop|begin-prefix-failed|" + ex.Message); }
        }

        // Prefix on the wrapped methods: swap in a Random seeded from salt|method|target GUID; __state keeps the peer's own instance.
        static void SwapPrefix(MethodBase __originalMethod, object[] __args, ref object __state)
        {
            __state = null;
            try
            {
                if (salt == null) return;
                string site = __originalMethod.Name;
                string unit = __args != null && __args.Length > 0 ? Get(__args[0], "GUID") as string ?? "" : "";
                ulong h = Fnv64(salt + "|fieldrepairs|" + site + "|" + unit);
                var seeded = new LoggingRandom((int)(uint)(h ^ (h >> 32)), site + "|" + unit);
                object outer = randomField.GetValue(null);
                randomField.SetValue(null, seeded);
                if (!ReferenceEquals(randomField.GetValue(null), seeded))
                {
                    log("fieldrepairs-coop|swap-not-taken|" + site + "|" + unit + "|Mod.Random readback differs after SetValue");
                    return;
                }
                __state = outer;
            }
            catch (Exception ex) { log("fieldrepairs-coop|swap-failed|" + ex.GetType().Name + ": " + ex.Message); }
        }

        static void SwapFinalizer(object __state)
        {
            if (__state == null) return;
            try { randomField.SetValue(null, __state); }
            catch (Exception ex) { log("fieldrepairs-coop|restore-failed|" + ex.Message); }
        }

        // A seeded System.Random that logs its first draw (the proof that FieldRepairs' loads of Mod.Random reach this instance).
        sealed class LoggingRandom : Random
        {
            readonly string site;
            bool logged;
            public LoggingRandom(int seed, string site) : base(seed) { this.site = site; }
            void First(string call, object value)
            {
                if (logged) return;
                logged = true;
                log("fieldrepairs-coop|first-draw|" + site + "|" + call + "=" + value);
            }
            public override int Next() { int v = base.Next(); First("Next()", v); return v; }
            public override int Next(int max) { int v = base.Next(max); First("Next(" + max + ")", v); return v; }
            public override int Next(int min, int max) { int v = base.Next(min, max); First("Next(" + min + "," + max + ")", v); return v; }
            public override double NextDouble() { double v = base.NextDouble(); First("NextDouble()", v.ToString("R")); return v; }
        }

        // Wraps the peer's own FieldRepairs instance. Draws made while the field holds a seeded LoggingRandom never reach it; a draw that does
        // reach it during a co-op contract (salt set) was made outside every wrapped method: logged with its caller chain, the first 5
        // times per chain and then every 50th.
        sealed class ProxyRandom : Random
        {
            readonly Random inner;
            readonly Dictionary<string, int> seen = new Dictionary<string, int>();
            public ProxyRandom(Random inner) { this.inner = inner; }
            void Watch()
            {
                if (salt == null) return;
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
