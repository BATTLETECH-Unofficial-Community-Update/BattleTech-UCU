// Mod state sent to a co-op peer that loads a resync or rejoin snapshot (gh issue 24, snapshot-field sweep;
// .hermes/reports/issue24-sweep-1/snapshot-fields.tsv, Sam 2026-11-22: fix the one-peer and not-carried class before 4B).
// A co-op guest's resync reload destroys its combat; mods clear their static state there (OnCombatGameDestroyed) or key it by actor objects
// the reload replaces, and the save holds none of it. The host never reloads. This engine takes a declaration (CarryDecl: the static fields
// or properties of a mod type, and the instance fields of every actor of a game type) and registers one source with UCU's co-op side
// channel (CoopInvocationSideData, patch 166): the host's full state for a peer that has just loaded (capture(null)) is the declared
// members' values, encoded by their declared types; the guest's apply(null) decodes them against its new combat and replaces its own
// values (collections in place). Nothing is sent with an invocation: every member carried here is written on every peer alike while play
// runs (the declaration states the basis), so only the reload loses it. LoadSteps.cs holds the declarations, in order.
// What it encodes: string, bool, the integer and float types, enums, UnityEngine.Vector3 and Color, an AbstractActor / ICombatant /
// Building reference (by GUID, found with CombatGameState.FindCombatantByGUID), a Team (by GUID), a MechComponent reference (its owner's GUID
// and its uid, found in the owner's allComponents), List<T>, HashSet<T>, Queue<T>, Dictionary<K,V>, and a class whose instance fields are
// all of those (made with FormatterServices.GetUninitializedObject, so no constructor runs). A member of any other type (a delegate, an
// Ability, a Transform, a def object) is refused when the declaration installs and is logged by name; it is not carried. A reference whose
// GUID the guest cannot find is dropped from its collection and logged `<tag>|unmapped|<field>|<guid>`; with CarryDecl.Strict an object
// holding such a reference is dropped whole.
// Rows (same format on both peers, so they diff as text): host `<tag>|capture|<field>=<count>#<fnv>,...|at=full`, guest
// `<tag>|before|...|at=apply` and `<tag>|apply|...|at=apply`; `<tag>|installed|...`, `<tag>|absent|...` (the declaring mod is not loaded),
// `<tag>|row-missing|<member>|<why>` and `<tag>|refused|<member>|<reason>` (a declaration that does not resolve: a defect, not noise),
// `<tag>|FAILED|...`, `<tag>|apply-failed|...`, and at each combat's end (CombatGameState.OnCombatGameDestroyed)
// `<tag>|count|capture=<n>|apply=<n>|nonempty=<n>`. An actor field's row entry counts the actors whose value is not 0/false.
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
    // Static members of one mod type: fields or properties, by name. Optional: a member of one version of the mod; when the type or a name
    // does not resolve the set is skipped with an `absent` row. Without it every name must resolve (a miss is a `row-missing` defect).
    public sealed class CarryStatic
    {
        public string Type;
        public string[] Names;
        public bool Optional;
    }

    // Instance fields (declared on Type) of every actor that is a Type, by actor GUID.
    public sealed class CarryActorFields
    {
        public string Type;
        public string[] Names;
    }

    public sealed class CarryDecl
    {
        public string Tag;            // row prefix and side-channel key; no '|'
        public string Assembly;       // the mod's assembly name; "Assembly-CSharp" for the game's own types
        public string Basis;          // why only a load loses it (logged at install)
        public bool Strict;           // an object holding an unmapped reference is dropped whole
        public CarryStatic[] Statics = new CarryStatic[0];
        public CarryActorFields[] ActorFields = new CarryActorFields[0];
    }

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
        // One carried member: a static field or property (OnType == null), or an instance field of actors that are an OnType.
        sealed class Slot
        {
            public string Label;
            public Type Value, OnType;
            public FieldInfo Field;
            public PropertyInfo Property;
            public object Get(object target) => Field != null ? Field.GetValue(target) : Property.GetValue(target, null);
            public void Set(object target, object value) { if (Field != null) Field.SetValue(target, value); else Property.SetValue(target, value, null); }
        }

        sealed class Source { public string Tag; public bool Strict; public Slot[] Statics, ActorSlots; public CoopCarryCounter Count; public int Rows; }

        static Action<string> log;
        static PropertyInfo battleTechGame;
        static MethodInfo findCombatant, register;
        static Type actorType, combatantType, buildingType, teamType, componentType;
        const BindingFlags St = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // Resolves the declaration and registers its source. Returns the members carried. Called at AtFirstCombat (fault M: no reflection
        // on a mod's types before every mod has run its Init).
        public static int Install(CarryDecl d, Action<string> logLine)
        {
            log = logLine;
            string tag = d.Tag;
            try
            {
                if (!Resolve())
                {
                    logLine(tag + "|FAILED|shape changed CoopInvocationSideData.Register=" + (register != null) + " FindCombatantByGUID=" + (findCombatant != null)
                        + " BattleTechGame=" + (battleTechGame != null) + " (needs UCU patch 166)");
                    return 0;
                }
                bool game = d.Assembly == "Assembly-CSharp";
                Assembly asm = game ? null : FindAssembly(d.Assembly);
                if (asm == null && !game) { logLine(tag + "|absent|assembly " + d.Assembly + " is not loaded: nothing to carry"); return 0; }
                var statics = new List<Slot>();
                var actors = new List<Slot>();
                int missing = 0;
                foreach (CarryStatic s in d.Statics)
                {
                    Type owner = TypeOf(asm, s.Type);
                    if (owner == null)
                    {
                        if (s.Optional) { logLine(tag + "|absent|type " + s.Type + " not in " + d.Assembly + " (optional: another version of the mod)"); continue; }
                        logLine(tag + "|row-missing|" + s.Type + "|type not found in " + d.Assembly);
                        missing += s.Names.Length;
                        continue;
                    }
                    foreach (string name in s.Names)
                    {
                        Slot slot = Member(owner, name, true);
                        if (slot == null && s.Optional) { logLine(tag + "|absent|" + name + " not on " + owner.FullName + " (optional: another version of the mod)"); continue; }
                        if (slot == null) { logLine(tag + "|row-missing|" + name + "|no static field or property " + owner.FullName + "." + name); missing++; continue; }
                        string why = Refuse(slot.Value, new Dictionary<Type, bool>());
                        if (why != null) { logLine(tag + "|refused|" + name + "|" + why); missing++; continue; }
                        statics.Add(slot);
                    }
                }
                foreach (CarryActorFields a in d.ActorFields)
                {
                    Type owner = TypeOf(asm, a.Type);
                    if (owner == null || !actorType.IsAssignableFrom(owner))
                    {
                        logLine(tag + "|row-missing|" + a.Type + "|actor type not found in " + d.Assembly);
                        missing += a.Names.Length;
                        continue;
                    }
                    foreach (string name in a.Names)
                    {
                        Slot slot = Member(owner, name, false);
                        if (slot == null) { logLine(tag + "|row-missing|" + name + "|no instance field " + owner.FullName + "." + name); missing++; continue; }
                        string why = Refuse(slot.Value, new Dictionary<Type, bool>());
                        if (why != null) { logLine(tag + "|refused|" + name + "|" + why); missing++; continue; }
                        actors.Add(slot);
                    }
                }
                if (statics.Count + actors.Count == 0) { logLine(tag + "|FAILED|no member of " + d.Assembly + " can be carried"); return 0; }
                var src = new Source { Tag = tag, Strict = d.Strict, Statics = statics.ToArray(), ActorSlots = actors.ToArray(), Count = CoopCarryCounter.For(tag, logLine) };
                register.Invoke(null, new object[] { tag, new Func<object, string>(inv => Capture(src, inv)), new Action<object, string>((inv, data) => Apply(src, inv, data)) });
                var names = new StringBuilder();
                foreach (Slot s in statics) { if (names.Length > 0) names.Append(','); names.Append(s.Label); }
                foreach (Slot s in actors) { if (names.Length > 0) names.Append(','); names.Append(s.Label); }
                logLine(tag + "|installed|" + d.Assembly + "|" + names + "|unresolved=" + missing + "|sent to a peer that loads a co-op snapshot|" + d.Basis);
                return statics.Count + actors.Count;
            }
            catch (Exception e) { logLine(tag + "|FAILED|" + Inner(e)); return 0; }
        }

        // A mod assembly by simple name, from what is loaded (Assembly-CSharp: the game's).
        internal static Assembly FindAssembly(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n;
                try { n = a.GetName().Name; } catch { continue; }
                if (n == name) return a;
            }
            return null;
        }

        // The game's types resolve as every adapter resolves them (ModAssemblyHook.GameType), a mod's from its own assembly.
        static Type TypeOf(Assembly asm, string name)
        {
            try { return asm == null ? ModAssemblyHook.GameType(name) : asm.GetType(name, false); } catch { return null; }
        }

        static Slot Member(Type owner, string name, bool isStatic)
        {
            if (isStatic)
            {
                FieldInfo f = owner.GetField(name, St | BindingFlags.DeclaredOnly);
                if (f != null) return new Slot { Label = owner.Name + "." + name, Field = f, Value = f.FieldType };
                PropertyInfo p = owner.GetProperty(name, St | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0 && p.GetGetMethod(true) != null) return new Slot { Label = owner.Name + "." + name, Property = p, Value = p.PropertyType };
                return null;
            }
            FieldInfo fi = owner.GetField(name, Inst);
            return fi == null ? null : new Slot { Label = owner.Name + "." + name, Field = fi, Value = fi.FieldType, OnType = owner };
        }

        static bool Resolve()
        {
            if (register != null) return true;
            actorType = ModAssemblyHook.GameType("BattleTech.AbstractActor");
            combatantType = ModAssemblyHook.GameType("BattleTech.ICombatant");
            buildingType = ModAssemblyHook.GameType("BattleTech.Building");
            teamType = ModAssemblyHook.GameType("BattleTech.Team");
            componentType = ModAssemblyHook.GameType("BattleTech.MechComponent");
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", St);
            findCombatant = ModAssemblyHook.GameType("BattleTech.CombatGameState")?.GetMethod("FindCombatantByGUID", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(bool) }, null);
            MethodInfo r = ModAssemblyHook.GameType("BattleTech.CoopInvocationSideData")?.GetMethod("Register", St);
            if (r == null || findCombatant == null || battleTechGame == null || actorType == null || combatantType == null || teamType == null || componentType == null) return false;
            register = r;
            return true;
        }

        // A source that is not a declaration (the census): registered with the same side channel, in call order.
        internal static bool RegisterSource(string key, Func<object, string> capture, Action<object, string> apply, Action<string> logLine)
        {
            log = logLine;
            if (!Resolve()) { logLine(key + "|FAILED|shape changed CoopInvocationSideData.Register=" + (register != null) + " (needs UCU patch 166)"); return false; }
            register.Invoke(null, new object[] { key, capture, apply });
            return true;
        }

        internal static object Combat() => Get(battleTechGame.GetValue(null, null), "Combat");

        // The actors of a combat that have a GUID, in ordinal GUID order (the same on every peer).
        static List<KeyValuePair<string, object>> Actors(object combat)
        {
            var list = new List<KeyValuePair<string, object>>();
            if (Get(combat, "AllActors") is IEnumerable all)
                foreach (object a in all)
                    if (a != null && Get(a, "GUID") is string g && g.Length > 0) list.Add(new KeyValuePair<string, object>(g, a));
            list.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            return list;
        }

        static string Capture(Source src, object invocation)
        {
            if (invocation != null) return null;
            try
            {
                object combat = Combat();
                if (combat == null) return null;
                var sb = new StringBuilder();
                var summary = new StringBuilder();
                bool any = Encode(src, combat, sb, summary);
                src.Count.Capture++;
                if (any) src.Count.NonEmpty++;
                Row(src, "capture", summary.ToString(), "full");
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
            }
            catch (Exception e) { log(src.Tag + "|FAILED|capture|" + Inner(e)); return null; }
        }

        // The wire text of the source's live values and its row summary; true when any carried value is not empty.
        static bool Encode(Source src, object combat, StringBuilder sb, StringBuilder summary)
        {
            bool any = false;
            foreach (Slot f in src.Statics)
            {
                object v = f.Get(null);
                int start = sb.Length;
                Encode(v, f.Value, sb);
                if (Count(v) > 0) any = true;
                Summary(summary, f.Label, Count(v), sb.ToString(start, sb.Length - start));
            }
            if (src.ActorSlots.Length == 0) return any;
            List<KeyValuePair<string, object>> actors = Actors(combat);
            sb.Append('a').Append(actors.Count.ToString(CultureInfo.InvariantCulture)).Append(';');
            var slices = new StringBuilder[src.ActorSlots.Length];
            var counts = new int[src.ActorSlots.Length];
            for (int i = 0; i < slices.Length; i++) slices[i] = new StringBuilder();
            foreach (KeyValuePair<string, object> a in actors)
            {
                Encode(a.Key, typeof(string), sb);
                for (int i = 0; i < src.ActorSlots.Length; i++)
                {
                    Slot s = src.ActorSlots[i];
                    if (!s.OnType.IsInstanceOfType(a.Value)) { sb.Append('n'); continue; }
                    object v = s.Get(a.Value);
                    int start = sb.Length;
                    Encode(v, s.Value, sb);
                    if (!IsDefault(v)) { counts[i]++; any = true; }
                    slices[i].Append(a.Key).Append(sb.ToString(start, sb.Length - start));
                }
            }
            for (int i = 0; i < slices.Length; i++) Summary(summary, src.ActorSlots[i].Label, counts[i], slices[i].ToString());
            return any;
        }

        static string Summarize(Source src, object combat)
        {
            var summary = new StringBuilder();
            Encode(src, combat, new StringBuilder(), summary);
            return summary.ToString();
        }

        static void Apply(Source src, object invocation, string data)
        {
            if (invocation != null || string.IsNullOrEmpty(data)) return;
            try
            {
                object combat = Combat();
                if (combat == null) return;
                string text = Encoding.UTF8.GetString(Convert.FromBase64String(data));
                Row(src, "before", Summarize(src, combat), "apply");
                var r = new Reader(text, combat, src);
                var decoded = new object[src.Statics.Length];
                for (int i = 0; i < src.Statics.Length; i++) decoded[i] = r.Read(src.Statics[i].Value, src.Statics[i].Label);
                var perActor = new List<KeyValuePair<string, object[]>>();
                if (src.ActorSlots.Length > 0)
                {
                    if (r.Tag() != 'a') throw new FormatException("actor section expected at " + (r.Pos - 1));
                    int n = int.Parse(r.Until(';'), CultureInfo.InvariantCulture);
                    for (int i = 0; i < n; i++)
                    {
                        string guid = (string)r.Read(typeof(string), "actors");
                        var values = new object[src.ActorSlots.Length];
                        for (int k = 0; k < values.Length; k++) values[k] = r.Read(src.ActorSlots[k].Value, src.ActorSlots[k].Label);
                        perActor.Add(new KeyValuePair<string, object[]>(guid, values));
                    }
                }
                if (r.Pos != text.Length) throw new FormatException("trailing data at " + r.Pos + " of " + text.Length);
                // Decoded in full before anything is written, so a malformed payload changes nothing.
                for (int i = 0; i < src.Statics.Length; i++) Store(src.Statics[i], decoded[i]);
                foreach (KeyValuePair<string, object[]> e in perActor)
                {
                    object actor = findCombatant.Invoke(combat, new object[] { e.Key, false });
                    if (actor == null || !actorType.IsInstanceOfType(actor)) { if (++src.Rows <= 200) log(src.Tag + "|unmapped|actors|" + e.Key); continue; }
                    for (int k = 0; k < src.ActorSlots.Length; k++)
                        if (src.ActorSlots[k].OnType.IsInstanceOfType(actor)) src.ActorSlots[k].Set(actor, e.Value[k]);
                }
                src.Count.Apply++;
                string after = Summarize(src, combat);
                bool any = false;
                foreach (Slot f in src.Statics) if (Count(f.Get(null)) > 0) any = true;
                if (!any) foreach (KeyValuePair<string, object> a in Actors(combat)) { foreach (Slot s in src.ActorSlots) if (s.OnType.IsInstanceOfType(a.Value) && !IsDefault(s.Get(a.Value))) any = true; if (any) break; }
                if (any) src.Count.NonEmpty++;
                Row(src, "apply", after, "apply");
            }
            catch (Exception e) { log(src.Tag + "|apply-failed|" + Inner(e)); }
        }

        // A collection already in the field is refilled in place (a mod may hold the same instance elsewhere); anything else is set.
        static void Store(Slot f, object value)
        {
            object cur = f.Get(null);
            if (cur != null && value != null && cur.GetType() == value.GetType())
            {
                if (cur is IDictionary cd && value is IDictionary vd) { cd.Clear(); foreach (DictionaryEntry e in vd) cd[e.Key] = e.Value; return; }
                if (cur is IList cl && value is IList vl) { cl.Clear(); foreach (object o in vl) cl.Add(o); return; }
                MethodInfo clear = cur.GetType().GetMethod("Clear", Type.EmptyTypes);
                MethodInfo add = Adder(cur.GetType());
                if (clear != null && add != null && add.GetParameters().Length == 1 && value is IEnumerable ve)
                {
                    clear.Invoke(cur, null);
                    foreach (object o in ve) add.Invoke(cur, new[] { o });
                    return;
                }
            }
            f.Set(null, value);
        }

        static MethodInfo Adder(Type collection) => collection.GetMethod("Add") ?? collection.GetMethod("Enqueue");

        // ---- type check ----
        // HashSet<T> and Queue<T> are matched by name: this assembly references mscorlib only, and they live in System.Core and System.
        static bool IsSetOrList(Type g) => g == typeof(List<>) || g.FullName == "System.Collections.Generic.HashSet`1" || g.FullName == "System.Collections.Generic.Queue`1";

        // True for a type this engine can carry. The census (ModStateCoopCensus) asks the same question, so what it hashes is what a
        // declaration could carry.
        internal static bool Encodable(Type t) => Resolve() && Refuse(t, new Dictionary<Type, bool>()) == null;

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

        static bool IsRef(Type t) => actorType.IsAssignableFrom(t) || t == combatantType || (buildingType != null && buildingType.IsAssignableFrom(t)) || teamType.IsAssignableFrom(t) || IsComponent(t);

        static bool IsComponent(Type t) => componentType.IsAssignableFrom(t);

        // ---- encoding ----
        internal static string Encoded(object v, Type t) { var sb = new StringBuilder(); Encode(v, t, sb); return sb.ToString(); }

        const int MaxEncoded = 16000000;

        static void Encode(object v, Type t, StringBuilder sb)
        {
            if (sb.Length > MaxEncoded) throw new InvalidOperationException("encoding over " + MaxEncoded + " characters");
            if (v == null) { sb.Append('n'); return; }
            if (t == typeof(string)) { string s = (string)v; sb.Append('s').Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s); return; }
            if (t == typeof(bool)) { sb.Append((bool)v ? "b1" : "b0"); return; }
            if (t == typeof(float)) { sb.Append('f').Append(((float)v).ToString("R", CultureInfo.InvariantCulture)).Append(';'); return; }
            if (t == typeof(double)) { sb.Append('d').Append(((double)v).ToString("R", CultureInfo.InvariantCulture)).Append(';'); return; }
            if (t.IsEnum || Scalar(t)) { sb.Append('i').Append(t == typeof(ulong) ? ((ulong)v).ToString(CultureInfo.InvariantCulture) : Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)).Append(';'); return; }
            if (t.FullName == "UnityEngine.Vector3") { sb.Append('v'); foreach (string c in new[] { "x", "y", "z" }) sb.Append(((float)t.GetField(c).GetValue(v)).ToString("R", CultureInfo.InvariantCulture)).Append(';'); return; }
            if (t.FullName == "UnityEngine.Color") { sb.Append('c'); foreach (string c in new[] { "r", "g", "b", "a" }) sb.Append(((float)t.GetField(c).GetValue(v)).ToString("R", CultureInfo.InvariantCulture)).Append(';'); return; }
            if (IsComponent(t)) { sb.Append('k'); Encode(Get(Get(v, "parent"), "GUID") as string ?? "", typeof(string), sb); Encode(Get(v, "uid") as string ?? "", typeof(string), sb); return; }
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

            public string Until(char c)
            {
                int end = s.IndexOf(c, Pos);
                if (end < 0) throw new FormatException("missing '" + c + "' at " + Pos);
                string r = s.Substring(Pos, end - Pos);
                Pos = end + 1;
                return r;
            }

            public char Tag() { if (Pos >= s.Length) throw new FormatException("truncated at " + Pos); return s[Pos++]; }

            static readonly object Dropped = new object();

            public object Read(Type t, string field)
            {
                object v = ReadOne(t, field);
                return ReferenceEquals(v, Dropped) ? null : v;
            }

            string Str()
            {
                if (Tag() != 's') throw new FormatException("string expected at " + (Pos - 1));
                int len = int.Parse(Until(':'), CultureInfo.InvariantCulture);
                string r = s.Substring(Pos, len);
                Pos += len;
                return r;
            }

            object ReadOne(Type t, string field)
            {
                char tag = Tag();
                if (tag == 'n') return null;
                switch (tag)
                {
                    case 's': { int len = int.Parse(Until(':'), CultureInfo.InvariantCulture); string r = s.Substring(Pos, len); Pos += len; return IsRef(t) ? Ref(t, r, field) : r; }
                    case 'k': return Component(t, Str(), Str(), field);
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
                            MethodInfo add = Adder(t);
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
                            bool dropped = false;
                            foreach (FieldInfo f in fs)
                            {
                                object v = ReadOne(f.FieldType, field);
                                if (ReferenceEquals(v, Dropped)) { dropped = true; v = null; }
                                f.SetValue(o, v);
                            }
                            return dropped && src.Strict ? Dropped : o;
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

            // A component of an actor: its owner's GUID and its uid.
            object Component(Type t, string owner, string uid, string field)
            {
                object actor = owner.Length == 0 ? null : findCombatant.Invoke(combat, new object[] { owner, false });
                if (actor != null && Get(actor, "allComponents") is IEnumerable all)
                    foreach (object c in all)
                        if (c != null && t.IsInstanceOfType(c) && Get(c, "uid") as string == uid) return c;
                if (++src.Rows <= 200) log(src.Tag + "|unmapped|" + field + "|" + owner + "/" + uid);
                return Dropped;
            }
        }

        // ---- rows ----
        static int Count(object v) => v is ICollection c ? c.Count : v is IEnumerable e && !(v is string) ? Enumerate(e) : v == null ? 0 : IsDefault(v) ? 0 : 1;

        static int Enumerate(IEnumerable e) { int n = 0; foreach (object _ in e) n++; return n; }

        // The census and the rows count a scalar at its default (0, false, "") or a null as empty.
        internal static int Filled(object v) => Count(v);

        static bool IsDefault(object v) => v == null || (v is bool b ? !b : v is int i ? i == 0 : v is float f ? f == 0f : v is string s ? s.Length == 0 : v is long l ? l == 0 : v is double d ? d == 0 : v is Enum ? Convert.ToInt64(v, CultureInfo.InvariantCulture) == 0 : false);

        static void Summary(StringBuilder sb, string name, int count, string encoded)
        {
            uint h = 2166136261;
            foreach (char c in encoded) { h ^= c; h *= 16777619; }
            if (sb.Length > 0) sb.Append(',');
            sb.Append(name).Append('=').Append(count.ToString(CultureInfo.InvariantCulture)).Append('#').Append(h.ToString("x8", CultureInfo.InvariantCulture));
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
