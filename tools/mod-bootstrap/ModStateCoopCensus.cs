// The co-op mod-state census (post-4B housekeeping, family L2): names the mod state a snapshot load lost without anyone sweeping for it.
// On a co-op snapshot load the host sends, as one more side-channel source (CoopInvocationSideData, patch 166; registered after every
// carry, so the guest applies it last), a hash of every carry-encodable static field of every mod type that is not empty: the fields
// ModStateCarry could carry (its own Encodable test), with a collection's count and an order-free fingerprint of its encoded contents.
// The guest hashes its own the same way after the carries are applied and logs each field that differs by name:
//   `census|diff|<assembly>|<Type.field>|lost|host=<count>#<hash>|guest=<count>#<hash>`  (host has state the guest does not),
//   `|extra|` (guest only) or `|changed|`; a host field the guest's table does not hold is `census|diff|?<name hash>|unknown`.
// A field the compare names is not carried yet: it needs a CarryDecl row in LoadSteps (or is a per-peer value that is allowed to differ).
// Rows: `census|armed|...` / `census|not-armed|coop=<bool>|mod-assemblies=<n>|reason=...` at the first combat, `census|scan-time|ms=...`
// (the reflection pass, once, at the first snapshot load), `census|capture|...|ms=` (host) and `census|compare|...|ms=` (guest): the cost.
// Arms only with at least one non-UCU mod assembly loaded (the same scope as CoopRngSweepDeterminism's guard; the source only ever runs in a
// co-op combat), at first combat, and does no reflection on a mod type before the first snapshot load (fault M timing).
// Known cost and risk: reading a static field runs its type's static constructor if it has not run. By a snapshot load every mod has run its
// Init, which is when a constructor that reads the mod's config is safe (lessons/patch-mod-methods-after-mod-init); a constructor that
// throws is counted (`cctor-failed`) and skipped. shortcut: no way on this runtime to read only already-initialised types; upgrade by
// listing the types a leg shows to misbehave.
// shortcut: the non-UCU mod assembly test below copies CoopRngSweepDeterminism.IsModAssembly (that file is another executor's during this
// pass); fold the two into one when it is free.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace BattleTechPort
{
    public static class ModStateCoopCensus
    {
        const string Tag = "census";
        const int MaxDiffRows = 300;
        static Action<string> log;
        static readonly string[] UcuAssemblies = { "CoopMod", "CorruptOnce", "FirstRunSkip", "SaveProbe", "LoadProbe", "RouteMod" };
        static readonly Dictionary<string, bool> KeepManaged = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        { { "Newtonsoft.Json", true }, { "YamlDotNet", true }, { "MessagePack", true }, { "EPPlus", true }, { "ManagedBass", true } };
        static Dictionary<string, bool> managedNames;

        sealed class Entry { public string Assembly, Name; public uint NameHash; public FieldInfo Field; }
        struct Hit { public Entry E; public int Count; public ulong Print; }

        static List<Entry> table;
        static int scanAssemblies, scanTypes, scanFields;
        static int cctorFailed, unreadable, unhashable;
        static readonly Dictionary<string, bool> cctorNamed = new Dictionary<string, bool>();

        // At first combat (called by LoadSteps after the carries). One row says why it is or is not armed.
        public static void Arm(Action<string> logLine)
        {
            log = logLine;
            bool coop = IsCoopCombat();
            List<Assembly> mods = ModAssemblies();
            if (mods.Count == 0)
            {
                log(Tag + "|not-armed|coop=" + coop + "|mod-assemblies=0|reason=no non-UCU mod assembly loaded");
                return;
            }
            if (!ModStateCarry.RegisterSource(Tag, Capture, Apply, log)) return;
            log(Tag + "|armed|coop=" + coop + "|mod-assemblies=" + mods.Count + "|source registered last, hashed at a co-op snapshot load");
        }

        static bool IsCoopCombat()
        {
            try
            {
                object game = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null, null);
                return ModStateCarry.Get(ModStateCarry.Get(game, "NetworkGameInstance"), "IsCoopCombat") is bool b && b;
            }
            catch { return false; }
        }

        // ---- the scan: every encodable static field of every mod type ----

        static List<Entry> Scan()
        {
            if (table != null) return table;
            long t0 = Now();
            var list = new List<Entry>();
            var encodable = new Dictionary<Type, bool>();
            List<Assembly> mods = ModAssemblies();
            scanAssemblies = mods.Count;
            foreach (Assembly a in mods)
            {
                Type[] types;
                try { types = a.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                string asm = a.GetName().Name;
                foreach (Type type in types)
                {
                    if (type == null || type.ContainsGenericParameters || type.Name.StartsWith("<", StringComparison.Ordinal)) continue;
                    scanTypes++;
                    FieldInfo[] fields;
                    try { fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                    catch { continue; }
                    foreach (FieldInfo f in fields)
                    {
                        if (f.IsLiteral) continue;
                        string name = f.Name;
                        if (name.StartsWith("<", StringComparison.Ordinal))
                        {
                            int end = name.IndexOf(">k__BackingField", StringComparison.Ordinal);
                            if (end < 0) continue;   // cached delegates and other compiler fields
                            name = name.Substring(1, end - 1);
                        }
                        scanFields++;
                        Type ft;
                        try { ft = f.FieldType; } catch { continue; }
                        bool ok;
                        if (!encodable.TryGetValue(ft, out ok))
                        {
                            try { ok = ModStateCarry.Encodable(ft); } catch { ok = false; }
                            encodable[ft] = ok;
                        }
                        if (!ok) continue;
                        string full = asm + "|" + type.FullName + "." + name;
                        list.Add(new Entry { Assembly = asm, Name = full, NameHash = Fnv32(full), Field = f });
                    }
                }
            }
            list.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
            table = list;
            log(Tag + "|scan-time|ms=" + (Now() - t0) + "|assemblies=" + scanAssemblies + "|types=" + scanTypes + "|static-fields=" + scanFields + "|encodable=" + list.Count);
            return list;
        }

        // The fields that hold state, with a count and a fingerprint. A field whose read or hash fails is counted and left out.
        static List<Hit> Hash(out long ms)
        {
            long t0 = Now();
            cctorFailed = unreadable = unhashable = 0;
            var hits = new List<Hit>();
            foreach (Entry e in Scan())
            {
                object v;
                try { v = e.Field.GetValue(null); }
                catch (Exception ex)
                {
                    if (ex is TypeInitializationException || ex.InnerException is TypeInitializationException)
                    {
                        if (++cctorFailed == 1 && !cctorNamed.ContainsKey(e.Name)) { cctorNamed[e.Name] = true; log(Tag + "|cctor-failed|" + e.Name + "|" + (ex.InnerException ?? ex).GetType().Name); }
                    }
                    else unreadable++;
                    continue;
                }
                int count;
                try { count = ModStateCarry.Filled(v); }
                catch { unreadable++; continue; }   // a collection another thread is changing
                if (count == 0) continue;
                try { hits.Add(new Hit { E = e, Count = count, Print = Fingerprint(v, e.Field.FieldType) }); }
                catch { unhashable++; }
            }
            ms = Now() - t0;
            return hits;
        }

        // Order-free for a dictionary or a set (a reloaded peer may hold the same content in another order), the encoding's hash otherwise.
        static ulong Fingerprint(object v, Type t)
        {
            if (t.IsGenericType && v is IDictionary d && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                Type[] a = t.GetGenericArguments();
                ulong sum = 0;
                foreach (DictionaryEntry e in d) sum += Fnv64(Norm(ModStateCarry.Encoded(e.Key, a[0]) + "=" + ModStateCarry.Encoded(e.Value, a[1])));
                return sum;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Collections.Generic.HashSet`1" && v is IEnumerable set)
            {
                Type e0 = t.GetGenericArguments()[0];
                ulong sum = 0;
                foreach (object o in set) sum += Fnv64(Norm(ModStateCarry.Encoded(o, e0)));
                return sum;
            }
            return Fnv64(Norm(ModStateCarry.Encoded(v, t)));
        }

        // Each copy of the game sits in its own folder, so a field holding a path under it (a mod's directory, a log file) differs between
        // two peers on one machine and, on two machines, would differ the same way. The game folder is replaced by "<game>" before hashing,
        // so such a field compares equal when the peers differ only in where they are installed.
        static string gameDir, gameDirSlash;
        static string Norm(string s)
        {
            if (gameDir == null) { gameDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'); gameDirSlash = gameDir.Replace('\\', '/'); }
            if (gameDir.Length == 0) return s;
            string r = Replace(Replace(s, gameDir), gameDirSlash);
            return ReferenceEquals(r, s) || r.Length == s.Length && r == s ? s : StripLengths(r);
        }

        // The encoding prefixes a string with its length ("s<n>:"), which differs when the folder names differ in length: dropped from the hash
        // text of a value that held the game folder.
        static string StripLengths(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                sb.Append(s[i]);
                if (s[i] != 's') continue;
                int j = i + 1;
                while (j < s.Length && s[j] >= '0' && s[j] <= '9') j++;
                if (j > i + 1 && j < s.Length && s[j] == ':') i = j - 1;
            }
            return sb.ToString();
        }

        static string Replace(string s, string what)
        {
            if (s.IndexOf(what, StringComparison.OrdinalIgnoreCase) < 0) return s;
            var sb = new StringBuilder();
            int at = 0;
            while (true)
            {
                int i = s.IndexOf(what, at, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;
                sb.Append(s, at, i - at).Append("<game>");
                at = i + what.Length;
            }
            return sb.Append(s, at, s.Length - at).ToString();
        }

        // ---- the source ----

        // Host: the fields that hold state, for a peer that has just loaded (invocation == null).
        static string Capture(object invocation)
        {
            if (invocation != null) return null;
            try
            {
                if (ModStateCarry.Combat() == null) return null;
                List<Hit> hits = Hash(out long ms);
                var sb = new StringBuilder("c1;");
                sb.Append(hits.Count.ToString(CultureInfo.InvariantCulture)).Append(';');
                foreach (Hit h in hits)
                    sb.Append(h.E.NameHash.ToString("x8", CultureInfo.InvariantCulture)).Append(',').Append(h.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(h.Print.ToString("x16", CultureInfo.InvariantCulture)).Append(';');
                log(Tag + "|capture|fields=" + scanFields + "|encodable=" + table.Count + "|nonempty=" + hits.Count + "|cctor-failed=" + cctorFailed + "|unreadable=" + unreadable
                    + "|unhashable=" + unhashable + "|payload=" + sb.Length + "|ms=" + ms + "|at=full");
                return sb.ToString();
            }
            catch (Exception e) { log(Tag + "|FAILED|capture|" + ModStateCarry.Inner(e)); return null; }
        }

        // Guest: the compare, after every carry has been applied.
        static void Apply(object invocation, string data)
        {
            if (invocation != null || string.IsNullOrEmpty(data)) return;
            try
            {
                if (ModStateCarry.Combat() == null) return;
                var host = Parse(data);
                List<Hit> hits = Hash(out long ms);
                var mine = new Dictionary<uint, Hit>();
                foreach (Hit h in hits) mine[h.E.NameHash] = h;
                var byHash = new Dictionary<uint, Entry>();
                foreach (Entry e in table) byHash[e.NameHash] = e;
                var rows = new List<string>();
                int lost = 0, extra = 0, changed = 0, unknown = 0;
                foreach (KeyValuePair<uint, KeyValuePair<int, ulong>> hf in host)
                {
                    Entry e;
                    if (!byHash.TryGetValue(hf.Key, out e)) { unknown++; rows.Add(Tag + "|diff|?" + hf.Key.ToString("x8", CultureInfo.InvariantCulture) + "|unknown|host=" + hf.Value.Key + "#" + Short(hf.Value.Value) + "|guest=not in table"); continue; }
                    if (!mine.TryGetValue(hf.Key, out Hit g)) { lost++; rows.Add(Row(e, "lost", hf.Value.Key, hf.Value.Value, 0, 0)); }
                    else if (g.Count != hf.Value.Key || g.Print != hf.Value.Value) { changed++; rows.Add(Row(e, "changed", hf.Value.Key, hf.Value.Value, g.Count, g.Print)); }
                }
                foreach (KeyValuePair<uint, Hit> m in mine)
                    if (!host.ContainsKey(m.Key)) { extra++; rows.Add(Row(m.Value.E, "extra", 0, 0, m.Value.Count, m.Value.Print)); }
                rows.Sort(StringComparer.Ordinal);
                for (int i = 0; i < rows.Count && i < MaxDiffRows; i++) log(rows[i]);
                log(Tag + "|compare|host-nonempty=" + host.Count + "|guest-nonempty=" + hits.Count + "|differ=" + rows.Count + "|lost=" + lost + "|extra=" + extra + "|changed=" + changed
                    + "|unknown=" + unknown + "|rows-capped=" + Math.Max(0, rows.Count - MaxDiffRows) + "|cctor-failed=" + cctorFailed + "|unreadable=" + unreadable
                    + "|unhashable=" + unhashable + "|ms=" + ms + "|at=apply");
            }
            catch (Exception e) { log(Tag + "|apply-failed|" + ModStateCarry.Inner(e)); }
        }

        static string Row(Entry e, string kind, int hc, ulong hp, int gc, ulong gp)
            => Tag + "|diff|" + e.Assembly + "|" + e.Name.Substring(e.Assembly.Length + 1) + "|" + kind + "|host=" + hc + "#" + Short(hp) + "|guest=" + gc + "#" + Short(gp);

        static string Short(ulong h) => h.ToString("x16", CultureInfo.InvariantCulture).Substring(0, 8);

        static Dictionary<uint, KeyValuePair<int, ulong>> Parse(string data)
        {
            var map = new Dictionary<uint, KeyValuePair<int, ulong>>();
            string[] parts = data.Split(';');
            if (parts.Length < 2 || parts[0] != "c1") throw new FormatException("census payload version");
            for (int i = 2; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                string[] f = parts[i].Split(',');
                if (f.Length != 3) throw new FormatException("census entry " + parts[i]);
                map[uint.Parse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture)] =
                    new KeyValuePair<int, ulong>(int.Parse(f[1], CultureInfo.InvariantCulture), ulong.Parse(f[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            return map;
        }

        // Harness support (CoopMod/CensusPlant.cs compiles this file in; the bootstrap never calls it): the encodable fields of the named
        // mod assemblies (all when none are named) that hold state, as "<assembly>|<Type.field>=<count>", in name order.
        internal static List<string> NonEmpty(string[] assemblyNames, Action<string> logLine)
        {
            if (log == null) log = logLine;
            var list = new List<string>();
            foreach (Entry e in Scan())
            {
                if (assemblyNames != null && assemblyNames.Length > 0 && Array.IndexOf(assemblyNames, e.Assembly) < 0) continue;
                object v;
                try { v = e.Field.GetValue(null); } catch { continue; }
                int n;
                try { n = ModStateCarry.Filled(v); } catch { continue; }
                if (n > 0) list.Add(e.Name + "=" + n);
            }
            return list;
        }

        // The first field, in name order, of the named mod assemblies that holds a collection with entries (the harness plants a lost field
        // by clearing it; it names mods whose state no CarryDecl carries). Null when none does.
        internal static FieldInfo PlantCandidate(string[] assemblyNames, Action<string> logLine, out int count)
        {
            count = 0;
            if (log == null) log = logLine;
            foreach (Entry e in Scan())
            {
                if (Array.IndexOf(assemblyNames, e.Assembly) < 0) continue;
                object v;
                try { v = e.Field.GetValue(null); } catch { continue; }
                if (!(v is IDictionary) && !(v is IList)) continue;
                count = ModStateCarry.Filled(v);
                if (count > 0) return e.Field;
            }
            return null;
        }

        // ---- the non-UCU mod assemblies (as CoopRngSweepDeterminism's guard counts them) ----

        // The third-party libraries a mod ships (KeepManaged) are mod assemblies for the RNG guard but hold no battle state, and reading a static of one
        // runs its type initializer (L1-b: EPPlus ZipFile.BufferSizeDefault threw NotSupportedException): the census skips them.
        static List<Assembly> ModAssemblies()
        {
            var list = new List<Assembly>();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) if (IsModAssembly(a) && !KeepManaged.ContainsKey(a.GetName().Name)) list.Add(a);
            return list;
        }

        static bool IsModAssembly(Assembly a)
        {
            if (a == null || a.IsDynamic || a == typeof(ModStateCoopCensus).Assembly) return false;
            string location;
            try { location = a.Location; } catch { return false; }
            if (string.IsNullOrEmpty(location)) return false;
            string full;
            try { full = Path.GetFullPath(location); } catch { return false; }
            int i = full.IndexOf("\\Mods\\", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            string rel = full.Substring(i + 6);
            if (rel.StartsWith("ModTek\\", StringComparison.OrdinalIgnoreCase)) return false;
            if (rel.StartsWith(".port\\", StringComparison.OrdinalIgnoreCase)) return false;
            string name;
            try { name = a.GetName().Name; } catch { return false; }
            if (Array.IndexOf(UcuAssemblies, name) >= 0) return false;
            if (name == "Assembly-CSharp" || name == "Assembly-CSharp-firstpass" || name == "mscorlib" || name == "netstandard") return false;
            if (name == "System" || name.StartsWith("System.", StringComparison.Ordinal)) return false;
            if (name == "Unity" || name.StartsWith("Unity.", StringComparison.Ordinal) || name.StartsWith("UnityEngine", StringComparison.Ordinal)) return false;
            if (name.StartsWith("DOTween", StringComparison.Ordinal)) return false;
            if (name == "Mono" || name.StartsWith("Mono.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)) return false;
            if (name.StartsWith("BattleTech.", StringComparison.Ordinal) || name.StartsWith("BattleTechPort", StringComparison.Ordinal)) return false;
            if (name.StartsWith("ModTek", StringComparison.Ordinal) || name == "0Harmony" || name.StartsWith("0Harmony.", StringComparison.Ordinal)
                || name.StartsWith("HarmonyX", StringComparison.Ordinal) || name.StartsWith("Ionic", StringComparison.Ordinal)) return false;
            if (ManagedNames().ContainsKey(name) && !KeepManaged.ContainsKey(name)) return false;
            return true;
        }

        static Dictionary<string, bool> ManagedNames()
        {
            if (managedNames != null) return managedNames;
            var set = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Path.Combine("BattleTech_Data", "Managed"));
                foreach (string f in Directory.GetFiles(dir, "*.dll")) set[Path.GetFileNameWithoutExtension(f)] = true;
            }
            catch { }
            return managedNames = set;
        }

        static long Now() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        static uint Fnv32(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h;
        }

        static ulong Fnv64(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
            return h;
        }
    }
}
