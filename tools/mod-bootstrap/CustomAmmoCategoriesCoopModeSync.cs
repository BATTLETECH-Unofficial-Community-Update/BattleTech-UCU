// CustomAmmoCategories weapon modes and ammo in a co-op combat (gh issue 24, fault J follow-up; Sam, 2026-11-21, D-20261121-3;
// .hermes/reports/issue24-faultJ-sync-1.md).
// CustomAmmoCategories keeps each weapon's firing mode and ammo in two weapon stats, "CAC-WeaponMode" (WeaponModeStatisticName) and
// "CurrentAmmoId" (AmmoIdStatName), and changes them on one peer only: the AI's attack choice (ChooseBestWeaponForTarget, from
// AttackEvaluator.MakeAttackOrder) and its brace evaluation run where the AI thinks (the host); a player's mode or ammo click in the
// combat HUD (CycleMode, CycleAmmo, SyncMode) and the HUD's weapon-order defaults (ResetSortedWeaponList) run on the clicking or
// displaying peer. No invocation carries them, so the other peers fired the same attack with other modes and the CMP-9 MECHS compare
// differed. This adapter, only when an assembly named CustomAmmoCategories is loaded, registers a source with UCU's co-op invocation
// channel (CoopInvocationSideData, UCU patch 166): with each invocation a peer sends, it sends both stats of the weapons of the units
// it is the authority for (a client: the units it owns; the host: its own and the AI's), all of the acting unit's weapons and the
// others' that changed since its last send; every other peer writes them right before that invocation runs. Single player and a game
// without UCU patch 166 are untouched. A resync or rejoin snapshot does not carry the two stats (H-fix-h3: the Viper's AMS Off on the
// host, On on the guest after the reload), so the host also sends every unit's stats to a peer that reports its snapshot loaded. Coexists with CustomAmmoCategoriesCoopBraceModes, which undoes the brace evaluation's leftover
// modes on the host before anything is sent.
// A HUD click (CycleMode, CycleAmmo, SyncMode returning true) is sent at once (CoopInvocationSideData.SendNow): left for the unit's next
// invocation it missed an activation compare and the resync put the old mode back (S1-dry-3; .hermes/reports/issue24-faultJ-sync-fix-2.md).
// The host gives it the next invocation it relays as its order point (UCU patch 176) and every peer applies it right before that
// invocation. Until then the clicking peer's MECHS compare reads the weapon's two stats as they were before the click (the value every
// other peer still holds), and from that point on the real value, so the compare never sees a click in flight and still sees a click
// that was lost or applied wrong (.hermes/reports/issue24-faultJ-sync-fix-3.md).
// A receiver never writes the units it is the authority for, except from the host's full state after a snapshot load.
// shortcut: a stat present on a receiver and absent on the authority is left alone (CustomAmmoCategories creates both on first write).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;


namespace BattleTechPort
{
    public static class CustomAmmoCategoriesCoopModeSync
    {
        const string Key = "cac-wm";
        const string Absent = "~";
        static Action<string> log;
        static string modeStat, ammoStat;
        static MethodInfo isAuthority, sendNow, clearCache, getValue, setString, addString;
        static object nowMarker;
        static FieldInfo currentInvocation;
        // The clicking peer's weapons with a click not yet at its order point: StatCollection -> the values the other peers hold.
        static readonly Dictionary<object, Pending> pending = new Dictionary<object, Pending>(new SameObject());
        static object pendingCombat;
        static int inCompare, pendingRows;

        sealed class Pending { public string Unit, Mode, Ammo; }

        sealed class SameObject : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
        static PropertyInfo battleTechGame;
        static readonly Dictionary<string, string> lastSent = new Dictionary<string, string>();
        static object lastSentCombat;
        static int logged, refused;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("CustomAmmoCategories", Apply, logLine, "cac-coop-mode-sync");
        }

