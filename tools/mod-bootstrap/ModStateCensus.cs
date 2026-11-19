// Mod-state census for combat saves (mod support). Diagnostic only: it changes no game or mod state.
//
// HBS's combat save stores the objects it knows about. Mods keep a lot of battle state in their own static fields
// (pairings, minefields, ambush links, use counts), and the save never sees it. This takes a census of every static
// field in the loaded mod assemblies at each combat save and at each combat load, then compares the two, so a save
// and load round trip names each field that came back empty or different. The census reads fields and walks
// System.Collections containers; it calls no mod method and nothing on a Unity object.
//
// Files go to Mods/.port/census/:
//   <utc>-save-<key>.tsv   at CombatGameState.Dehydrate (written even when the save then fails)
//   <utc>-load-<key>.tsv   at TurnDirector.OnEncounterBegin of a combat loaded from a save (CombatGameState.WasFromSave):
//                          the battle is restored and about to hand out the turn. (CombatGameState.LoadComplete has no
//                          caller in the game; save-shim-step1-1 V1 showed it never runs.)
//   <utc>-diff-<key>.tsv   at load, against the newest save census with the same key
// key = <contract GUID>_r<round>_p<phase>. Columns: assembly, field, kind, count, guids, fingerprint.
// BattleTechPortBootstrap.log gets one line per census. Snapshot(label) can be called by hand (Hera) at any time.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;

namespace BattleTechPort
{
    public static class ModStateCensus
    {
        const string GameTypeName = "BattleTech.CombatGameState";
        const int MaxElements = 200000;
        static Action<string> log;
        static bool applied;
        static string modsDir;
        static string censusDir;
        static Type iGuid;
        static PropertyInfo guidProperty;
        static readonly object fileLock = new object();

        public static void Register(Action<string> logLine, string root)
        {
            log = logLine;
            modsDir = Path.GetFullPath(Path.Combine(root, "Mods")) + Path.DirectorySeparatorChar;
            censusDir = Path.Combine(Path.Combine(modsDir, ".port"), "census");
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies()) Consider(loaded);
        }

