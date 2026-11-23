// Pilot stat history after a co-op combat-save load (gh issue 24, CBTBE-5 compare 217; .hermes/reports/issue24-javelin-read-1/report.md Q2).
// A pilot-targeted StatisticEffect undoes its change only by removing its own history events (StatisticEffect.OnEffectEnd,
// StatisticEffect.cs:122-140: RemoveHistoryEvent for each historyEventUIDs entry, then RefreshStatistic). AbstractActor and
// MechComponent save their StatHistory and put it back on a load (AbstractActor.cs:3860-3863, 3909; MechComponent.cs:409-412);
// Pilot does not (retail Pilot.cs has no serializedStatHistory), and StatCollection.Load restores each stat's value and default
// with an empty history (StatCollection.cs:320-332, `history = new StatHistory(this)`; Statistic.Save holds value and default,
// Statistic.cs:185-191). So after any combat load a pilot stat keeps its effects' values with no events behind them, and an
// effect that ends cannot take its change back. BTA's Life Support A and B (Gear_Cockpit_LifeSupportA/B_Standard.json,
// BleedingRateMulti Float_Multiply 0.95, targetCollection Pilot) end when the Javelin dies: the host, which never reloads, went
// back to 1; the reloaded co-op guest stayed at 0.9025.
// This adapter, in a co-op session only (as ComponentOrderLoadCompat.CoopSession), on a combat loading from a save, runs a
// postfix on EffectManager.Hydrate. That is after every pilot is hydrated and after every effect has found its stat collection:
// TurnDirector.Hydrate hydrates the actors, and with them their pilots (Mech.cs:3357-3361), then calls EffectManager.Hydrate
// (TurnDirector.cs:1357-1382), whose body runs each StatisticEffect.Hydrate (GetProperStatCollectionOnLoad: Target.GetPilot().
// StatCollection for targetCollection Pilot); the pilots' stat values were set earlier still, when the save was deserialized
// (StatCollection PostDeserialization). It is before CombatGameState.InitFromSave (GameInstance.CreateCombatFromSave:
// HydrateCombat, then LaunchContractFromSave), so no effect created after the load is in the list yet. For each pilot stat
// that hydrated pilot-targeted StatisticEffects applied (historyEventUIDs not empty), it resets the stat to its default and
// applies each of those effects again through StatCollection.ModifyStatistic, in the order of their saved event uids (the
// order the host created them; one call per saved event, so an effect that ticks keeps its count), and gives each effect
// the new event uids. If the result is not the loaded value (within 1e-6 for a number) the events are removed and the loaded
// value is put back, as loaded. Rows: `pilot-history-load|rebuilt|<actor>/<callsign>|<stat>|effects=<n>|value=<v>|history=<count>`,
// `pilot-history-load|left-as-loaded|<actor>/<callsign>|<stat>|loaded=..|rebuilt=..`.
// Armed when Assembly-CSharp loads, installed at the first combat-save load before anything hydrates (ComponentOrderLoadCompat).
// shortcut: a pilot stat that also had a write outside an effect keeps its loaded value without history (the guard leaves it);
// the rebuilt events take new uids, so the guest's pilot uidTracker runs ahead of the host's (not compared: MechStatsReporter
// drops history entries). Upgrade to saving Pilot's StatHistory (a transplant patch) if a leg shows a left-as-loaded row that differs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class PilotStatHistoryLoadCompat
    {
        const string OwnId = "battletech.port.pilot-history-load";
        const string Tag = "pilot-history-load";
        static Action<string> log;
        static PropertyInfo battleTechGame, effectCollection, modVariant, effectData, loading, currentValue;
        static FieldInfo effects, historyUids, statisticData, targetingData, statName, operation, triggerType, effectId, stackItemUid, effectTarget;
        static MethodInfo modify, removeEvent, getStatistic, statReset, getPilot, historyFor, variantSet;
        static Type statisticEffectType, statCollectionType;
        static FieldInfo carriedField;
        static object onDamaged;
        static bool applied;
        static int rows;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("Assembly-CSharp", game =>
            {
                MethodInfo create = game.GetType("BattleTech.GameInstance", false)?.GetMethod("CreateCombatFromSave", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (create == null) { log(Tag + "|skipped|GameInstance.CreateCombatFromSave not found"); return; }
                new Harmony(OwnId).Patch(create, prefix: new HarmonyMethod(typeof(PilotStatHistoryLoadCompat).GetMethod(nameof(BeforeCombatLoad), BindingFlags.Static | BindingFlags.NonPublic)));
                log(Tag + "|armed|GameInstance.CreateCombatFromSave");
            }, logLine, Tag);
        }

        static void BeforeCombatLoad()
        {
            if (applied) return;
            applied = true;
            try { Apply(); }
            catch (Exception ex) { log(Tag + "|FAILED|" + ex); }
        }

        static void Apply()
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", st);
            Type manager = ModAssemblyHook.GameType("BattleTech.EffectManager");
            Type combat = ModAssemblyHook.GameType("BattleTech.CombatGameState");
            Type refs = ModAssemblyHook.GameType("BattleTech.Save.Test.SerializableReferenceContainer");
            Type effect = ModAssemblyHook.GameType("BattleTech.Effect");
            Type collection = statCollectionType = ModAssemblyHook.GameType("BattleTech.StatCollection");
            Type statistic = ModAssemblyHook.GameType("BattleTech.Statistic");
            Type variant = ModAssemblyHook.GameType("BattleTech.Variant");
            Type history = ModAssemblyHook.GameType("BattleTech.StatHistory");
            Type op = collection?.GetNestedType("StatOperation");
            Type trigger = ModAssemblyHook.GameType("BattleTech.EffectTriggerType");
            statisticEffectType = ModAssemblyHook.GameType("BattleTech.StatisticEffect");
            Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor");
            MethodInfo hydrate = combat == null || refs == null ? null : manager?.GetMethod("Hydrate", inst | BindingFlags.DeclaredOnly, null, new[] { combat, refs }, null);
            effects = manager?.GetField("effects", inst);
            loading = combat?.GetProperty("IsLoadingFromSave", inst);
            effectCollection = statisticEffectType?.GetProperty("statCollection", inst | BindingFlags.DeclaredOnly);
            historyUids = statisticEffectType?.GetField("historyEventUIDs", inst | BindingFlags.DeclaredOnly);
            modVariant = statisticEffectType?.GetProperty("ModVariant", inst);
            effectData = effect?.GetProperty("EffectData", inst);
            effectId = effect?.GetField("id", inst);
            stackItemUid = effect?.GetField("stackItemUID", inst);
            effectTarget = effect?.GetField("target", inst);
            Type data = ModAssemblyHook.GameType("BattleTech.EffectData");
            statisticData = data?.GetField("statisticData", inst);
            targetingData = data?.GetField("targetingData", inst);
            statName = ModAssemblyHook.GameType("BattleTech.StatisticEffectData")?.GetField("statName", inst);
            operation = ModAssemblyHook.GameType("BattleTech.StatisticEffectData")?.GetField("operation", inst);
            triggerType = ModAssemblyHook.GameType("BattleTech.EffectTargetingData")?.GetField("effectTriggerType", inst);
            onDamaged = trigger == null ? null : Enum.Parse(trigger, "OnDamaged");
            modify = op == null || variant == null ? null
                : collection.GetMethod("ModifyStatistic", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(int), typeof(string), op, variant, typeof(int), typeof(bool) }, null);
            removeEvent = statistic == null ? null : collection?.GetMethod("RemoveHistoryEvent", BindingFlags.Instance | BindingFlags.Public, null, new[] { statistic, typeof(int), typeof(bool) }, null);
            getStatistic = collection?.GetMethod("GetStatistic", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
            historyFor = history?.GetMethod("GetHistoryForStatistic", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
            statReset = statistic?.GetMethod("Reset", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            currentValue = statistic?.GetProperty("CurrentValue", inst);
            variantSet = variant?.GetMethod("SetValue", BindingFlags.Instance | BindingFlags.Public, null, new[] { variant }, null);
            getPilot = actor?.GetMethod("GetPilot", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (hydrate == null || effects == null || loading == null || effectCollection == null || historyUids == null || modVariant == null
                || effectData == null || effectId == null || stackItemUid == null || effectTarget == null || statisticData == null || targetingData == null
                || statName == null || operation == null || triggerType == null || onDamaged == null || modify == null || removeEvent == null || getStatistic == null
                || historyFor == null || statReset == null || currentValue == null || variantSet == null || getPilot == null || battleTechGame == null)
            {
                log(Tag + "|skipped|shape changed EffectManager.Hydrate=" + (hydrate != null) + " effects=" + (effects != null) + " StatisticEffect.statCollection=" + (effectCollection != null)
                    + " historyEventUIDs=" + (historyUids != null) + " ModifyStatistic=" + (modify != null) + " RemoveHistoryEvent=" + (removeEvent != null)
                    + " GetHistoryForStatistic=" + (historyFor != null) + " Statistic.Reset=" + (statReset != null) + " Variant.SetValue=" + (variantSet != null) + " GetPilot=" + (getPilot != null));
                return;
            }
            var postfix = new HarmonyMethod(typeof(PilotStatHistoryLoadCompat).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last };
            new Harmony(OwnId).Patch(hydrate, postfix: postfix);
            log(Tag + "|installed|pilot stat history rebuilt from the hydrated pilot effects after a co-op combat-save load (EffectManager.Hydrate)");
        }

        static bool CoopSession()
        {
            if (AppDomain.CurrentDomain.GetData("BattleTech.UCU.CoopCombat.v1") as string == "on") return true;
            object net = Get(battleTechGame.GetValue(null, null), "NetworkGameInstance");
            return Get(net, "IsCampaignHost") is bool h && h || Get(net, "IsCampaignClient") is bool c && c;
        }

        sealed class Use { public object Effect; public int SavedUid; }

        sealed class Group
        {
            public object Collection, Actor;
            public string Stat;
            public readonly List<Use> Uses = new List<Use>();
            public readonly List<object> Effects = new List<object>();
        }

        static void Postfix(object __instance)
        {
            try
            {
                object combat = Get(__instance, "Combat");
                if (combat == null || !(bool)loading.GetValue(combat, null) || !CoopSession()) return;
                // UCU patch 183 carries pilot StatHistory in the co-op snapshot block and counts the pilots it restored. That load already
                // has the host's exact history, so the rebuild from effects is skipped (the two must never both apply).
                FieldInfo carried = carriedField ?? (carriedField = statCollectionType?.GetField("CoopPilotHistoryCarried", BindingFlags.Static | BindingFlags.Public));
                if (carried != null && carried.GetValue(null) is int n && n > 0)
                {
                    carried.SetValue(null, 0);
                    Row("skipped|snapshot-carried=" + n);
                    return;
                }
                if (!(effects.GetValue(__instance) is IList list)) return;
                var groups = new List<Group>();
                foreach (object e in list)
                {
                    if (e == null || !statisticEffectType.IsInstanceOfType(e)) continue;
                    object coll = effectCollection.GetValue(e, null);
                    object target = effectTarget.GetValue(e);
                    object pilot = target == null ? null : SafePilot(target);
                    if (coll == null || pilot == null || !ReferenceEquals(coll, Get(pilot, "StatCollection"))) continue;
                    object ed = effectData.GetValue(e, null);
                    if (Equals(triggerType.GetValue(targetingData.GetValue(ed)), onDamaged)) continue;   // OnEffectEnd never undoes these
                    if (!(historyUids.GetValue(e) is List<int> uids) || uids.Count == 0) continue;
                    string stat = statName.GetValue(statisticData.GetValue(ed)) as string;
                    if (string.IsNullOrEmpty(stat)) continue;
                    Group g = groups.Find(x => ReferenceEquals(x.Collection, coll) && x.Stat == stat);
                    if (g == null) { g = new Group { Collection = coll, Actor = target, Stat = stat }; groups.Add(g); }
                    g.Effects.Add(e);
                    foreach (int u in uids) g.Uses.Add(new Use { Effect = e, SavedUid = u });
                }
                foreach (Group g in groups) Rebuild(g);
            }
            catch (Exception ex) { log(Tag + "|postfix-failed|" + ex.Message); }
        }

        static void Rebuild(Group g)
        {
            string who = Get(g.Actor, "GUID") + "/" + Get(SafePilot(g.Actor), "Callsign");
            object stat = getStatistic.Invoke(g.Collection, new object[] { g.Stat });
            if (stat == null) { Row("left-as-loaded|" + who + "|" + g.Stat + "|no such statistic"); return; }
            object loaded = Activator.CreateInstance(currentValue.PropertyType, currentValue.GetValue(stat, null));   // a copy (Variant(Variant))
            g.Uses.Sort((a, b) => a.SavedUid.CompareTo(b.SavedUid));
            var fresh = new Dictionary<object, List<int>>(new RefEq());
            var added = new List<int>();
            FieldInfo callback = stat.GetType().GetField("useChangedCallback");
            bool cb = callback != null && (bool)callback.GetValue(stat);
            if (callback != null) callback.SetValue(stat, false);
            try
            {
                statReset.Invoke(stat, null);
                foreach (Use u in g.Uses)
                {
                    object ed = effectData.GetValue(u.Effect, null);
                    int uid = (int)modify.Invoke(g.Collection, new[] { effectId.GetValue(u.Effect), stackItemUid.GetValue(u.Effect), g.Stat,
                        operation.GetValue(statisticData.GetValue(ed)), modVariant.GetValue(u.Effect, null), -1, true });
                    if (uid < 0) continue;
                    added.Add(uid);
                    if (!fresh.TryGetValue(u.Effect, out List<int> l)) fresh[u.Effect] = l = new List<int>();
                    l.Add(uid);
                }
                string was = Text(loaded), now = Text(currentValue.GetValue(stat, null));
                if (added.Count != g.Uses.Count || !Same(loaded, currentValue.GetValue(stat, null)))
                {
                    foreach (int uid in added) removeEvent.Invoke(g.Collection, new[] { stat, uid, true });
                    variantSet.Invoke(currentValue.GetValue(stat, null), new[] { loaded });
                    Row("left-as-loaded|" + who + "|" + g.Stat + "|loaded=" + was + "|rebuilt=" + now + (added.Count != g.Uses.Count ? "|applied=" + added.Count + "/" + g.Uses.Count : ""));
                    return;
                }
                foreach (object e in g.Effects) historyUids.SetValue(e, fresh.TryGetValue(e, out List<int> l) ? l : new List<int>());
                object hist = Get(g.Collection, "History");
                int count = hist == null ? -1 : ((IList)historyFor.Invoke(hist, new object[] { g.Stat })).Count;
                Row("rebuilt|" + who + "|" + g.Stat + "|effects=" + g.Effects.Count + "|value=" + now + "|history=" + count);
            }
            finally { if (callback != null) callback.SetValue(stat, cb); }
        }

        // Numbers within 1e-6, anything else by its text.
        static bool Same(object a, object b)
        {
            object x = Value(a), y = Value(b);
            if (x is IConvertible && y is IConvertible && !(x is string) && !(y is string) && !(x is bool) && !(y is bool))
                return Math.Abs(Convert.ToDouble(x, CultureInfo.InvariantCulture) - Convert.ToDouble(y, CultureInfo.InvariantCulture)) <= 1e-6;
            return Text(a) == Text(b);
        }

        static object Value(object variant)
        {
            Type t = Get(variant, "type") as Type;
            if (t == null) return null;
            MethodInfo m = variant.GetType().GetMethod("Value", BindingFlags.Instance | BindingFlags.Public);
            try { return m?.MakeGenericMethod(t).Invoke(variant, null); } catch { return null; }
        }

        static string Text(object variant)
        {
            object v = Value(variant);
            return v is float f ? f.ToString("R", CultureInfo.InvariantCulture) : v is double d ? d.ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(v ?? variant, CultureInfo.InvariantCulture);
        }

        static object SafePilot(object actor)
        {
            try { return getPilot.DeclaringType.IsInstanceOfType(actor) ? getPilot.Invoke(actor, null) : null; } catch { return null; }
        }

        static void Row(string text)
        {
            if (++rows <= 200 || rows % 50 == 0) log(Tag + "|" + text);
        }

        sealed class RefEq : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
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
