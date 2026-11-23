// PanicSystem's damage tracker and panic levels after a co-op resync or rejoin load (gh issue 24, fault W2;
// .hermes/reports/issue24-javelin-read-1/report.md Q1).
// PanicSystem keeps, in static memory only, the units damaged since its last check (TurnDamageTracker.activationVictims, a
// List<AbstractActor>), the activating unit (attacker), each unit's armor and structure baseline (turnStartArmor/Structure), its
// external heat (turnExternalHeatAccumulator), the ejections counted (ejectedActors) and each pilot's panic level
// (Controller.TrackedActors). hintAttackComplete and completedTurnFor process the victims only while attacker is set
// (ps-decompile TurnDamageTracker.cs hintAttackComplete/completedTurnFor), and ProcessBatchedTurnDamage measures the damage
// against the baselines (DamageDuringTurn) and reads the panic level (DamageHandler.cs:91-185). None of it is in a save. A co-op
// guest's resync reload destroys its combat, and PanicSystem's OnCombatGameDestroyed postfix runs TurnDamageTracker.Reset()
// (CombatGameState_OnCombatGameDestroyedMap.cs; Reset at TurnDamageTracker.cs "Turn Damage Tracker Reset - new Mission"); the
// load itself leaves TrackedActors as it was (GameInstance_Load_Patch -> Controller.Resync, a no-op with BTA's CombatSaves false).
// The host never reloads. So in CBTBE-5 the Javelin's fall damage, taken with no unit activating, stayed in the host's victim list
// across the snapshot and the guest's was emptied: at the next activation's move-complete hook, under the same panic-coop seed,
// the host rolled a failed save (Unsettled, AccuracyModifier +1) and the guest had nothing to roll (compare 101); and after the
// reload the guest measured the next hit from max armor where the host measured from its last reset (14:54:31, guest-only save).
// This adapter, only when an assembly named PanicSystem is loaded and only in a co-op session, registers a source with UCU's
// co-op side channel (CoopInvocationSideData, patch 166; as CustomAmmoCategoriesCoopModeSync does). The host's full state for a
// peer that has just loaded (capture(null)) is that tracker state, actors by GUID; the guest's apply(null) replaces its own with
// it, actors mapped back by GUID. Nothing is sent with an invocation. Order on the guest: Reset runs when the old combat is
// destroyed inside the reload (CampaignSessionClient.StartResyncReload -> LoadTransitioning); the guest reports "cmp9|loaded" only
// once the new combat is ready (CampaignSessionClient.NotifyRejoinLoaded, CMP9CombatReady); the host answers it with
// OnRejoinLoaded, which only clears the wait entry, then OnPeerLoaded, which sends this full state at once
// (CampaignSnapshotRequestMessageHandler.cs:39-43; CampaignSessionHost.cs:2108-2140), while the held orders are released from a
// later Update (CampaignSessionHost.cs:1760-1766, "all-returned"), on the same connection behind it. So the apply lands after
// the Reset and before any order of the next activation. No effect or statistic is written: a panic level is set on its field,
// not through PilotTracker's setter, because the snapshot already carries the host's panic effects.
// Rows: host `panic-coop|tracker-capture|...|at=snapshot` (CampaignSessionHost.SerializeSnapshot postfix) and `...|at=send`
// (capture), guest `panic-coop|tracker-before|...` (its own state before the apply) and `panic-coop|tracker-apply|...`, all with
// the same fields; `panic-coop|tracker-unmapped|...` for a GUID with no actor on the guest.
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
    public static class PanicSystemTrackerCoopSync
    {
        const string Key = "panic-trk";
        const string Tag = "panic-coop";
        static Action<string> log;
        static PropertyInfo battleTechGame;
        static FieldInfo fVictims, fAttacker, fArmor, fStructure, fHeat, fEjected, fTracked, fTrackerGuid, fPanicStatus, fWorsened, fPrevent;
        static Type trackerType;
        static MethodInfo findActor;
        static int rows;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("PanicSystem", ps => ModAssemblyHook.AtFirstCombat(() => Install(ps), logLine, Tag), logLine, Tag);
        }

        static void Install(Assembly ps)
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type tdt = ps.GetType("PanicSystem.Components.TurnDamageTracker", false);
            Type controller = ps.GetType("PanicSystem.Components.Controller", false);
            trackerType = ps.GetType("PanicSystem.Components.PilotTracker", false);
            fVictims = tdt?.GetField("activationVictims", st);
            fAttacker = tdt?.GetField("attacker", st);
            fArmor = tdt?.GetField("turnStartArmor", st);
            fStructure = tdt?.GetField("turnStartStructure", st);
            fHeat = tdt?.GetField("turnExternalHeatAccumulator", st);
            fEjected = tdt?.GetField("ejectedActors", st);
            fTracked = controller?.GetField("TrackedActors", st);
            fTrackerGuid = trackerType?.GetField("Guid", inst);
            fPanicStatus = trackerType?.GetField("panicStatus", inst);
            fWorsened = trackerType?.GetField("PanicWorsenedRecently", inst);
            fPrevent = trackerType?.GetField("PreventEjection", inst);
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", st);
            findActor = ModAssemblyHook.GameType("BattleTech.CombatGameState")?.GetMethod("FindActorByGUID", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
            Type side = ModAssemblyHook.GameType("BattleTech.CoopInvocationSideData");
            MethodInfo register = side?.GetMethod("Register", st);
            MethodInfo serialize = ModAssemblyHook.GameType("BattleTech.Network.Server.CampaignSessionHost")?.GetMethod("SerializeSnapshot", inst | BindingFlags.DeclaredOnly);
            if (fVictims == null || fAttacker == null || fArmor == null || fStructure == null || fHeat == null || fEjected == null || fTracked == null
                || fTrackerGuid == null || fPanicStatus == null || fWorsened == null || fPrevent == null || battleTechGame == null || findActor == null || register == null)
            {
                log(Tag + "|tracker-skipped|shape changed victims=" + (fVictims != null) + " attacker=" + (fAttacker != null) + " baselines=" + (fArmor != null && fStructure != null)
                    + " heat=" + (fHeat != null) + " ejected=" + (fEjected != null) + " TrackedActors=" + (fTracked != null) + " PilotTracker=" + (fPanicStatus != null && fTrackerGuid != null)
                    + " FindActorByGUID=" + (findActor != null) + " CoopInvocationSideData.Register=" + (register != null) + " (needs UCU patch 166)");
                return;
            }
            register.Invoke(null, new object[] { Key, new Func<object, string>(Capture), new Action<object, string>(Apply) });
            string snap = "no SerializeSnapshot row (method not found)";
            if (serialize != null)
            {
                new Harmony("battletech.port.panic-tracker-coop").Patch(serialize,
                    postfix: new HarmonyMethod(typeof(PanicSystemTrackerCoopSync).GetMethod(nameof(SnapshotPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                snap = "row at CampaignSessionHost.SerializeSnapshot";
            }
            log(Tag + "|tracker-installed|PanicSystem TurnDamageTracker and TrackedActors sent to a peer that loads a co-op snapshot; " + snap);
        }

        static object Combat() => Get(battleTechGame.GetValue(null, null), "Combat");

        // ComponentOrderLoadCompat.CoopSession: a co-op campaign session or a co-op combat running.
        static bool CoopSession()
        {
            if (AppDomain.CurrentDomain.GetData("BattleTech.UCU.CoopCombat.v1") as string == "on") return true;
            object net = Get(battleTechGame.GetValue(null, null), "NetworkGameInstance");
            return Get(net, "IsCampaignHost") is bool h && h || Get(net, "IsCampaignClient") is bool c && c;
        }

        static void SnapshotPostfix()
        {
            try
            {
                if (Combat() == null || !CoopSession()) return;
                Row("tracker-capture", Serialize(), "snapshot");
            }
            catch (Exception e) { log(Tag + "|tracker-capture-failed|snapshot|" + e.Message); }
        }

        // Host: the full state for a peer that has just loaded (invocation == null); nothing with an invocation or a SendNow.
        static string Capture(object invocation)
        {
            if (invocation != null || Combat() == null || !CoopSession()) return null;
            string data = Serialize();
            Row("tracker-capture", data, "send");
            return data;
        }

        // Guest: the host's full state, right after its snapshot load (CoopInvocationSideData.ApplyLoaded).
        static void Apply(object invocation, string data)
        {
            if (invocation != null || string.IsNullOrEmpty(data)) return;
            object combat = Combat();
            if (combat == null || !CoopSession()) return;
            Row("tracker-before", Serialize(), "apply");
            Dictionary<string, string> part = Parts(data);
            var victims = (IList)Activator.CreateInstance(fVictims.FieldType);
            foreach (string g in List(part, "vic"))
            {
                object actor = Find(combat, g);
                if (actor != null) victims.Add(actor);
                else Unmapped("victim", g);
            }
            object attacker = null;
            string att = part.TryGetValue("att", out string a) ? a : "-";
            if (att != "-" && (attacker = Find(combat, att)) == null) Unmapped("attacker", att);
            var armor = (IDictionary)Activator.CreateInstance(fArmor.FieldType);
            var structure = (IDictionary)Activator.CreateInstance(fStructure.FieldType);
            var heat = (IDictionary)Activator.CreateInstance(fHeat.FieldType);
            foreach (string r in List(part, "arm")) { string[] f = r.Split(':'); if (f.Length == 2) armor[f[0]] = float.Parse(f[1], CultureInfo.InvariantCulture); }
            foreach (string r in List(part, "str")) { string[] f = r.Split(':'); if (f.Length == 2) structure[f[0]] = float.Parse(f[1], CultureInfo.InvariantCulture); }
            foreach (string r in List(part, "heat")) { string[] f = r.Split(':'); if (f.Length == 2) heat[f[0]] = int.Parse(f[1], CultureInfo.InvariantCulture); }
            var ejected = (IList)Activator.CreateInstance(fEjected.FieldType);
            foreach (string g in List(part, "ej")) ejected.Add(g);
            // Panic levels: the host's list, in the host's order; a tracker this peer already has is updated in place.
            var mine = new Dictionary<string, object>();
            if (fTracked.GetValue(null) is IList old) foreach (object t in old) if (t != null && fTrackerGuid.GetValue(t) is string g) mine[g] = t;
            var tracked = (IList)Activator.CreateInstance(fTracked.FieldType);
            foreach (string r in List(part, "trk"))
            {
                string[] f = r.Split(':');
                if (f.Length != 4) continue;
                if (!mine.TryGetValue(f[0], out object t))
                {
                    t = Activator.CreateInstance(trackerType, true);
                    fTrackerGuid.SetValue(t, f[0]);
                    if (Find(combat, f[0]) == null) Unmapped("tracked", f[0]);
                }
                fPanicStatus.SetValue(t, Enum.ToObject(fPanicStatus.FieldType, int.Parse(f[1], CultureInfo.InvariantCulture)));
                fWorsened.SetValue(t, f[2] == "1");
                fPrevent.SetValue(t, f[3] == "1");
                tracked.Add(t);
            }
            fVictims.SetValue(null, victims);
            fAttacker.SetValue(null, attacker);
            fArmor.SetValue(null, armor);
            fStructure.SetValue(null, structure);
            fHeat.SetValue(null, heat);
            fEjected.SetValue(null, ejected);
            fTracked.SetValue(null, tracked);
            Row("tracker-apply", Serialize(), "apply");
        }

        // "att=<guid|->;vic=<g>,<g>;arm=<g>:<f>,...;str=<g>:<f>,...;heat=<g>:<i>,...;ej=<g>,...;trk=<g>:<status>:<worsened>:<prevent>,..."
        static string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("att=").Append(fAttacker.GetValue(null) is object at ? Get(at, "GUID") as string ?? "-" : "-");
            sb.Append(";vic=");
            bool first = true;
            if (fVictims.GetValue(null) is IList victims)
                foreach (object v in victims) { if (!first) sb.Append(','); sb.Append(Get(v, "GUID") as string ?? "?"); first = false; }
            sb.Append(";arm=").Append(Floats(fArmor.GetValue(null) as IDictionary));
            sb.Append(";str=").Append(Floats(fStructure.GetValue(null) as IDictionary));
            sb.Append(";heat=").Append(Ints(fHeat.GetValue(null) as IDictionary));
            sb.Append(";ej=");
            first = true;
            if (fEjected.GetValue(null) is IList ej) foreach (object g in ej) { if (!first) sb.Append(','); sb.Append(g); first = false; }
            sb.Append(";trk=");
            first = true;
            if (fTracked.GetValue(null) is IList tracked)
                foreach (object t in tracked)
                {
                    if (t == null) continue;
                    if (!first) sb.Append(',');
                    sb.Append(fTrackerGuid.GetValue(t)).Append(':').Append(Convert.ToInt32(fPanicStatus.GetValue(t), CultureInfo.InvariantCulture))
                        .Append(':').Append((bool)fWorsened.GetValue(t) ? '1' : '0').Append(':').Append((bool)fPrevent.GetValue(t) ? '1' : '0');
                    first = false;
                }
            return sb.ToString();
        }

        static string Floats(IDictionary d) => Pairs(d, v => ((float)v).ToString("R", CultureInfo.InvariantCulture));

        static string Ints(IDictionary d) => Pairs(d, v => ((int)v).ToString(CultureInfo.InvariantCulture));

        // Sorted by GUID, so two peers' rows compare as text.
        static string Pairs(IDictionary d, Func<object, string> value)
        {
            if (d == null) return "";
            var keys = new List<string>();
            foreach (object k in d.Keys) keys.Add((string)k);
            keys.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (string k in keys) { if (sb.Length > 0) sb.Append(','); sb.Append(k).Append(':').Append(value(d[k])); }
            return sb.ToString();
        }

        static Dictionary<string, string> Parts(string data)
        {
            var parts = new Dictionary<string, string>();
            foreach (string p in data.Split(';'))
            {
                int eq = p.IndexOf('=');
                if (eq > 0) parts[p.Substring(0, eq)] = p.Substring(eq + 1);
            }
            return parts;
        }

        static string[] List(Dictionary<string, string> parts, string key)
            => parts.TryGetValue(key, out string v) && v.Length > 0 ? v.Split(',') : new string[0];

        static object Find(object combat, string guid)
        {
            try { return findActor.Invoke(combat, new object[] { guid }); } catch { return null; }
        }

        static void Unmapped(string what, string guid) => log(Tag + "|tracker-unmapped|" + what + "|" + guid);

        // The row's fields: victims=<n>:<guids>|attacker=<guid>|baselines=<guid:a/s,...>|heat=<guid:h,...>|tracked=<n>:<hash>|ejected=<n>|at=<where>
        static void Row(string kind, string data, string at)
        {
            if (++rows > 60) return;
            Dictionary<string, string> p = Parts(data);
            string[] vic = List(p, "vic");
            var arm = new Dictionary<string, string>();
            foreach (string r in List(p, "arm")) { int c = r.IndexOf(':'); if (c > 0) arm[r.Substring(0, c)] = r.Substring(c + 1); }
            var sb = new StringBuilder();
            foreach (string r in List(p, "str"))
            {
                int c = r.IndexOf(':');
                if (c <= 0) continue;
                string g = r.Substring(0, c);
                if (sb.Length > 0) sb.Append(',');
                sb.Append(g).Append(':').Append(arm.TryGetValue(g, out string av) ? av : "?").Append('/').Append(r.Substring(c + 1));
            }
            string trk = p.TryGetValue("trk", out string t) ? t : "";
            ulong h = 14695981039346656037UL;
            foreach (char ch in trk) h = (h ^ ch) * 1099511628211UL;
            log(Tag + "|" + kind + "|victims=" + vic.Length + ":" + string.Join(",", vic) + "|attacker=" + (p.TryGetValue("att", out string a) ? a : "-")
                + "|baselines=" + sb + "|heat=" + (p.TryGetValue("heat", out string ht) ? ht : "") + "|tracked=" + List(p, "trk").Length + ":" + h.ToString("x16")
                + "|ejected=" + List(p, "ej").Length + "|at=" + at);
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
