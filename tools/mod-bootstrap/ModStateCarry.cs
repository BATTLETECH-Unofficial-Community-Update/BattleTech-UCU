// Mod static state sent to a co-op peer that loads a resync or rejoin snapshot (gh issue 24, snapshot-field sweep;
// .hermes/reports/issue24-sweep-1/snapshot-fields.tsv, Sam 2026-11-22: fix the one-peer and not-carried class before 4B).
// A co-op guest's resync reload destroys its combat; mods clear their static state there (OnCombatGameDestroyed) or key it by actor objects
// the reload replaces, and the save holds none of it. The host never reloads. This engine, given a mod's static fields, registers one source
// with UCU's co-op side channel (CoopInvocationSideData, patch 166): the host's full state for a peer that has just loaded (capture(null))
// is the fields' values, encoded by their declared types; the guest's apply(null) decodes them against its new combat and replaces its own
// values (collections in place). Nothing is sent with an invocation: every field carried here is written on every peer alike while play
// runs (the source adapter states the basis), so only the reload loses it.
// What it encodes: string, bool, the integer and float types, enums, UnityEngine.Vector3 and Color, an AbstractActor / ICombatant /
// Building reference (by GUID, found with CombatGameState.FindCombatantByGUID), a Team (by GUID), List<T>, HashSet<T>, Dictionary<K,V>,
// and a class whose instance fields are all of those (made with FormatterServices.GetUninitializedObject, so no constructor runs). A field
// of any other type (a delegate, an Ability, a Transform, a def object) is refused when the source installs and is logged by name; it is
// not carried. A reference whose GUID the guest cannot find is dropped from its collection and logged `<tag>|unmapped|<field>|<guid>`.
// Rows (same format on both peers, so they diff as text): host `<tag>|capture|<field>=<count>#<fnv>,...|at=full`, guest
// `<tag>|before|...|at=apply` and `<tag>|apply|...|at=apply`; `<tag>|installed|...`, `<tag>|refused|<field>|<reason>`, `<tag>|FAILED|...`,
// `<tag>|apply-failed|...`, and at each combat's end (CombatGameState.OnCombatGameDestroyed) `<tag>|count|capture=<n>|apply=<n>|nonempty=<n>`.
// shortcut: a third peer's held order and the full state share the host's one relay order, as for patch 166's other sources.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using HarmonyLib;

namespace BattleTechPort
{
    // Per-source counts, logged at each combat's end (one OnCombatGameDestroyed prefix for every source of this pass).
    public sealed class CoopCarryCounter
    {
        static readonly List<CoopCarryCounter> all = new List<CoopCarryCounter>();
        static bool patched;
        static Action<string> log;
        public readonly string Tag;
        public int Capture, Apply, NonEmpty;

        CoopCarryCounter(string tag) { Tag = tag; }

        public static CoopCarryCounter For(string tag, Action<string> logLine)
        {
            lock (all)
            {
                log = logLine;
                var c = new CoopCarryCounter(tag);
                all.Add(c);
                if (!patched)
                {
                    patched = true;
                    PatchDestroyed(logLine);
                }
                return c;
            }
        }

