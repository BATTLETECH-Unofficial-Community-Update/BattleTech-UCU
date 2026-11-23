// CustomAmmoCategories artillery mode and terrain targets in a co-op combat (gh issue 24, CBTBE-8 resyncs 1 and 3 and the 4th mismatch;
// .hermes/reports/issue24-sweep-1/snapshot-fields.tsv rows CAC.ActorsInArtilleryMode, CAC.artilleryStrikes, CAC.terrainHitPositions,
// stat irbtmu_immobile_unit).
// CustomAmmoCategories keeps three pieces of unit state in static memory, written on one peer only:
//  - WeaponArtilleryHelper.ActorsInArtilleryMode (Dictionary<AbstractActor, ArtilleryUnitInfo>): ToArtilleryMode adds a unit and, through
//    the ArtilleryUnitInfo constructor, sets IRBTModUtils' actor stat "irbtmu_immobile_unit" true (MovementFeature.SetImmobile, a direct
//    Statistic.SetValue); FromArtilleryMode removes it and sets the stat back. While a unit is in it, Weapon.HasAmmo is false for its
//    non-AMS weapons (JammingRealizer postfix).
//  - WeaponArtilleryHelper.artilleryStrikes (Dictionary<Weapon, ArtilleryStrikeInfo>): each artillery weapon's target point.
//  - CustomAmmoCategories.terrainHitPositions (Dictionary<string, TerrainHitInfo>, by actor GUID): the ground point an attack on oneself
//    hits (addTerrainHitPosition), read on every peer while the attack resolves (getTerrinHitPosition in the hit generation).
// The AI enters artillery mode in a postfix on AITeam.makeInvocationFromOrders and fires from CreateStrikeInvocation, which also leaves the
// mode and sets the terrain point; both run only where the AI thinks (the host). A player does the same in the combat HUD (SelectionStateFire
// and SelectionStateFireMulti.CreateFiringOrders prefixes) on the clicking peer. No invocation carries any of it, so CBTBE-8 compare 10 found
// the Longbow d7109e5f's irbtmu_immobile_unit host True / guest False, compare 223 (after the resync gave the guest True) False / True with
// the Arrow IV damage split, and compare 335 True / False again. All three are cleared when a combat is destroyed
// (CombatGameState_OnCombatGameDestroyed prefix: terrainHitPositions.Clear, WeaponArtilleryHelper.Clear), so a guest's resync load loses
// them too, and the save holds only the stat.
// This adapter, only when an assembly named CustomAmmoCategories is loaded, registers a source with UCU's co-op side channel
// (CoopInvocationSideData, patch 166; as CustomAmmoCategoriesCoopModeSync does). With each invocation a peer sends, it sends, for the units it
// is the authority for, each unit's record that changed since its last send and always the acting unit's: in artillery mode or not, the
// weapons' strike points, the stat, and the terrain point. Every other peer makes its state match right before that invocation runs: it
// calls CustomAmmoCategories' own ToArtilleryMode / FromArtilleryMode, AddArtilleryStrike / ClearArtillery, addTerrainHitPosition, then
// writes the stat to the sender's value. So CustomAmmoCategories' all-peer exits from the mode (FlagForDeath, overheat and fall postfixes)
// then run alike on every peer. A peer that has just loaded a resync or rejoin snapshot gets every unit's record from the host
// (capture(null)), its own units included, and the host holds a guest unit's record from that guest's invocations.
// Order: registered after CustomAmmoCategoriesCoopModeSync, so a weapon's synced mode is in place when AddArtilleryStrike records it.
// Rows: sender `cac-arty|capture|<records>|at=send|inv=<id>` (records sent), host `...|at=snapshot` (CampaignSessionHost.SerializeSnapshot
// postfix, every unit) and `...|at=full`; receiver `cac-arty|before|...` and `cac-arty|apply|...` for every unit a record named, and
// `cac-arty|unmapped|<guid>` for a GUID or weapon uid with no match. A record: <guid>:<art 0/1>:<stat 0/1/~>:<terrain x_y_z_indirect or ~>:<strikes
// uid=x_y_z/... or ~>:<ArtilleryUnitInfo.AlreadyImmobilized 0/1, ~ out of the mode> (the sender's, so FromArtilleryMode resets the stat alike:
// a guest that enters the mode after a load already holds the saved true and would otherwise record AlreadyImmobilized true).
// shortcut: an ArtilleryStrikeInfo made on a receiver records the weapon's mode when it is made, not the sender's at its creation; upgrade
// by sending the AmmoModePair if a leg shows a strike fired in another mode.
// shortcut: a third peer's held order and the full state share the host's one relay order, as for patch 166's other sources.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomAmmoCategoriesCoopArtillerySync
    {
        const string Key = "cac-arty";
        const string Tag = "cac-arty";
        const string ImmobileStat = "irbtmu_immobile_unit";   // IRBTModUtils.ModStats.ImmobileUnit
        const string None = "~";
        static Action<string> log;
        static PropertyInfo battleTechGame;
        static MethodInfo isAuthority, isInMode, toMode, fromMode, addStrike, clearArtillery, addTerrain, getBool, setBool, addBool;
        static FieldInfo strikes, modeUnits, terrain, strikePos, terrainPos, terrainIndirect;
        static PropertyInfo already;
        static Type vector3, weaponType;
        static object nowMarker;
        static readonly Dictionary<string, string> lastSent = new Dictionary<string, string>(StringComparer.Ordinal);
        static object lastSentCombat;
        static int rows;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("CustomAmmoCategories", Apply, logLine, Tag);
        }

        static void Apply(Assembly cac)
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type side = ModAssemblyHook.GameType("BattleTech.CoopInvocationSideData");
            MethodInfo register = side?.GetMethod("Register", st);
            isAuthority = side?.GetMethod("IsAuthority", st);
            nowMarker = side?.GetField("Now", st)?.GetValue(null);
            Type actorType = ModAssemblyHook.GameType("BattleTech.AbstractActor");
            weaponType = ModAssemblyHook.GameType("BattleTech.Weapon");
            vector3 = ModAssemblyHook.GameType("UnityEngine.Vector3");
            Type helper = cac.GetType("CustAmmoCategories.WeaponArtilleryHelper", false);
            Type main = cac.GetType("CustAmmoCategories.CustomAmmoCategories", false);
            Type strikeInfo = cac.GetType("CustAmmoCategories.ArtilleryStrikeInfo", false);
            Type terrainInfo = cac.GetType("CustAmmoCategories.TerrainHitInfo", false);
            isInMode = helper?.GetMethod("IsInArtilleryMode", st, null, new[] { actorType }, null);
            toMode = helper?.GetMethod("ToArtilleryMode", st, null, new[] { actorType }, null);
            fromMode = helper?.GetMethod("FromArtilleryMode", st, null, new[] { actorType }, null);
            addStrike = vector3 == null ? null : helper?.GetMethod("AddArtilleryStrike", st, null, new[] { weaponType, vector3 }, null);
            foreach (MethodInfo m in helper?.GetMethods(st) ?? new MethodInfo[0])
                if (m.Name == "ClearArtillery" && m.GetParameters().Length == 1) { clearArtillery = m; break; }
            strikes = helper?.GetField("artilleryStrikes", st);
            modeUnits = helper?.GetField("ActorsInArtilleryMode", st);
            already = cac.GetType("CustAmmoCategories.ArtilleryUnitInfo", false)?.GetProperty("AlreadyImmobilized", inst);
            strikePos = strikeInfo?.GetField("position", inst);
            terrain = main?.GetField("terrainHitPositions", st);
            addTerrain = vector3 == null ? null : main?.GetMethod("addTerrainHitPosition", st, null, new[] { actorType, vector3, typeof(bool) }, null);
            terrainPos = terrainInfo?.GetField("pos", inst);
            terrainIndirect = terrainInfo?.GetField("indirect", inst);
            getBool = ModAssemblyHook.GameType("BattleTech.Statistic")?.GetMethod("Value", BindingFlags.Instance | BindingFlags.Public)?.MakeGenericMethod(typeof(bool));
            Type collection = ModAssemblyHook.GameType("BattleTech.StatCollection");
            foreach (MethodInfo m in collection?.GetMethods(BindingFlags.Instance | BindingFlags.Public) ?? new MethodInfo[0])
            {
                ParameterInfo[] p = m.GetParameters();
                if (!m.IsGenericMethodDefinition || p.Length != 2 || p[0].ParameterType != typeof(string)) continue;
                if (m.Name == "Set") setBool = m.MakeGenericMethod(typeof(bool));
                else if (m.Name == "AddStatistic") addBool = m.MakeGenericMethod(typeof(bool));
            }
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", st);
            MethodInfo serialize = ModAssemblyHook.GameType("BattleTech.Network.Server.CampaignSessionHost")?.GetMethod("SerializeSnapshot", inst);
            if (register == null || isAuthority == null || nowMarker == null || isInMode == null || toMode == null || fromMode == null || addStrike == null
                || clearArtillery == null || strikes == null || modeUnits == null || already == null || strikePos == null || terrain == null || addTerrain == null || terrainPos == null
                || terrainIndirect == null || getBool == null || setBool == null || addBool == null || battleTechGame == null)
            {
                log(Tag + "|skipped|shape changed CoopInvocationSideData.Register=" + (register != null) + " IsAuthority=" + (isAuthority != null)
                    + " WeaponArtilleryHelper.IsInArtilleryMode/To/From=" + (isInMode != null) + "/" + (toMode != null) + "/" + (fromMode != null)
                    + " AddArtilleryStrike=" + (addStrike != null) + " ClearArtillery=" + (clearArtillery != null) + " artilleryStrikes=" + (strikes != null)
                    + " ActorsInArtilleryMode=" + (modeUnits != null) + " AlreadyImmobilized=" + (already != null) + " ArtilleryStrikeInfo.position=" + (strikePos != null) + " terrainHitPositions=" + (terrain != null)
                    + " addTerrainHitPosition=" + (addTerrain != null) + " TerrainHitInfo.pos/indirect=" + (terrainPos != null) + "/" + (terrainIndirect != null)
                    + " StatCollection.Set/AddStatistic=" + (setBool != null) + "/" + (addBool != null) + " (needs UCU patch 166)");
                return;
            }
            register.Invoke(null, new object[] { Key, new Func<object, string>(Capture), new Action<object, string>(ApplyData) });
            string snap = "no SerializeSnapshot row (method not found)";
            if (serialize != null)
            {
                new Harmony("battletech.port.cac-arty-coop").Patch(serialize,
                    postfix: new HarmonyMethod(typeof(CustomAmmoCategoriesCoopArtillerySync).GetMethod(nameof(SnapshotPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                snap = "row at CampaignSessionHost.SerializeSnapshot";
            }
            log(Tag + "|installed|" + cac.GetName().Version + "|artillery mode, strike points, " + ImmobileStat + " and terrain points carried with co-op invocations"
                + " and sent to a peer that loads a co-op snapshot; " + snap);
        }

        static object Combat() => Get(battleTechGame.GetValue(null, null), "Combat");

        static bool Authority(object actor) => (bool)isAuthority.Invoke(null, new[] { actor });

        static void SnapshotPostfix()
        {
            try
            {
                object combat = Combat();
                if (combat == null || !CoopSession()) return;
                Row("capture", Records(combat, null, true), "snapshot");
            }
            catch (Exception e) { log(Tag + "|capture-failed|snapshot|" + e.Message); }
        }

        // ComponentOrderLoadCompat.CoopSession: a co-op campaign session or a co-op combat running.
        static bool CoopSession()
        {
            if (AppDomain.CurrentDomain.GetData("BattleTech.UCU.CoopCombat.v1") as string == "on") return true;
            object net = Get(battleTechGame.GetValue(null, null), "NetworkGameInstance");
            return Get(net, "IsCampaignHost") is bool h && h || Get(net, "IsCampaignClient") is bool c && c;
        }

        // Sender: the records for this invocation, or every unit's for a peer that has just loaded (invocation == null). Nothing for a SendNow.
        static string Capture(object invocation)
        {
            if (ReferenceEquals(invocation, nowMarker)) return null;
            object combat = Combat();
            if (combat == null) return null;
            if (!ReferenceEquals(combat, lastSentCombat)) { lastSent.Clear(); lastSentCombat = combat; }
            bool full = invocation == null;
            Dictionary<string, string> records = Records(combat, full ? null : ActorGuid(invocation), full);
            if (records.Count == 0) return null;
            Row("capture", records, full ? "full" : "send|inv=" + (Get(invocation, "InvocationID") as string ?? "?"), full);
            return Join(records);
        }

        // Every unit's record (full), or the sender's own units' records that changed since its last send plus the acting unit's.
        static Dictionary<string, string> Records(object combat, string acting, bool full)
        {
            var records = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<object, string> strikeText = StrikeText();
            foreach (object actor in Get(combat, "AllActors") as IEnumerable ?? new object[0])
            {
                string guid = Get(actor, "GUID") as string;
                if (string.IsNullOrEmpty(guid) || !Safe(guid)) continue;
                if (!full && !Authority(actor)) continue;
                string record = Record(actor, guid, strikeText);
                if (full) { records[guid] = record; continue; }
                bool changed = !lastSent.TryGetValue(guid, out string was) || was != record;
                if (!changed && guid != acting) continue;
                lastSent[guid] = record;
                records[guid] = record;
            }
            return records;
        }

        // "<art>:<stat>:<terrain>:<strikes>" for one unit.
        static string Record(object actor, string guid, Dictionary<object, string> strikeText)
        {
            string art = (bool)isInMode.Invoke(null, new[] { actor }) ? "1" : "0";
            string stat = ReadStat(actor);
            string ter = None;
            if (terrain.GetValue(null) is IDictionary positions && positions.Contains(guid) && positions[guid] is object info)
                ter = Vec(terrainPos.GetValue(info)) + "_" + ((bool)terrainIndirect.GetValue(info) ? "1" : "0");
            strikeText.TryGetValue(actor, out string sk);
            string held = None;
            if (art == "1" && modeUnits.GetValue(null) is IDictionary units && units.Contains(actor) && units[actor] is object info2)
                held = (bool)already.GetValue(info2, null) ? "1" : "0";
            return art + ":" + stat + ":" + ter + ":" + (string.IsNullOrEmpty(sk) ? None : sk) + ":" + held;
        }

        // The strike points by owning actor: "uid=x_y_z/uid=x_y_z".
        static Dictionary<object, string> StrikeText()
        {
            var text = new Dictionary<object, List<string>>(new SameObject());
            if (strikes.GetValue(null) is IDictionary all)
                foreach (DictionaryEntry e in all)
                {
                    object owner = Get(e.Key, "parent");
                    string uid = Get(e.Key, "uid") as string;
                    if (owner == null || string.IsNullOrEmpty(uid) || !Safe(uid) || uid.IndexOf('=') >= 0 || e.Value == null) continue;
                    if (!text.TryGetValue(owner, out List<string> l)) text[owner] = l = new List<string>();
                    l.Add(uid + "=" + Vec(strikePos.GetValue(e.Value)));
                }
            var result = new Dictionary<object, string>(new SameObject());
            foreach (KeyValuePair<object, List<string>> e in text) { e.Value.Sort(StringComparer.Ordinal); result[e.Key] = string.Join("/", e.Value.ToArray()); }
            return result;
        }

        // Receiver, right before the invocation runs (or at once for a full state).
        static void ApplyData(object invocation, string data)
        {
            if (string.IsNullOrEmpty(data)) return;
            object combat = Combat();
            if (combat == null) return;
            bool full = invocation == null;
            var actors = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (object actor in Get(combat, "AllActors") as IEnumerable ?? new object[0])
                if (Get(actor, "GUID") is string g) actors[g] = actor;
            var incoming = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string r in data.Split(','))
            {
                int colon = r.IndexOf(':');
                if (colon > 0) incoming[r.Substring(0, colon)] = r.Substring(colon + 1);
            }
            Dictionary<object, string> strikeText = StrikeText();
            var before = new Dictionary<string, string>(StringComparer.Ordinal);
            var targets = new List<KeyValuePair<object, string[]>>();
            int own = 0;
            foreach (KeyValuePair<string, string> e in incoming)
            {
                if (!actors.TryGetValue(e.Key, out object actor)) { if (++rows <= 400) log(Tag + "|unmapped|" + e.Key); continue; }
                // This peer's own units follow its own clicks and thinking; only the host's post-load full state overrides them.
                if (!full && Authority(actor)) { own++; continue; }
                string[] f = e.Value.Split(':');
                if (f.Length != 5) { if (++rows <= 400) log(Tag + "|bad-record|" + e.Key + "|" + e.Value); continue; }
                before[e.Key] = Record(actor, e.Key, strikeText);
                targets.Add(new KeyValuePair<object, string[]>(actor, f));
            }
            if (targets.Count == 0) return;
            string at = full ? "full" : "inv=" + (Get(invocation, "InvocationID") as string ?? "?");
            bool rowAll = full;
            foreach (KeyValuePair<object, string[]> t in targets) Write(t.Key, t.Value, strikeText);
            Dictionary<object, string> afterStrikes = StrikeText();
            var after = new Dictionary<string, string>(StringComparer.Ordinal);
            int changed = 0;
            foreach (KeyValuePair<object, string[]> t in targets)
            {
                string guid = Get(t.Key, "GUID") as string;
                after[guid] = Record(t.Key, guid, afterStrikes);
                if (after[guid] != before[guid]) changed++;
            }
            // The receiver's state before and after, in the sender's format, so the peers' rows compare as text.
            Row("before", before, at, rowAll || changed > 0);
            Row("apply", after, at + "|changed=" + changed + "|own=" + own, rowAll || changed > 0);
        }

        static void Write(object actor, string[] f, Dictionary<object, string> strikeText)
        {
            string guid = Get(actor, "GUID") as string;
            // 1. The mode, through CustomAmmoCategories' own calls (they set the stat and record the weapons' enabled state as the sender did).
            bool inMode = (bool)isInMode.Invoke(null, new[] { actor });
            if (f[0] == "1" && !inMode) toMode.Invoke(null, new[] { actor });
            else if (f[0] == "0" && inMode) fromMode.Invoke(null, new[] { actor });
            if (f[0] == "1" && f[4] != None && modeUnits.GetValue(null) is IDictionary units && units.Contains(actor) && units[actor] is object info)
                already.SetValue(info, f[4] == "1", null);
            // 2. The strike points.
            var wanted = new Dictionary<string, object>(StringComparer.Ordinal);
            if (f[3] != None)
                foreach (string s in f[3].Split('/'))
                {
                    int eq = s.IndexOf('=');
                    object v = eq > 0 ? ParseVec(s.Substring(eq + 1)) : null;
                    if (v != null) wanted[s.Substring(0, eq)] = v;
                }
            strikeText.TryGetValue(actor, out string have);
            if ((have ?? None) != f[3])
            {
                var weapons = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (object w in Get(actor, "Weapons") as IEnumerable ?? new object[0])
                    if (Get(w, "uid") is string u) weapons[u] = w;
                IDictionary all = strikes.GetValue(null) as IDictionary;
                var dropList = new List<object>();
                foreach (KeyValuePair<string, object> w in weapons)
                    if (!wanted.ContainsKey(w.Key) && all != null && all.Contains(w.Value)) dropList.Add(w.Value);
                if (dropList.Count > 0)
                {
                    Array drop = Array.CreateInstance(weaponType, dropList.Count);
                    for (int i = 0; i < dropList.Count; i++) drop.SetValue(dropList[i], i);
                    clearArtillery.Invoke(null, new object[] { drop });
                }
                foreach (KeyValuePair<string, object> s in wanted)
                {
                    if (!weapons.TryGetValue(s.Key, out object weapon)) { if (++rows <= 400) log(Tag + "|unmapped|" + guid + "|weapon=" + s.Key); continue; }
                    if (all != null && all.Contains(weapon) && Vec(strikePos.GetValue(all[weapon])) == Vec(s.Value)) continue;
                    addStrike.Invoke(null, new[] { weapon, s.Value });
                }
            }
            // 3. The terrain point (the sender decides; "~" removes this peer's).
            IDictionary positions = terrain.GetValue(null) as IDictionary;
            if (f[2] == None) { if (positions != null && positions.Contains(guid)) positions.Remove(guid); }
            else
            {
                int last = f[2].LastIndexOf('_');
                object v = last > 0 ? ParseVec(f[2].Substring(0, last)) : null;
                if (v != null) addTerrain.Invoke(null, new[] { actor, v, (object)(f[2].Substring(last + 1) == "1") });
            }
            // 4. The stat, last: the sender's value stands whatever the mode calls above set.
            if (f[1] != None) WriteStat(actor, f[1] == "1");
        }

        static string ReadStat(object actor)
        {
            object stats = Get(actor, "StatCollection");
            if (stats == null || !(bool)Call(stats, "ContainsStatistic", ImmobileStat)) return None;
            object statistic = Call(stats, "GetStatistic", ImmobileStat);
            return statistic == null ? None : ((bool)getBool.Invoke(statistic, null) ? "1" : "0");
        }

        static void WriteStat(object actor, bool value)
        {
            object stats = Get(actor, "StatCollection");
            if (stats == null) return;
            string was = ReadStat(actor);
            if (was == (value ? "1" : "0")) return;
            if (was == None) addBool.Invoke(stats, new object[] { ImmobileStat, value });
            else setBool.Invoke(stats, new object[] { ImmobileStat, value });
        }

        static string Vec(object v)
        {
            if (v == null) return "0_0_0";
            return F(Get(v, "x")) + "_" + F(Get(v, "y")) + "_" + F(Get(v, "z"));
        }

        static string F(object f) => f is float x ? x.ToString("R", CultureInfo.InvariantCulture) : "0";

        static object ParseVec(string s)
        {
            string[] p = s.Split('_');
            if (p.Length != 3) return null;
            float[] c = new float[3];
            for (int i = 0; i < 3; i++)
                if (!float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out c[i])) return null;
            return Activator.CreateInstance(vector3, new object[] { c[0], c[1], c[2] });
        }

        static string Join(Dictionary<string, string> records)
        {
            var keys = new List<string>(records.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (string k in keys) { if (sb.Length > 0) sb.Append(','); sb.Append(k).Append(':').Append(records[k]); }
            return sb.ToString();
        }

        // cac-arty|<kind>|n=<units>|<guid>:<record>,...|at=<where>; only records other than "0:0:~:~:~" / "0:~:~:~:~" are listed.
        // A per-invocation row whose records are all idle is not logged (force: the snapshot, full-state and changed apply rows).
        static void Row(string kind, Dictionary<string, string> records, string at, bool force = true)
        {
            var keep = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> e in records)
                if (e.Value != "0:0:~:~:~" && e.Value != "0:~:~:~:~") keep[e.Key] = e.Value;
            if (!force && keep.Count == 0) return;
            if (++rows > 400) return;
            log(Tag + "|" + kind + "|n=" + records.Count + "|" + Join(keep) + "|at=" + at);
        }

        static bool Safe(string s) => s.IndexOfAny(new[] { ',', ';', ':', '/', '|', '~' }) < 0;

        static string ActorGuid(object invocation)
        {
            foreach (string name in new[] { "ActorGUID", "SourceGUID", "MechGUID", "targetGUID" })
                if (Get(invocation, name) is string guid && guid.Length > 0) return guid;
            return null;
        }

        sealed class SameObject : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        static object Call(object o, string name, string arg)
            => o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null).Invoke(o, new object[] { arg });

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
