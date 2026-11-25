// BEX:T (BattleTech Extended: Tactics) adapter for combat saves (mod support).
// BEX:T's Extended_CE postfixes SimGameOptionsMenu.CanSave and SetSaveTooltip: while a mission runs, CanSave returns
// false and the save button's tooltip reads "Saving during combat missions disabled", unless its own setting
// CombatSavesDontWorkEnableAnyway is true. So the launcher's "in-mission saves" box, which lifts IRTweaks'
// DisableCombatSaves (IRTweaksSaveCompat), left BEX:T's save button disabled. With the opt-in marker this sets that
// setting true in memory, as IRTweaksSaveCompat does for IRTweaks; BEX:T's files are not touched.
// Extended_CE reads the setting on every call, from Extended_CE.Core.Settings, which its Init fills from mod.json. The
// switch is made at the first CombatGameState._Init, after every mod's Init has run.
// The save-button switch is made only with the marker. Parts 2 and 3 act on any combat save or load while
// CombatSavesRuntimeGate.SaveDataEnabled() / IsLoadingFromSave, a co-op resync or rejoin snapshot included, so they are installed with the
// marker absent too (ModBootstrap.RegisterSaveAdapters(false) at the first co-op combat): without them a BEX:T co-op guest's reload doubled
// Extended_CE's components and reset its tabletop state, and every compare after the reload differed (logic-bext-fixes finding S).
// Applied when an assembly named Extended_CE loads.
// Part 2, Extended_CE's tabletop state in a combat save (.hermes/reports/logic-bext-modlist-20261122/findings.md G3). Extended_CE keeps,
// in static memory only, each unit's TTRuleInfo (BTComponents.MechTTRuleInfo.MechTTStatStore, keyed by AbstractActor.uid: engine, hip,
// gyro and life-support crits, AMS and MASC use, external heat, move malus), the ambushers that have not fired yet
// (TacticalGameChanges.AmbushersYetToFire, uids) and the pilots whose next head hit is ignored (FewerHeadInjuries.IgnoreNextHeadHit).
// A load reruns InitStats (Mech.cs:1056), whose Extended_CE postfixes make a zeroed TTRuleInfo for a uid they don't hold and put every
// role_ambusher unit back on the list, so in a fresh process every crit is gone and every ambusher gets its bonus again. Extended_CE's
// UnitRoleStore is not carried: the same postfix rebuilds it from the unit's tags. The uid is saved by the game (AbstractActor.Save) and
// restored by the load (Mech.Init -> InitAbstractActor(base.uid, ...)).
// A combat save writes each unit through the contract Serializer (SerializableReferenceContainer+SerializedReference.Serialize;
// AbstractActor.encounterTags and TagSet.items are contract members), not through AbstractActor.Save(SerializationStream). While that
// Serialize runs for a unit (gated on CombatSavesRuntimeGate.SaveDataEnabled), the unit's encounterTags hold one more tag,
// "ucu.extendedce.v1|tt=<field:value,...|->|amb=<0|1>|head=<0|1>", which a finalizer takes off again (TagSet.Add appends, Remove of
// the last item keeps the order). An AbstractActor.Hydrate postfix takes that tag off the loaded unit and keeps it by uid; a
// Priority.Last postfix on Mech, Vehicle and Turret InitStats, after Extended_CE's own, writes it into Extended_CE's fields. No
// statistic is added (a statistic would move StatCollection.uidTracker). Rows: `extended-ce-save|store-save|<uid>|<tag>`,
// `|store-load|<uid>|<tag>` and `|store-apply|<uid>|before=<state>|after=<state>`, up to 80 each.
// Part 3, duplicates on load (findings.md G4). Extended_CE's Mech.InitStats prefix adds its runtime components (Gear_BEX_Engine, Gyro,
// actuators, ...; Vehicle.InitStats: Gear_BEX_MotiveSystem) to miscComponents and allComponents, and its Mech postfix creates the
// no-hand / no-lower-arm effects. A load restores both from the save (Mech/Vehicle.Hydrate; EffectManager.Hydrate via TurnDirector.Hydrate)
// before Init reruns InitStats (EncounterLayerParent.InitFromSavePassTwo), so every one doubles. While IsLoadingFromSave, a Priority.First
// prefix notes the unit's components and effects; a Priority.Last prefix, after Extended_CE's and MechQuirks', takes out each Gear_BEX_ (or Gear_Quirk_) component added in
// this call when the save already held components with that prefix (a save made without the mod keeps its additions); a Priority.Last postfix
// cancels each of those four effects created in this call when the save already held one with the same id. Row:
// `extended-ce-save|load-dedupe|<uid>|components=<n removed>|effects=<n cancelled>`, up to 80.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BattleTechPort
{
    public static class ExtendedCESaveCompat
    {
        const string AssemblyName = "Extended_CE";
        static Action<string> log;
        static bool applied, armed, saveButton;
        static Assembly extendedCe;

        // marker: the in-mission saves marker is present, so BEX:T's save button is switched on as well.
        public static void Register(Action<string> logLine, bool marker)
        {
            log = logLine;
            saveButton = marker;
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies()) Consider(loaded);
        }

        static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args) => Consider(args.LoadedAssembly);

        static void Consider(Assembly assembly)
        {
            if (applied || assembly == null) return;
            string name;
            try { name = assembly.GetName().Name; } catch { return; }
            if (name != AssemblyName) return;
            applied = true;
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            try { Apply(assembly); }
            catch (Exception ex) { log("extended-ce-save|FAILED|" + ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Apply(Assembly assembly)
        {
            extendedCe = assembly;
            Type combat = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { combat = a.GetType("BattleTech.CombatGameState", false); } catch { continue; }
                if (combat != null) break;
            }
            MethodInfo init = combat == null ? null : AccessTools.Method(combat, "_Init");
            if (!saveButton) log("extended-ce-save|armed|" + assembly.GetName().Version + "|no marker: unit-state carry and load dedupe only");
            else if (init == null) log("extended-ce-save|skipped|shape changed CombatGameState._Init=false; Extended_CE's combat-save block stays on");
            else
            {
                new Harmony("battletech.port.extended-ce-save").Patch(init,
                    postfix: new HarmonyMethod(typeof(ExtendedCESaveCompat).GetMethod(nameof(ArmPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                log("extended-ce-save|armed|" + assembly.GetName().Version + "|switches Extended_CE's combat-save block off at the first combat");
            }
            try { InstallStore(assembly); }
            catch (Exception ex) { log("extended-ce-save|store|FAILED|" + ex); }
            try { InstallDedupe(); }
            catch (Exception ex) { log("extended-ce-save|dedupe|FAILED|" + ex); }
        }

        // Extended_CE's effect ids (BTComponents.Mech_InitStats.Postfix).
        static readonly string[] OwnEffects = { "leftHandEffect", "rightHandEffect", "leftarmLowerDmgEffect", "rightarmLowerDmgEffect" };
        // Runtime components added at every InitStats: Extended_CE's, and MechQuirks' (BT_Extended_Quirks, Quirks.Quirks.MechEffects.Mech_InitStats prefix:
        // Gear_Quirk_* chassis-quirk gear; logic-bext-fixes BEXT-ALL-1 status compares 101/201, 16d4b3f5 and a2920539 held 1 and 3 more after each guest reload).
        // shortcut: MechQuirks is covered only where Extended_CE is loaded (BEX:T ships both); a MechQuirks pack without Extended_CE needs its own gate.
        static readonly string[] OwnComponentPrefixes = { "Gear_BEX_", "Gear_Quirk_" };
        static PropertyInfo combatProp, loadingProp, effectsProp, defIdProp;
        static FieldInfo allComps, effectId;
        static MethodInfo targeting, cancel;
        static readonly Dictionary<object, Noted> noted = new Dictionary<object, Noted>();

        sealed class Noted
        {
            public Dictionary<object, bool> comps, effects;
            public Dictionary<string, bool> savedPrefixes = new Dictionary<string, bool>();
            public int removed;
        }

        static void InstallDedupe()
        {
            Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor"), comp = ModAssemblyHook.GameType("BattleTech.MechComponent");
            Type combat = ModAssemblyHook.GameType("BattleTech.CombatGameState"), effect = ModAssemblyHook.GameType("BattleTech.Effect");
            combatProp = actor?.GetProperty("Combat", Inst);
            loadingProp = combat?.GetProperty("IsLoadingFromSave", Inst);
            effectsProp = combat?.GetProperty("EffectManager", Inst);
            allComps = actor?.GetField("allComponents", Inst);
            defIdProp = comp?.GetProperty("defId", Inst);
            effectId = effect?.GetField("id", Inst);
            targeting = effectsProp?.PropertyType.GetMethod("GetAllEffectsTargeting", new[] { typeof(object) });
            cancel = effect == null ? null : effectsProp?.PropertyType.GetMethod("CancelEffect", new[] { effect, typeof(bool) });
            var h = new Harmony("battletech.port.extended-ce-save");
            int units = 0;
            foreach (string unit in new[] { "BattleTech.Mech", "BattleTech.Vehicle" })
            {
                Type t = ModAssemblyHook.GameType(unit);
                MethodInfo init = Declared(t, "InitStats", 0);
                if (init == null || t.GetField("miscComponents", Inst) == null || combatProp == null || loadingProp == null || effectsProp == null || allComps == null
                    || defIdProp == null || effectId == null || targeting == null || cancel == null)
                {
                    log("extended-ce-save|dedupe|skipped|" + unit + " shape changed InitStats=" + (init != null) + " IsLoadingFromSave=" + (loadingProp != null)
                        + " GetAllEffectsTargeting/CancelEffect=" + (targeting != null) + "/" + (cancel != null) + "; Extended_CE's components double on load");
                    continue;
                }
                h.Patch(init, prefix: Mine(nameof(NotePrefix), Priority.First), postfix: Mine(nameof(EffectsPostfix), Priority.Last));
                h.Patch(init, prefix: Mine(nameof(ComponentsPrefix), Priority.Last));
                units++;
            }
            log("extended-ce-save|dedupe|patched|" + units + " unit types; Extended_CE's components and effects are not added again on a combat load");
        }

        static bool Loading(object actor) => combatProp.GetValue(actor, null) is object c && (bool)loadingProp.GetValue(c, null);

        static string DefId(object component)
        {
            try { return defIdProp.GetValue(component, null) as string; } catch { return null; }
        }

        static IEnumerable<object> Effects(object actor)
            => combatProp.GetValue(actor, null) is object c && effectsProp.GetValue(c, null) is object em && targeting.Invoke(em, new[] { actor }) is IEnumerable list
                ? Cast(list) : new object[0];

        static IEnumerable<object> Cast(IEnumerable list) { foreach (object o in list) yield return o; }

        static string PrefixOf(object component)
        {
            string id = DefId(component) ?? "";
            foreach (string p in OwnComponentPrefixes) if (id.StartsWith(p, StringComparison.Ordinal)) return p;
            return null;
        }

        static bool OwnEffect(object e) => Array.IndexOf(OwnEffects, effectId.GetValue(e) as string) >= 0;

        static void NotePrefix(object __instance)
        {
            noted.Remove(__instance);
            try
            {
                if (!Loading(__instance) || !(allComps.GetValue(__instance) is IList comps)) return;
                var n = new Noted { comps = new Dictionary<object, bool>(), effects = new Dictionary<object, bool>() };
                foreach (object c in comps) { n.comps[c] = true; if (PrefixOf(c) is string p) n.savedPrefixes[p] = true; }
                foreach (object e in Effects(__instance)) n.effects[e] = true;
                noted[__instance] = n;
            }
            catch (Exception ex) { Row("dedupe-failed", ex.Message); }
        }

        static void ComponentsPrefix(object __instance)
        {
            if (!noted.TryGetValue(__instance, out Noted n) || n.savedPrefixes.Count == 0) return;
            try
            {
                Predicate<object> added = c => !n.comps.ContainsKey(c) && PrefixOf(c) is string p && n.savedPrefixes.ContainsKey(p);
                foreach (IList list in new[] { allComps.GetValue(__instance) as IList, __instance.GetType().GetField("miscComponents", Inst)?.GetValue(__instance) as IList })
                {
                    if (list == null) continue;
                    for (int i = list.Count - 1; i >= 0; i--) if (added(list[i])) { list.RemoveAt(i); if (list == allComps.GetValue(__instance)) n.removed++; }
                }
            }
            catch (Exception ex) { Row("dedupe-failed", ex.Message); }
        }

        static void EffectsPostfix(object __instance)
        {
            if (!noted.TryGetValue(__instance, out Noted n)) return;
            noted.Remove(__instance);
            try
            {
                var savedIds = new Dictionary<string, bool>();
                foreach (object e in n.effects.Keys) if (OwnEffect(e)) savedIds[(string)effectId.GetValue(e)] = true;
                int cancelled = 0;
                object em = effectsProp.GetValue(combatProp.GetValue(__instance, null), null);
                foreach (object e in new List<object>(Effects(__instance)))
                    if (!n.effects.ContainsKey(e) && OwnEffect(e) && savedIds.ContainsKey((string)effectId.GetValue(e))) { cancel.Invoke(em, new[] { e, (object)true }); cancelled++; }
                if (n.removed > 0 || cancelled > 0) Row("load-dedupe", uidProp?.GetValue(__instance, null) + "|components=" + n.removed + "|effects=" + cancelled);
            }
            catch (Exception ex) { Row("dedupe-failed", ex.Message); }
        }

        const string StoreTag = "ucu.extendedce.v1|";
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static FieldInfo ttStore, ambushers, headHits, encounterTags, sourceObject;
        static Type actorType;
        static FieldInfo[] ttFields;
        static Type ttType;
        static PropertyInfo uidProp;
        static MethodInfo getPilot, tagAdd, tagRemove, headContains, headAdd, headRemove;
        static readonly Dictionary<string, string> loaded = new Dictionary<string, string>();
        static readonly Dictionary<string, int> rowCounts = new Dictionary<string, int>();

        static void InstallStore(Assembly a)
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            ttStore = a.GetType("Extended_CE.BTComponents+MechTTRuleInfo", false)?.GetField("MechTTStatStore", st);
            ttType = a.GetType("Extended_CE.BTComponents+TTRuleInfo", false);
            ambushers = a.GetType("Extended_CE.Functionality.TacticalGameChanges", false)?.GetField("AmbushersYetToFire", st);
            headHits = a.GetType("Extended_CE.Functionality.FewerHeadInjuries", false)?.GetField("IgnoreNextHeadHit", st);
            Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor"), pilot = ModAssemblyHook.GameType("BattleTech.Pilot");
            encounterTags = actor?.GetField("encounterTags", Inst);
            uidProp = actor?.GetProperty("uid", Inst);
            getPilot = actor?.GetMethod("GetPilot", Inst, null, Type.EmptyTypes, null);
            tagAdd = encounterTags?.FieldType.GetMethod("Add", new[] { typeof(string) });
            tagRemove = encounterTags?.FieldType.GetMethod("Remove", new[] { typeof(string) });
            if (headHits != null && pilot != null)
            {
                headContains = headHits.FieldType.GetMethod("Contains", new[] { pilot });
                headAdd = headHits.FieldType.GetMethod("Add", new[] { pilot });
                headRemove = headHits.FieldType.GetMethod("Remove", new[] { pilot });
            }
            Type reference = ModAssemblyHook.GameType("BattleTech.Save.Test.SerializableReferenceContainer")?.GetNestedType("SerializedReference", BindingFlags.NonPublic | BindingFlags.Public);
            sourceObject = reference?.GetField("sourceObject", Inst);
            actorType = actor;
            MethodInfo save = Declared(reference, "Serialize", 0), load = Declared(actor, "Hydrate", 2);
            if (ttStore == null || ttType == null || !typeof(IDictionary).IsAssignableFrom(ttStore.FieldType) || ambushers == null || !typeof(IList).IsAssignableFrom(ambushers.FieldType)
                || headContains == null || headAdd == null || headRemove == null || encounterTags == null || uidProp == null || getPilot == null || tagAdd == null || tagRemove == null
                || sourceObject == null || save == null || load == null)
            {
                log("extended-ce-save|store|skipped|shape changed MechTTStatStore=" + (ttStore != null) + " TTRuleInfo=" + (ttType != null) + " AmbushersYetToFire=" + (ambushers != null)
                    + " IgnoreNextHeadHit=" + (headAdd != null) + " encounterTags=" + (tagAdd != null && tagRemove != null) + " SerializedReference.Serialize/AbstractActor.Hydrate=" + (save != null) + "/" + (load != null)
                    + "; Extended_CE's crits, ambushers and head-hit flags are not in a combat save");
                return;
            }
            var list = new List<FieldInfo>();
            foreach (FieldInfo f in ttType.GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (f.FieldType == typeof(bool) || f.FieldType == typeof(int) || f.FieldType == typeof(float)) list.Add(f);
            ttFields = list.ToArray();
            var h = new Harmony("battletech.port.extended-ce-save");
            h.Patch(save, prefix: Mine(nameof(StoreSavePrefix), Priority.First), finalizer: Mine(nameof(StoreFinalizer), Priority.Last));
            h.Patch(load, postfix: Mine(nameof(LoadPostfix), Priority.Last));
            int inits = 0;
            foreach (string unit in new[] { "BattleTech.Mech", "BattleTech.Vehicle", "BattleTech.Turret" })
            {
                MethodInfo init = Declared(ModAssemblyHook.GameType(unit), "InitStats", 0);
                if (init == null) { log("extended-ce-save|store|" + unit + ".InitStats not found: not restored for that unit type"); continue; }
                h.Patch(init, postfix: Mine(nameof(InitStatsPostfix), Priority.Last));
                inits++;
            }
            log("extended-ce-save|store|patched|" + ttFields.Length + " TTRuleInfo fields, ambushers, head-hit flags carried in each unit's save; restored after InitStats on " + inits + " unit types");
        }

        static MethodInfo Declared(Type t, string name, int parameters)
        {
            if (t == null) return null;
            foreach (MethodInfo m in t.GetMethods(Inst | BindingFlags.DeclaredOnly)) if (m.Name == name && m.GetParameters().Length == parameters) return m;
            return null;
        }

        static HarmonyMethod Mine(string name, int priority)
            => new HarmonyMethod(typeof(ExtendedCESaveCompat).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };

        // The unit's state: "tt=<field:value,...|->|amb=<0|1>|head=<0|1>".
        static string State(object actor)
        {
            string uid = uidProp.GetValue(actor, null) as string;
            var sb = new StringBuilder("tt=");
            object info = uid != null && ttStore.GetValue(null) is IDictionary store && store.Contains(uid) ? store[uid] : null;
            if (info == null) sb.Append('-');
            else
                for (int i = 0; i < ttFields.Length; i++)
                {
                    object v = ttFields[i].GetValue(info);
                    if (i > 0) sb.Append(',');
                    sb.Append(ttFields[i].Name).Append(':').Append(v is float fl ? fl.ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture));
                }
            sb.Append("|amb=").Append(uid != null && ambushers.GetValue(null) is IList l && l.Contains(uid) ? '1' : '0');
            object pilot = getPilot.Invoke(actor, null), set = headHits.GetValue(null);
            sb.Append("|head=").Append(pilot != null && set != null && (bool)headContains.Invoke(set, new[] { pilot }) ? '1' : '0');
            return sb.ToString();
        }

        // SerializedReference.Serialize: __state is the unit and the tag added to it, or null.
        static void StoreSavePrefix(object __instance, out object[] __state)
        {
            __state = null;
            object unit = sourceObject.GetValue(__instance);
            if (unit == null || !actorType.IsInstanceOfType(unit)) return;
            string tag = AddTag(unit, true);
            if (tag != null) __state = new[] { unit, tag };
        }

        static string AddTag(object actor, bool row)
        {
            if (!CombatSavesRuntimeGate.SaveDataEnabled()) return null;
            try
            {
                object tags = encounterTags.GetValue(actor);
                string tag = StoreTag + State(actor);
                if (tags == null || !(bool)tagAdd.Invoke(tags, new object[] { tag })) return null;
                if (row) Row("store-save", uidProp.GetValue(actor, null) + "|" + tag);
                return tag;
            }
            catch (Exception ex) { Row("store-save-failed", ex.Message); return null; }
        }

        static void StoreFinalizer(object[] __state)
        {
            if (__state == null) return;
            try { tagRemove.Invoke(encounterTags.GetValue(__state[0]), new[] { __state[1] }); }
            catch (Exception ex) { Row("store-remove-failed", ex.Message); }
        }

        // The loaded unit: its tag comes off at once and is kept by uid until the unit's InitStats.
        static void LoadPostfix(object __instance)
        {
            try
            {
                if (!(encounterTags.GetValue(__instance) is IEnumerable<string> tags)) return;
                string found = null;
                foreach (string t in tags) if (t != null && t.StartsWith(StoreTag, StringComparison.Ordinal)) { found = t; break; }
                if (found == null) return;
                tagRemove.Invoke(encounterTags.GetValue(__instance), new object[] { found });
                string uid = uidProp.GetValue(__instance, null) as string;
                if (uid == null) return;
                loaded[uid] = found.Substring(StoreTag.Length);
                Row("store-load", uid + "|" + found);
            }
            catch (Exception ex) { Row("store-load-failed", ex.Message); }
        }

        static void InitStatsPostfix(object __instance)
        {
            if (loaded.Count == 0) return;
            try
            {
                string uid = uidProp.GetValue(__instance, null) as string;
                if (uid == null || !loaded.TryGetValue(uid, out string saved)) return;
                loaded.Remove(uid);
                string before = State(__instance);
                var part = new Dictionary<string, string>();
                foreach (string p in saved.Split('|')) { int eq = p.IndexOf('='); if (eq > 0) part[p.Substring(0, eq)] = p.Substring(eq + 1); }
                if (part.TryGetValue("tt", out string tt) && tt != "-" && ttStore.GetValue(null) is IDictionary store)
                {
                    if (!store.Contains(uid)) store[uid] = Activator.CreateInstance(ttType);
                    object info = store[uid];
                    foreach (string kv in tt.Split(','))
                    {
                        int c = kv.IndexOf(':');
                        FieldInfo f = c > 0 ? Array.Find(ttFields, x => x.Name == kv.Substring(0, c)) : null;
                        if (f == null) continue;
                        string v = kv.Substring(c + 1);
                        f.SetValue(info, f.FieldType == typeof(bool) ? (object)bool.Parse(v) : f.FieldType == typeof(int)
                            ? (object)int.Parse(v, CultureInfo.InvariantCulture) : float.Parse(v, CultureInfo.InvariantCulture));
                    }
                }
                if (ambushers.GetValue(null) is IList amb && part.TryGetValue("amb", out string a))
                {
                    if (a == "1" && !amb.Contains(uid)) amb.Add(uid);
                    else if (a == "0") while (amb.Contains(uid)) amb.Remove(uid);
                }
                object pilot = getPilot.Invoke(__instance, null), set = headHits.GetValue(null);
                if (pilot != null && set != null && part.TryGetValue("head", out string hd))
                    (hd == "1" ? headAdd : headRemove).Invoke(set, new[] { pilot });
                Row("store-apply", uid + "|before=" + before + "|after=" + State(__instance));
            }
            catch (Exception ex) { Row("store-apply-failed", ex.Message); }
        }

        static void Row(string kind, string text)
        {
            rowCounts.TryGetValue(kind, out int n);
            if (n >= 80) return;
            rowCounts[kind] = n + 1;
            log("extended-ce-save|" + kind + "|" + text);
        }

        static void ArmPostfix()
        {
            if (armed) return;
            armed = true;
            try
            {
                object settings = extendedCe.GetType("Extended_CE.Core", false)
                    ?.GetField("Settings", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                FieldInfo enable = settings?.GetType().GetField("CombatSavesDontWorkEnableAnyway");
                if (enable == null || enable.FieldType != typeof(bool))
                {
                    log("extended-ce-save|skipped|CombatSavesDontWorkEnableAnyway not readable settings=" + (settings != null) + "; combat saves remain off");
                    return;
                }
                enable.SetValue(settings, true);
                log("extended-ce-save|combat saves on (UCU toggle)");
            }
            catch (Exception ex) { log("extended-ce-save|arm|FAILED|" + ex); }
        }
    }
}