        static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args) => Consider(args.LoadedAssembly);

        static void Consider(Assembly assembly)
        {
            if (applied || assembly == null || assembly.IsDynamic) return;
            Type combat;
            try { combat = assembly.GetType(GameTypeName, false); } catch { return; }
            if (combat == null) return;
            applied = true;
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            try { Apply(assembly, combat); }
            catch (Exception ex) { log("census|FAILED|" + ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Apply(Assembly game, Type combat)
        {
            iGuid = game.GetType("BattleTech.IGuid", false);
            guidProperty = iGuid?.GetProperty("GUID");
            MethodInfo dehydrate = combat.GetMethod("Dehydrate", BindingFlags.Instance | BindingFlags.Public);
            Type turns = game.GetType("BattleTech.TurnDirector", false);
            MethodInfo encounterBegin = turns?.GetMethod("OnEncounterBegin", BindingFlags.Instance | BindingFlags.Public);
            if (dehydrate == null || encounterBegin == null || guidProperty == null)
            {
                log("census|skipped|shape changed Dehydrate=" + (dehydrate != null) + " OnEncounterBegin=" + (encounterBegin != null) + " IGuid=" + (guidProperty != null));
                return;
            }
            var harmony = new Harmony("battletech.port.modstatecensus");
            harmony.Patch(dehydrate, postfix: new HarmonyMethod(typeof(ModStateCensus).GetMethod(nameof(DehydratePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(encounterBegin, postfix: new HarmonyMethod(typeof(ModStateCensus).GetMethod(nameof(EncounterBeginPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            log("census|patched|" + game.GetName().Name + "|CombatGameState.Dehydrate and TurnDirector.OnEncounterBegin");
        }

        static void DehydratePostfix(object __instance) => Guarded("save", __instance);

        // Only for a combat that came from a save; a new battle's encounter begin is not a load.
        static void EncounterBeginPostfix(object __instance)
        {
            object combat = null;
            try
            {
                combat = __instance.GetType().GetProperty("Combat")?.GetValue(__instance, null);
                if (combat == null || !(combat.GetType().GetProperty("WasFromSave")?.GetValue(combat, null) is bool fromSave) || !fromSave) return;
            }
            catch (Exception ex) { log("census|load|FAILED|" + ex); return; }
            Guarded("load", combat);
        }

        // The census must never break a save or a load.
        static void Guarded(string label, object combat)
        {
            try { Take(label, combat); }
            catch (Exception ex) { log("census|" + label + "|FAILED|" + ex); }
        }

        // For Hera or a debugger: a census outside the save path, with no diff.
        public static string Snapshot(string label)
        {
            if (censusDir == null || iGuid == null) return null; // Wait for opt-in registration and game binding.
            try { return Take(string.IsNullOrEmpty(label) ? "manual" : label, null); }
            catch (Exception ex) { log("census|" + label + "|FAILED|" + ex); return null; }
        }

        struct Row
        {
            public string Assembly, Field, Kind, Fingerprint;
            public int Count, Guids;
        }

        static string Take(string label, object combat)
        {
            string key = combat == null ? "nocombat" : Key(combat);
            var rows = new List<Row>();
            int assemblies = 0;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!IsModAssembly(assembly)) continue;
                assemblies++;
                CensusAssembly(assembly, rows);
            }
            rows.Sort((a, b) => string.CompareOrdinal(a.Assembly + "|" + a.Field, b.Assembly + "|" + b.Field));
            string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff");
            string path;
            lock (fileLock)
            {
                Directory.CreateDirectory(censusDir);
                path = Path.Combine(censusDir, stamp + "-" + label + "-" + key + ".tsv");
                var text = new StringBuilder();
                text.Append("# ").Append(label).Append('\t').Append(key).Append('\t').Append(stamp).Append('\t').Append(assemblies).Append(" mod assemblies\n");
                text.Append("assembly\tfield\tkind\tcount\tguids\tfingerprint\n");
                foreach (Row row in rows)
                    text.Append(row.Assembly).Append('\t').Append(row.Field).Append('\t').Append(row.Kind).Append('\t')
                        .Append(row.Count).Append('\t').Append(row.Guids).Append('\t').Append(row.Fingerprint).Append('\n');
                File.WriteAllText(path, text.ToString());
            }
            int stateful = 0;
            foreach (Row row in rows) if (row.Count > 0) stateful++;
            log("census|" + label + "|" + key + "|assemblies=" + assemblies + "|fields=" + rows.Count + "|nonempty=" + stateful + "|" + path);
            if (label == "load") Diff(key, rows, stamp);
            return path;
        }

        static string Key(object combat)
        {
            string guid = "unknown";
            int round = -1, phase = -1;
            try
            {
                object contract = combat.GetType().GetProperty("ActiveContract")?.GetValue(combat, null);
                if (contract != null && guidProperty.DeclaringType.IsInstanceOfType(contract))
                    guid = (string)guidProperty.GetValue(contract, null) ?? "unknown";
                object turns = combat.GetType().GetProperty("TurnDirector")?.GetValue(combat, null);
                if (turns != null)
                {
                    round = (int)turns.GetType().GetProperty("CurrentRound").GetValue(turns, null);
                    phase = (int)turns.GetType().GetProperty("CurrentPhase").GetValue(turns, null);
                }
            }
            catch { }
            var safe = new StringBuilder();
            foreach (char c in guid) safe.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
            return safe + "_r" + round + "_p" + phase;
        }

        // Assemblies loaded from under Mods\ (a mod folder, or ModTek's Mods\.modtek cache of shimmed copies), less ModTek
        // and its libraries, UCU's own copies under Mods\.port, and the game assembly (ModTek's injected copy of
        // Assembly-CSharp can also sit in Mods\.modtek).
        static bool IsModAssembly(Assembly assembly)
        {
            if (assembly.IsDynamic || assembly == typeof(ModStateCensus).Assembly || assembly == iGuid.Assembly) return false;
            string location;
            try { location = assembly.Location; } catch { return false; }
            if (string.IsNullOrEmpty(location)) return false;
            string full;
            try { full = Path.GetFullPath(location); } catch { return false; }
            if (!full.StartsWith(modsDir, StringComparison.OrdinalIgnoreCase)) return false;
            string relative = full.Substring(modsDir.Length);
            if (relative.StartsWith("ModTek" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith(".port" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            string name = assembly.GetName().Name;
            foreach (string library in new[] { "ModTek", "0Harmony", "Harmony", "Mono.Cecil", "MonoMod", "Assembly-CSharp", "HBS.", "UnityEngine", "System", "Newtonsoft" })
                if (name.StartsWith(library, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        static void CensusAssembly(Assembly assembly, List<Row> rows)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch { return; }
            string name = assembly.GetName().Name;
            foreach (Type type in types)
            {
                if (type == null || type.ContainsGenericParameters || type.Name.StartsWith("<")) continue;
                FieldInfo[] fields;
                try { fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (FieldInfo field in fields)
                {
                    if (field.IsLiteral) continue;
                    string fieldName = field.Name;
                    if (fieldName.StartsWith("<"))
                    {
                        int end = fieldName.IndexOf(">k__BackingField", StringComparison.Ordinal);
                        if (end < 0) continue; // cached delegates and other compiler fields
                        fieldName = fieldName.Substring(1, end - 1);
                    }
                    object value;
                    try { value = field.GetValue(null); }
                    catch { continue; } // a static constructor that throws, say
                    Row row;
                    if (!Describe(value, out row.Kind, out row.Count, out row.Guids, out row.Fingerprint)) continue;
                    row.Assembly = name;
                    row.Field = type.FullName + "." + fieldName;
                    rows.Add(row);
                }
            }
        }

        // What is recorded: collections from System.Collections* and arrays (count, IGuid elements, an order-free hash of
        // their elements), single IGuid objects, and primitives, strings and enums (which may be flags or counters). A mod's
        // settings object or a Unity object is not: neither is battle state the save could carry.
        static bool Describe(object value, out string kind, out int count, out int guids, out string fingerprint)
        {
            kind = null; count = 0; guids = 0; fingerprint = "";
            if (value == null) return false;
            Type type = value.GetType();
            if (value is string || type.IsPrimitive || type.IsEnum)
            {
                kind = "scalar";
                count = 1;
                fingerprint = Clip(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                return true;
            }
            if (IsGuid(value))
            {
                kind = "object";
                count = 1;
                guids = 1;
                fingerprint = Clip(GuidOf(value));
                return true;
            }
            bool collection = type.IsArray || (type.Namespace != null && type.Namespace.StartsWith("System.Collections", StringComparison.Ordinal));
            if (!collection || !(value is IEnumerable enumerable)) return false;
            kind = Short(type);
            ulong hash = 0;
            try
            {
                if (value is IDictionary dictionary)
                {
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (++count > MaxElements) break;
                        guids += (IsGuid(entry.Key) ? 1 : 0) + (IsGuid(entry.Value) ? 1 : 0);
                        hash += Hash(Element(entry.Key) + "=" + Element(entry.Value));
                    }
                }
                else
                {
                    foreach (object element in enumerable)
                    {
                        if (++count > MaxElements) break;
                        if (IsGuid(element)) guids++;
                        hash += Hash(Element(element));
                    }
                }
            }
            catch (Exception ex)
            {
                fingerprint = "unreadable:" + ex.GetType().Name;
                return true;
            }
            fingerprint = hash.ToString("x16");
            return true;
        }

        static bool IsGuid(object value) => value != null && iGuid.IsInstanceOfType(value);

        static string GuidOf(object value)
        {
            try { return "guid:" + (guidProperty.GetValue(value, null) as string); }
            catch { return "guid:?"; }
        }

        // An element's identity for the hash: GUID for game objects, value for scalars, type name otherwise. Key/value
        // pairs from a non-IDictionary map count both halves.
        static string Element(object value)
        {
            if (value == null) return "null";
            if (IsGuid(value)) return GuidOf(value);
            Type type = value.GetType();
            if (value is string || type.IsPrimitive || type.IsEnum)
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                return Element(type.GetProperty("Key").GetValue(value, null)) + "=" + Element(type.GetProperty("Value").GetValue(value, null));
            return "type:" + type.FullName;
        }

        // FNV-1a, 64-bit; summed over elements, so the order of a dictionary or set doesn't matter.
        static ulong Hash(string text)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in text) { hash ^= c; hash *= 1099511628211UL; }
            return hash;
        }

        static string Short(Type type)
        {
            if (type.IsArray) return "array";
            string name = type.Name;
            int tick = name.IndexOf('`');
            return tick < 0 ? name : name.Substring(0, tick);
        }

        static string Clip(string text)
        {
            if (text == null) return "";
            text = text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
            return text.Length > 80 ? text.Substring(0, 80) + "..." : text;
        }

        // Lost: had elements or a value at save, nothing at load. Changed: different count or fingerprint. Scalars that
        // changed are listed too; the census cannot tell a counter from a setting a mod rewrote on load.
        static void Diff(string key, List<Row> loaded, string stamp)
        {
            string savePath = null;
            foreach (string candidate in Directory.GetFiles(censusDir, "*-save-" + key + ".tsv"))
                if (savePath == null || string.CompareOrdinal(Path.GetFileName(candidate), Path.GetFileName(savePath)) > 0) savePath = candidate;
            if (savePath == null)
            {
                log("census|diff|" + key + "|no save census with this key in " + censusDir);
                return;
            }
            var atLoad = new Dictionary<string, Row>();
            foreach (Row row in loaded) atLoad[row.Assembly + "\t" + row.Field] = row;
            var text = new StringBuilder();
            text.Append("# diff\t").Append(key).Append('\t').Append(Path.GetFileName(savePath)).Append('\n');
            text.Append("status\tassembly\tfield\tkind\tsave count\tload count\tsave guids\tload guids\tsave fingerprint\tload fingerprint\n");
            int lost = 0, changed = 0;
            foreach (string line in File.ReadAllLines(savePath))
            {
                if (line.StartsWith("#") || line.StartsWith("assembly\t")) continue;
                string[] cells = line.Split('\t');
                if (cells.Length < 6) continue;
                int count = int.Parse(cells[3]);
                if (count == 0) continue;
                Row after;
                bool present = atLoad.TryGetValue(cells[0] + "\t" + cells[1], out after);
                string status;
                if (!present || after.Count == 0) { status = "LOST"; lost++; }
                else if (after.Count != count || after.Fingerprint != cells[5]) { status = "CHANGED"; changed++; }
                else continue;
                text.Append(status).Append('\t').Append(cells[0]).Append('\t').Append(cells[1]).Append('\t').Append(cells[2]).Append('\t')
                    .Append(count).Append('\t').Append(present ? after.Count : 0).Append('\t')
                    .Append(cells[4]).Append('\t').Append(present ? after.Guids : 0).Append('\t')
                    .Append(cells[5]).Append('\t').Append(present ? after.Fingerprint : "").Append('\n');
            }
            string path;
            lock (fileLock)
            {
                path = Path.Combine(censusDir, stamp + "-diff-" + key + ".tsv");
                File.WriteAllText(path, text.ToString());
            }
            log("census|diff|" + key + "|lost=" + lost + "|changed=" + changed + "|" + path);
        }
    }
}