        // Its own method, so a caller's JIT does not need HarmonyLib until a patch is made.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void PatchDestroyed(Action<string> logLine)
        {
            MethodInfo destroyed = ModAssemblyHook.GameType("BattleTech.CombatGameState")?.GetMethod("OnCombatGameDestroyed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (destroyed == null) { logLine("coop-carry|FAILED|CombatGameState.OnCombatGameDestroyed not found: no count rows"); return; }
            new Harmony("battletech.port.coop-carry-counts").Patch(destroyed,
                prefix: new HarmonyMethod(typeof(CoopCarryCounter).GetMethod(nameof(Destroyed), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
        }

        static void Destroyed()
        {
            lock (all)
                foreach (CoopCarryCounter c in all)
                {
                    log(c.Tag + "|count|capture=" + c.Capture + "|apply=" + c.Apply + "|nonempty=" + c.NonEmpty);
                    c.Capture = c.Apply = c.NonEmpty = 0;
                }
        }
    }

    public static class ModStateCarry
    {
        sealed class Source { public string Tag; public FieldInfo[] Fields; public CoopCarryCounter Count; public int Rows; }

        static Action<string> log;
        static PropertyInfo battleTechGame;
        static MethodInfo findCombatant, register;
        static Type actorType, combatantType, buildingType, teamType;

        // Registers a source for the named static fields of owner. Returns the fields carried (refused ones are logged and left out).
        public static int Install(string tag, Type owner, string[] fieldNames, string basis, Action<string> logLine)
        {
            log = logLine;
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                if (!Resolve())
                {
                    logLine(tag + "|FAILED|shape changed CoopInvocationSideData.Register=" + (register != null) + " FindCombatantByGUID=" + (findCombatant != null)
                        + " BattleTechGame=" + (battleTechGame != null) + " (needs UCU patch 166)");
                    return 0;
                }
                if (owner == null) { logLine(tag + "|FAILED|owner type not found"); return 0; }
                var fields = new List<FieldInfo>();
                foreach (string name in fieldNames)
                {
                    FieldInfo f = owner.GetField(name, st);
                    if (f == null) { logLine(tag + "|refused|" + name + "|no static field " + owner.FullName + "." + name); continue; }
                    string why = Refuse(f.FieldType, new Dictionary<Type, bool>());
                    if (why != null) { logLine(tag + "|refused|" + name + "|" + why); continue; }
                    fields.Add(f);
                }
                if (fields.Count == 0) { logLine(tag + "|FAILED|no field of " + owner.FullName + " can be carried"); return 0; }
                var src = new Source { Tag = tag, Fields = fields.ToArray(), Count = CoopCarryCounter.For(tag, logLine) };
                register.Invoke(null, new object[] { tag, new Func<object, string>(inv => Capture(src, inv)), new Action<object, string>((inv, data) => Apply(src, inv, data)) });
                var names = new StringBuilder();
                foreach (FieldInfo f in fields) { if (names.Length > 0) names.Append(','); names.Append(f.Name); }
                logLine(tag + "|installed|" + owner.FullName + "|" + names + "|sent to a peer that loads a co-op snapshot|" + basis);
                return fields.Count;
            }
            catch (Exception e) { logLine(tag + "|FAILED|" + Inner(e)); return 0; }
        }

        static bool Resolve()
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            if (register != null) return true;
            actorType = ModAssemblyHook.GameType("BattleTech.AbstractActor");
            combatantType = ModAssemblyHook.GameType("BattleTech.ICombatant");
            buildingType = ModAssemblyHook.GameType("BattleTech.Building");
            teamType = ModAssemblyHook.GameType("BattleTech.Team");
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", st);
            findCombatant = ModAssemblyHook.GameType("BattleTech.CombatGameState")?.GetMethod("FindCombatantByGUID", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(bool) }, null);
            MethodInfo r = ModAssemblyHook.GameType("BattleTech.CoopInvocationSideData")?.GetMethod("Register", st);
            if (r == null || findCombatant == null || battleTechGame == null || actorType == null || combatantType == null || teamType == null) return false;
            register = r;
            return true;
        }

        static object Combat() => Get(battleTechGame.GetValue(null, null), "Combat");

        static string Capture(Source src, object invocation)
        {
            if (invocation != null) return null;
            try
            {
                if (Combat() == null) return null;
                var sb = new StringBuilder();
                var summary = new StringBuilder();
                bool any = false;
                foreach (FieldInfo f in src.Fields)
                {
                    object v = f.GetValue(null);
                    int start = sb.Length;
                    Encode(v, f.FieldType, sb);
                    if (Count(v) > 0) any = true;
                    Summary(summary, f.Name, v, sb.ToString(start, sb.Length - start));
                }
                src.Count.Capture++;
                if (any) src.Count.NonEmpty++;
                Row(src, "capture", summary.ToString(), "full");
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
            }
            catch (Exception e) { log(src.Tag + "|FAILED|capture|" + Inner(e)); return null; }
        }

        static void Apply(Source src, object invocation, string data)
        {
            if (invocation != null || string.IsNullOrEmpty(data)) return;
            try
            {
                object combat = Combat();
                if (combat == null) return;
                string text = Encoding.UTF8.GetString(Convert.FromBase64String(data));
                var before = new StringBuilder();
                foreach (FieldInfo f in src.Fields) { object v = f.GetValue(null); Summary(before, f.Name, v, Encoded(v, f.FieldType)); }
                Row(src, "before", before.ToString(), "apply");
                var r = new Reader(text, combat, src);
                var decoded = new object[src.Fields.Length];
                for (int i = 0; i < src.Fields.Length; i++) decoded[i] = r.Read(src.Fields[i].FieldType, src.Fields[i].Name);
                if (r.Pos != text.Length) throw new FormatException("trailing data at " + r.Pos + " of " + text.Length);
                // Decoded in full before anything is written, so a malformed payload changes nothing.
                bool any = false;
                var after = new StringBuilder();
                for (int i = 0; i < src.Fields.Length; i++)
                {
                    Store(src.Fields[i], decoded[i]);
                    object v = src.Fields[i].GetValue(null);
                    if (Count(v) > 0) any = true;
                    Summary(after, src.Fields[i].Name, v, Encoded(v, src.Fields[i].FieldType));
                }
                src.Count.Apply++;
                if (any) src.Count.NonEmpty++;
                Row(src, "apply", after.ToString(), "apply");
            }
            catch (Exception e) { log(src.Tag + "|apply-failed|" + Inner(e)); }
        }

        // A collection already in the field is refilled in place (a mod may hold the same instance elsewhere); anything else is set.
        static void Store(FieldInfo f, object value)
        {
            object cur = f.GetValue(null);
            if (cur != null && value != null && cur.GetType() == value.GetType())
            {
                if (cur is IDictionary cd && value is IDictionary vd) { cd.Clear(); foreach (DictionaryEntry e in vd) cd[e.Key] = e.Value; return; }
                if (cur is IList cl && value is IList vl) { cl.Clear(); foreach (object o in vl) cl.Add(o); return; }
                MethodInfo clear = cur.GetType().GetMethod("Clear", Type.EmptyTypes);
                MethodInfo add = cur.GetType().GetMethod("Add");
                if (clear != null && add != null && add.GetParameters().Length == 1 && value is IEnumerable ve)
                {
                    clear.Invoke(cur, null);
                    foreach (object o in ve) add.Invoke(cur, new[] { o });
                    return;
                }
            }
            f.SetValue(null, value);
        }

        // ---- type check ----
        // HashSet<T> is matched by name: this assembly references mscorlib only, and HashSet lives in System.Core.
        static bool IsSetOrList(Type g) => g == typeof(List<>) || g.FullName == "System.Collections.Generic.HashSet`1";

        static string Refuse(Type t, Dictionary<Type, bool> visiting)
        {
            if (Scalar(t) || IsRef(t) || t.FullName == "UnityEngine.Vector3" || t.FullName == "UnityEngine.Color") return null;
            if (t.IsGenericType)
            {
                Type g = t.GetGenericTypeDefinition();
                Type[] a = t.GetGenericArguments();
                if (IsSetOrList(g)) return Refuse(a[0], visiting);
                if (g == typeof(Dictionary<,>)) return Refuse(a[0], visiting) ?? Refuse(a[1], visiting);
            }
            if (t.IsClass && !t.IsArray && !typeof(Delegate).IsAssignableFrom(t) && !typeof(IEnumerable).IsAssignableFrom(t) && t.Assembly != typeof(object).Assembly
                && t.Assembly.GetName().Name != "Assembly-CSharp" && !t.Assembly.GetName().Name.StartsWith("UnityEngine", StringComparison.Ordinal))
            {
                if (visiting.ContainsKey(t)) return "recursive type " + t.Name;
                visiting[t] = true;
                for (Type b = t; b != null && b != typeof(object); b = b.BaseType)
                    foreach (FieldInfo f in b.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        string why = Refuse(f.FieldType, visiting);
                        if (why != null) return t.Name + "." + f.Name + ": " + why;
                    }
                visiting.Remove(t);
                return null;
            }
            return "type " + t.FullName + " is not carried";
        }

        static bool Scalar(Type t) => t == typeof(string) || t == typeof(bool) || t.IsEnum || t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
            || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte) || t == typeof(float) || t == typeof(double);