        static void Apply(Assembly cac)
        {
            const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type side = ModAssemblyHook.GameType("BattleTech.CoopInvocationSideData");
            MethodInfo register = side?.GetMethod("Register", stat);
            isAuthority = side?.GetMethod("IsAuthority", stat);
            sendNow = side?.GetMethod("SendNow", stat);
            nowMarker = side?.GetField("Now", stat)?.GetValue(null);
            currentInvocation = ModAssemblyHook.GameType("BattleTech.CoopStrayShots")?.GetField("CurrentInvocation", stat);
            Type cacMain = cac.GetType("CustAmmoCategories.CustomAmmoCategories", false);
            modeStat = cacMain?.GetField("WeaponModeStatisticName", stat)?.GetValue(null) as string;
            ammoStat = cacMain?.GetField("AmmoIdStatName", stat)?.GetValue(null) as string;
            Type weaponType = ModAssemblyHook.GameType("BattleTech.Weapon");
            Type cache = cac.GetType("CustAmmoCategories.WeaponStatCacheHelper", false);
            if (cache != null && weaponType != null)
                foreach (MethodInfo m in cache.GetMethods(stat))
                    if (m.Name == "ClearAmmoModeCache" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == weaponType) { clearCache = m; break; }
            Type collection = ModAssemblyHook.GameType("BattleTech.StatCollection");
            getValue = ModAssemblyHook.GameType("BattleTech.Statistic")?.GetMethod("Value", BindingFlags.Instance | BindingFlags.Public)?.MakeGenericMethod(typeof(string));
            if (collection != null)
                foreach (MethodInfo m in collection.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    ParameterInfo[] p = m.GetParameters();
                    if (!m.IsGenericMethodDefinition || p.Length != 2 || p[0].ParameterType != typeof(string)) continue;
                    if (m.Name == "Set") setString = m.MakeGenericMethod(typeof(string));
                    else if (m.Name == "AddStatistic") addString = m.MakeGenericMethod(typeof(string));
                }
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", stat);
            if (register == null || isAuthority == null || sendNow == null || nowMarker == null || currentInvocation == null || modeStat == null || ammoStat == null || clearCache == null || getValue == null || setString == null
                || addString == null || battleTechGame == null)
            {
                log("cac-coop-mode-sync|skipped|shape changed CoopInvocationSideData.Register=" + (register != null) + " IsAuthority=" + (isAuthority != null) + " SendNow=" + (sendNow != null) + " CurrentInvocation=" + (currentInvocation != null)
                    + " WeaponModeStatisticName=" + (modeStat != null) + " AmmoIdStatName=" + (ammoStat != null) + " ClearAmmoModeCache=" + (clearCache != null)
                    + " Statistic.Value=" + (getValue != null) + " StatCollection.Set=" + (setString != null) + " AddStatistic=" + (addString != null)
                    + " BattleTechGame=" + (battleTechGame != null) + " (needs UCU patches 166 and 176)");
                return;
            }
            register.Invoke(null, new object[] { Key, new Func<object, string>(Capture), new Action<object, string>(ApplyData) });
            // The HUD's writers (WeaponModeHover.cs:210, 236; WeaponAmmoHover.cs:272; WeaponExtendedInfo.cs:375, 599).
            var harmony = new HarmonyLib.Harmony("battletech.port.cac-coop-mode-sync");
            const BindingFlags own = BindingFlags.Static | BindingFlags.NonPublic;
            var prefix = new HarmonyLib.HarmonyMethod(typeof(CustomAmmoCategoriesCoopModeSync).GetMethod(nameof(ClickPrefix), own));
            var postfix = new HarmonyLib.HarmonyMethod(typeof(CustomAmmoCategoriesCoopModeSync).GetMethod(nameof(ClickPostfix), own));
            int hooked = 0;
            foreach (MethodInfo m in cacMain.GetMethods(stat))
                if ((m.Name == "CycleMode" || m.Name == "CycleAmmo" || m.Name == "SyncMode") && m.ReturnType == typeof(bool)
                    && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == weaponType)
                {
                    try { harmony.Patch(m, prefix: prefix, postfix: postfix); hooked++; }
                    catch (Exception e) { log("cac-coop-mode-sync|hook-failed|" + m.Name + "|" + e.Message); }
                }
            // The MECHS compare (MechStatsReporter.OnTrigger, on OnTurnActorActivate) reads each component's stats through
            // StatCollection.GetStatKeyValues (MechStatsReporter.cs:92).
            MethodInfo trigger = ModAssemblyHook.GameType("BattleTech.MechStatsReporter")?.GetMethod("OnTrigger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo keyValues = collection?.GetMethod("GetStatKeyValues", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            string compare;
            if (trigger == null || keyValues == null)
                compare = "ERROR pending compare not installed (MechStatsReporter.OnTrigger=" + (trigger != null) + " GetStatKeyValues=" + (keyValues != null) + ")";
            else
            {
                try
                {
                    harmony.Patch(trigger, prefix: new HarmonyLib.HarmonyMethod(typeof(CustomAmmoCategoriesCoopModeSync).GetMethod(nameof(CompareEnter), own)),
                        finalizer: new HarmonyLib.HarmonyMethod(typeof(CustomAmmoCategoriesCoopModeSync).GetMethod(nameof(CompareExit), own)));
                    harmony.Patch(keyValues, postfix: new HarmonyLib.HarmonyMethod(typeof(CustomAmmoCategoriesCoopModeSync).GetMethod(nameof(ReportPostfix), own)));
                    compare = "the MECHS compare reads a clicked weapon's pre-click value until its order point";
                }
                catch (Exception e) { compare = "ERROR pending compare failed: " + e.Message; }
            }
            log("cac-coop-mode-sync|installed|" + modeStat + "," + ammoStat + " carried with co-op invocations; " + hooked + " HUD writers sent at once; " + compare);
        }

        // Before a HUD click on a weapon of a unit this peer is the authority for, outside an invocation's run, in a co-op combat
        // (IsAuthority is false outside one): the values the other peers hold, if no click on it is pending yet.
        static void ClickPrefix(object __0)
        {
            try
            {
                if (__0 == null || currentInvocation.GetValue(null) != null) return;
                object actor = Get(__0, "parent");
                if (actor == null || !(bool)isAuthority.Invoke(null, new[] { actor })) return;
                object stats = Get(__0, "StatCollection");
                if (stats == null) return;
                NotePendingCombat();
                if (!pending.ContainsKey(stats))
                    pending[stats] = new Pending { Unit = Get(actor, "GUID") + ":" + Get(__0, "uid"), Mode = Read(stats, modeStat), Ammo = Read(stats, ammoStat) };
            }
            catch (Exception e) { if (++refused <= 8) log("cac-coop-mode-sync|click-prefix-failed|" + e.Message); }
        }

        // A mode or ammo change outside an invocation goes out at once (SendNow ignores one made inside an invocation's run).
        static void ClickPostfix(bool __result, object __0)
        {
            try
            {
                object stats = __0 == null ? null : Get(__0, "StatCollection");
                if (stats != null && pending.TryGetValue(stats, out Pending was) && Read(stats, modeStat) == was.Mode && Read(stats, ammoStat) == was.Ammo)
                    pending.Remove(stats);   // back where the other peers are
                if (!__result) return;
                sendNow.Invoke(null, new object[] { Key });
            }
            catch (Exception e) { if (++refused <= 8) log("cac-coop-mode-sync|send-now-failed|" + e.Message); }
        }

        static void NotePendingCombat()
        {
            object combat = Get(battleTechGame.GetValue(null, null), "Combat");
            if (!ReferenceEquals(combat, pendingCombat)) { pending.Clear(); pendingCombat = combat; }
        }

        static void CompareEnter() { inCompare++; }

        static void CompareExit() { if (inCompare > 0) inCompare--; }

        // Inside a MECHS compare: a weapon with a click pending reports what every other peer still holds.
        static void ReportPostfix(object __instance, Dictionary<string, string> __result)
        {
            if (inCompare == 0 || pending.Count == 0 || __result == null || !pending.TryGetValue(__instance, out Pending p)) return;
            string actual = Read(__instance, modeStat) + "," + Read(__instance, ammoStat);
            Report(__result, modeStat, p.Mode);
            Report(__result, ammoStat, p.Ammo);
            if (++pendingRows <= 40) log("cac-coop-mode-sync|pending|" + p.Unit + "|reported=" + p.Mode + "," + p.Ammo + "|actual=" + actual);
        }

        static void Report(Dictionary<string, string> values, string name, string value)
        {
            if (value == Absent) values.Remove(name);
            else values[name] = value;
        }

        // Sender: "actorGUID:uid,mode,ammo/uid,mode,ammo;actorGUID:..." ("~" = the stat does not exist), or null.
        static string Capture(object invocation)
        {
            object combat = Get(battleTechGame.GetValue(null, null), "Combat");
            if (combat == null) return null;
            if (!ReferenceEquals(combat, lastSentCombat)) { lastSent.Clear(); lastSentCombat = combat; }
            bool full = invocation == null;   // the host's full state for a peer that has just loaded a resync or rejoin snapshot
            string acting = full ? null : ActorGuid(invocation);
            StringBuilder sb = new StringBuilder();
            foreach (object actor in Get(combat, "AllActors") as IEnumerable ?? new object[0])
            {
                if (!full && !(bool)isAuthority.Invoke(null, new[] { actor })) continue;
                string guid = Get(actor, "GUID") as string;
                if (string.IsNullOrEmpty(guid) || !Safe(guid)) continue;
                bool all = full || guid == acting;
                int start = sb.Length;
                bool any = false;
                foreach (object weapon in Get(actor, "Weapons") as IEnumerable ?? new object[0])
                {
                    string uid = Get(weapon, "uid") as string;
                    object stats = Get(weapon, "StatCollection");
                    if (string.IsNullOrEmpty(uid) || stats == null) continue;
                    string mode = Read(stats, modeStat), ammo = Read(stats, ammoStat);
                    // The full state for a peer that has just loaded carries what every peer holds until the click's order point.
                    if (full && pending.TryGetValue(stats, out Pending held)) { mode = held.Mode; ammo = held.Ammo; }
                    if (mode == Absent && ammo == Absent) continue;
                    if (!Safe(uid) || !Safe(mode) || !Safe(ammo))
                    {
                        if (++refused <= 4) log("cac-coop-mode-sync|unsendable|" + guid + "|" + uid + "|" + mode + "|" + ammo);
                        continue;
                    }
                    string value = mode + "," + ammo;
                    string key = guid + ":" + uid;
                    if (!all && lastSent.TryGetValue(key, out string was) && was == value) continue;
                    if (!full) lastSent[key] = value;
                    sb.Append(any ? "/" : (sb.Length == 0 ? "" : ";") + guid + ":").Append(uid).Append(',').Append(value);
                    any = true;
                }
                if (!any) sb.Length = start;
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        // Receiver, right before the invocation runs.
        static void ApplyData(object invocation, string data)
        {
            object combat = Get(battleTechGame.GetValue(null, null), "Combat");
            if (combat == null) return;
            Dictionary<string, object> actors = new Dictionary<string, object>();
            foreach (object actor in Get(combat, "AllActors") as IEnumerable ?? new object[0])
                if (Get(actor, "GUID") is string g) actors[g] = actor;
            int records = 0, changed = 0, missing = 0, own = 0;
            foreach (string group in data.Split(';'))
            {
                int colon = group.IndexOf(':');
                if (colon <= 0 || !actors.TryGetValue(group.Substring(0, colon), out object actor)) { missing++; continue; }
                // This peer's own units follow its own clicks and thinking; only the host's post-load full state overrides them. At a
                // click's order point (Now) the other peers now hold the click's value, so the compare reads it from here on.
                if (invocation != null && (bool)isAuthority.Invoke(null, new[] { actor }))
                {
                    own += group.Split('/').Length;
                    if (ReferenceEquals(invocation, nowMarker) && pending.Count > 0) Ordered(actor, group.Substring(colon + 1));
                    continue;
                }
                Dictionary<string, object> weapons = new Dictionary<string, object>();
                foreach (object weapon in Get(actor, "Weapons") as IEnumerable ?? new object[0])
                    if (Get(weapon, "uid") is string u) weapons[u] = weapon;
                foreach (string record in group.Substring(colon + 1).Split('/'))
                {
                    string[] f = record.Split(',');
                    records++;
                    if (f.Length != 3 || !weapons.TryGetValue(f[0], out object weapon)) { missing++; continue; }
                    object stats = Get(weapon, "StatCollection");
                    bool a = Write(stats, modeStat, f[1]), b = Write(stats, ammoStat, f[2]);
                    if (a || b) { clearCache.Invoke(null, new[] { weapon }); changed++; }
                }
            }
            if ((changed > 0 || missing > 0 || own > 0) && ++logged <= 60)
                log("cac-coop-mode-sync|applied|inv=" + (invocation == null ? "@" : ReferenceEquals(invocation, nowMarker) ? "!" : Get(invocation, "InvocationID") as string)
                    + "|records=" + records + "|changed=" + changed + "|own=" + own + "|missing=" + missing);
        }

        static void Ordered(object actor, string records)
        {
            Dictionary<string, object> weapons = new Dictionary<string, object>();
            foreach (object weapon in Get(actor, "Weapons") as IEnumerable ?? new object[0])
                if (Get(weapon, "uid") is string u) weapons[u] = weapon;
            foreach (string record in records.Split('/'))
            {
                string[] f = record.Split(',');
                if (f.Length != 3 || !weapons.TryGetValue(f[0], out object weapon)) continue;
                object stats = Get(weapon, "StatCollection");
                if (stats == null || !pending.TryGetValue(stats, out Pending p)) continue;
                if (f[1] != Absent) p.Mode = f[1];
                if (f[2] != Absent) p.Ammo = f[2];
                bool done = Read(stats, modeStat) == p.Mode && Read(stats, ammoStat) == p.Ammo;
                if (done) pending.Remove(stats);
                if (++pendingRows <= 40) log("cac-coop-mode-sync|ordered|" + p.Unit + "|value=" + p.Mode + "," + p.Ammo + "|pending=" + !done);
            }
        }

        static string Read(object stats, string name)
        {
            if (!(bool)Call(stats, "ContainsStatistic", name)) return Absent;
            object statistic = Call(stats, "GetStatistic", name);
            return statistic == null ? Absent : (getValue.Invoke(statistic, null) as string ?? "");
        }

        static bool Write(object stats, string name, string value)
        {
            if (value == Absent || Read(stats, name) == value) return false;
            if ((bool)Call(stats, "ContainsStatistic", name)) setString.Invoke(stats, new object[] { name, value });
            else addString.Invoke(stats, new object[] { name, value });
            return true;
        }

        static bool Safe(string s) => s.IndexOfAny(new[] { ',', ';', ':', '/', '|' }) < 0;

        static string ActorGuid(object invocation)
        {
            foreach (string name in new[] { "ActorGUID", "SourceGUID", "MechGUID", "targetGUID" })
                if (Get(invocation, name) is string guid && guid.Length > 0) return guid;
            return null;
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