        static bool IsRef(Type t) => actorType.IsAssignableFrom(t) || t == combatantType || (buildingType != null && buildingType.IsAssignableFrom(t)) || teamType.IsAssignableFrom(t);

        // ---- encoding ----
        static string Encoded(object v, Type t) { var sb = new StringBuilder(); Encode(v, t, sb); return sb.ToString(); }

        static void Encode(object v, Type t, StringBuilder sb)
        {
            if (v == null) { sb.Append('n'); return; }
            if (t == typeof(string)) { string s = (string)v; sb.Append('s').Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s); return; }
            if (t == typeof(bool)) { sb.Append((bool)v ? "b1" : "b0"); return; }
            if (t == typeof(float)) { sb.Append('f').Append(((float)v).ToString("R", CultureInfo.InvariantCulture)).Append(';'); return; }
            if (t == typeof(double)) { sb.Append('d').Append(((double)v).ToString("R", CultureInfo.InvariantCulture)).Append(';'); return; }
            if (t.IsEnum || Scalar(t)) { sb.Append('i').Append(t == typeof(ulong) ? ((ulong)v).ToString(CultureInfo.InvariantCulture) : Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)).Append(';'); return; }
            if (t.FullName == "UnityEngine.Vector3") { sb.Append('v'); foreach (string c in new[] { "x", "y", "z" }) sb.Append(((float)t.GetField(c).GetValue(v)).ToString("R", CultureInfo.InvariantCulture)).Append(';'); return; }
            if (t.FullName == "UnityEngine.Color") { sb.Append('c'); foreach (string c in new[] { "r", "g", "b", "a" }) sb.Append(((float)t.GetField(c).GetValue(v)).ToString("R", CultureInfo.InvariantCulture)).Append(';'); return; }
            if (IsRef(t)) { Encode(Get(v, "GUID") as string ?? "", typeof(string), sb); return; }
            if (t.IsGenericType && IsSetOrList(t.GetGenericTypeDefinition()))
            {
                Type e = t.GetGenericArguments()[0];
                var items = new List<object>();
                foreach (object o in (IEnumerable)v) items.Add(o);
                sb.Append('l').Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append(';');
                foreach (object o in items) Encode(o, e, sb);
                return;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                Type[] a = t.GetGenericArguments();
                IDictionary d = (IDictionary)v;
                sb.Append('m').Append(d.Count.ToString(CultureInfo.InvariantCulture)).Append(';');
                foreach (DictionaryEntry e in d) { Encode(e.Key, a[0], sb); Encode(e.Value, a[1], sb); }
                return;
            }
            if (v.GetType() != t) throw new NotSupportedException("value of " + v.GetType().FullName + " in a field declared " + t.FullName);
            FieldInfo[] fs = DataFields(t);
            sb.Append('o').Append(fs.Length.ToString(CultureInfo.InvariantCulture)).Append(';');
            foreach (FieldInfo f in fs) Encode(f.GetValue(v), f.FieldType, sb);
        }

        static FieldInfo[] DataFields(Type t)
        {
            var list = new List<FieldInfo>();
            var chain = new List<Type>();
            for (Type b = t; b != null && b != typeof(object); b = b.BaseType) chain.Insert(0, b);
            // Base first, each type's fields in metadata-token order: the same order on every peer (one DLL).
            foreach (Type b in chain)
            {
                var own = new List<FieldInfo>(b.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                own.Sort((x, y) => x.MetadataToken.CompareTo(y.MetadataToken));
                list.AddRange(own);
            }
            return list.ToArray();
        }

        sealed class Reader
        {
            readonly string s;
            readonly object combat;
            readonly Source src;
            public int Pos;

            public Reader(string text, object combat, Source src) { s = text; this.combat = combat; this.src = src; }

            string Until(char c)
            {
                int end = s.IndexOf(c, Pos);
                if (end < 0) throw new FormatException("missing '" + c + "' at " + Pos);
                string r = s.Substring(Pos, end - Pos);
                Pos = end + 1;
                return r;
            }

            char Tag() { if (Pos >= s.Length) throw new FormatException("truncated at " + Pos); return s[Pos++]; }

            static readonly object Dropped = new object();

            public object Read(Type t, string field)
            {
                object v = ReadOne(t, field);
                return ReferenceEquals(v, Dropped) ? null : v;
            }

            object ReadOne(Type t, string field)
            {
                char tag = Tag();
                if (tag == 'n') return null;
                switch (tag)
                {
                    case 's': { int len = int.Parse(Until(':'), CultureInfo.InvariantCulture); string r = s.Substring(Pos, len); Pos += len; return IsRef(t) ? Ref(t, r, field) : r; }
                    case 'b': return Tag() == '1';
                    case 'f': return float.Parse(Until(';'), NumberStyles.Float, CultureInfo.InvariantCulture);
                    case 'd': return double.Parse(Until(';'), NumberStyles.Float, CultureInfo.InvariantCulture);
                    case 'i':
                        {
                            string n = Until(';');
                            if (t.IsEnum) return Enum.ToObject(t, long.Parse(n, CultureInfo.InvariantCulture));
                            return t == typeof(ulong) ? ulong.Parse(n, CultureInfo.InvariantCulture) : Convert.ChangeType(long.Parse(n, CultureInfo.InvariantCulture), t, CultureInfo.InvariantCulture);
                        }
                    case 'v':
                    case 'c':
                        {
                            string[] names = tag == 'v' ? new[] { "x", "y", "z" } : new[] { "r", "g", "b", "a" };
                            object box = Activator.CreateInstance(t);
                            foreach (string c in names) t.GetField(c).SetValue(box, float.Parse(Until(';'), NumberStyles.Float, CultureInfo.InvariantCulture));
                            return box;
                        }
                    case 'l':
                        {
                            int n = int.Parse(Until(';'), CultureInfo.InvariantCulture);
                            Type e = t.GetGenericArguments()[0];
                            object coll = Activator.CreateInstance(t);
                            MethodInfo add = t.GetMethod("Add");
                            for (int i = 0; i < n; i++) { object o = ReadOne(e, field); if (!ReferenceEquals(o, Dropped)) add.Invoke(coll, new[] { o }); }
                            return coll;
                        }
                    case 'm':
                        {
                            int n = int.Parse(Until(';'), CultureInfo.InvariantCulture);
                            Type[] a = t.GetGenericArguments();
                            IDictionary d = (IDictionary)Activator.CreateInstance(t);
                            for (int i = 0; i < n; i++)
                            {
                                object k = ReadOne(a[0], field), v = ReadOne(a[1], field);
                                if (ReferenceEquals(k, Dropped) || k == null || ReferenceEquals(v, Dropped)) continue;
                                d[k] = v;
                            }
                            return d;
                        }
                    case 'o':
                        {
                            int n = int.Parse(Until(';'), CultureInfo.InvariantCulture);
                            FieldInfo[] fs = DataFields(t);
                            if (fs.Length != n) throw new FormatException(t.Name + " has " + fs.Length + " fields, data " + n);
                            object o = FormatterServices.GetUninitializedObject(t);
                            foreach (FieldInfo f in fs) { object v = ReadOne(f.FieldType, field); f.SetValue(o, ReferenceEquals(v, Dropped) ? null : v); }
                            return o;
                        }
                }
                throw new FormatException("tag '" + tag + "' at " + (Pos - 1));
            }

            object Ref(Type t, string guid, string field)
            {
                if (guid.Length == 0) return Dropped;
                object found = null;
                if (teamType.IsAssignableFrom(t))
                {
                    if (Get(combat, "Teams") is IEnumerable teams)
                        foreach (object team in teams) if (Get(team, "GUID") as string == guid) { found = team; break; }
                }
                else found = findCombatant.Invoke(combat, new object[] { guid, false });
                if (found != null && t.IsInstanceOfType(found)) return found;
                if (++src.Rows <= 200) log(src.Tag + "|unmapped|" + field + "|" + guid);
                return Dropped;
            }
        }

        // ---- rows ----
        static int Count(object v) => v is ICollection c ? c.Count : v is IEnumerable e && !(v is string) ? Enumerate(e) : v == null ? 0 : IsDefault(v) ? 0 : 1;

        static int Enumerate(IEnumerable e) { int n = 0; foreach (object _ in e) n++; return n; }

        static bool IsDefault(object v) => v is bool b ? !b : v is int i ? i == 0 : v is float f ? f == 0f : v is string s ? s.Length == 0 : false;

        static void Summary(StringBuilder sb, string name, object v, string encoded)
        {
            uint h = 2166136261;
            foreach (char c in encoded) { h ^= c; h *= 16777619; }
            if (sb.Length > 0) sb.Append(',');
            sb.Append(name).Append('=').Append(Count(v).ToString(CultureInfo.InvariantCulture)).Append('#').Append(h.ToString("x8", CultureInfo.InvariantCulture));
        }

        static void Row(Source src, string kind, string summary, string at)
        {
            if (++src.Rows > 200) return;
            log(src.Tag + "|" + kind + "|" + summary + "|at=" + at);
        }

        internal static string Inner(Exception e) => (e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e).GetType().Name + ": " + (e is TargetInvocationException t2 && t2.InnerException != null ? t2.InnerException : e).Message;

        internal static object Get(object o, string name)
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
