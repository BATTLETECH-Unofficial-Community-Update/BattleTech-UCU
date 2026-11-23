// Actor state the co-op snapshot drops, sent with a co-op snapshot load (gh issue 24, CBTBE-6b compares 104 and 206;
// .hermes/reports/issue24-hornet-fix-1).
// A combat save, and so a co-op resync or rejoin snapshot, writes only [SerializableMember(SaveGame)] fields
// (FunctionalityObjectGenerator.cs:101; SerializableReferenceContainer.cs:60,92). Three fields that decide later outcomes are
// [JsonSerialized] only, so a guest's reload resets them to 0/false while the host, which never reloads, keeps them:
//  - Mech._tempHeat (Mech.cs:67-68): heat gained and not yet added. CustomAmmoCategories' AMS adds its heat on the AMS owner with
//    AddWeaponHeat (CustomAmmoCategories.cs:4863) and no heat sequence runs for it (retail runs one only after heat damage,
//    AttackDirector.cs:2278-2281), so it stays until the owner's own heat step. CBTBE-6b: the Hornet held 2 on the host after the AMS
//    volley; after the guest's reload it held 0, so at its activation end the host sank 18 and the guest 16 (CACOverrallHeatSinked
//    18/16, CACUsedHEatSinks 24/21, compare 104).
//  - AbstractActor._flaggedForKnockdown (AbstractActor.cs:85-88): set by FlagForKnockdown, cleared only by GenerateFallSequence
//    (Mech.cs:2841). A leg destroyed while the unit is falling or prone leaves it set (HandleKnockdown skips IsOrWillBeProne,
//    Mech.cs:2831-2835). CBTBE-6b: the harness fall, then CBTBE's fall damage took the Hornet's left leg (16:51:27, both peers); the
//    guest's reload cleared the flag; at 16:54:58 CustomAmmoCategories' HandleSanitize stability sweep (after a move through a
//    minefield) ran HandleKnockdown on every mech, and only the host made the Hornet fall, took CBTBE's fake melee and lost the pilot
//    (compare 206).
//  - Mech._isBecomingProne (Mech.cs:59-60): the fall in progress.
// This adapter, only in a co-op session, registers a source with UCU's co-op side channel (CoopInvocationSideData, patch 166; as
// PanicSystemTrackerCoopSync does). The host's full state for a peer that has just loaded (capture(null)) is those three fields for every
// actor, by GUID; the guest's apply(null) writes them on its actors. Nothing is sent with an invocation. _flaggedForDeath and
// _hasHandledDeath are written too (CBTBE-7, 2026-11-22: "Flagged For Death" host True / guest False on 5a7f08f5 at every act dump
// after each reload, two resyncs): the host decides whether a death is pending, and the guest's own row beside it is logged.
// Order on the guest (the PanicSystemTrackerCoopSync proof carries over): the guest reports "cmp9|loaded" only once its new combat is
// ready (CampaignSessionClient.NotifyRejoinLoaded, CMP9CombatReady); the host answers with OnPeerLoaded, which sends this full state at
// once (CampaignSnapshotRequestMessageHandler.cs:39-43; CampaignSessionHost.cs:2108-2140), while its held orders go from a later Update
// (CampaignSessionHost.cs:1760-1766), on the same connection behind it; the guest applies a full state on receipt
// (CoopInvocationSideData.ClientPayload -> ApplyLoaded). So the fields are written before any order of the next activation, and the heat
// step and the stability sweeps run only from orders.
// Rows: host `actor-transient|capture|...|at=snapshot` (CampaignSessionHost.SerializeSnapshot postfix) and `...|at=send` (capture);
// guest `actor-transient|before|...` and `actor-transient|apply|...` in the same format, `actor-transient|death|host=...|guest=...|same=...`,
// and `actor-transient|unmapped|<guid>` for a GUID with no actor. Lists hold only units whose value is not 0/false, in ordinal GUID order.
// AbstractActor.occupiedDesignMask (gh issue 24 snapshot-field sweep, .hermes/reports/issue24-sweep-1/snapshot-fields.tsv): not in a save
// (an auto-property with no SerializableMember), so a loaded actor starts with none and DesignMaskStickyLoadCompat (CustomStatisticEffects
// only) infers it from the cell's priority mask. The host's live value is the mask of its last SetOccupiedDesignMask, which decides whether
// the next move applies a mask's sticky effect (AbstractActor.SetOccupiedDesignMask; CustomAmmoCategories' temp masks and deferred
// ReapplyDesignMasks make the cell's mask a guess). The host's mask id is sent per actor and the guest sets the property to the def with
// that id (DataManager.DesignMaskDefs, then MapMetaData.designMaskDefs, then another actor's current mask); no sticky effect is applied.
// An id the guest cannot resolve is logged (`actor-transient|mask-unmapped|<guid>|<id>`) and the guest's own value is left.
// CBTBE-8 resync 2 (Catapult ec9d2280 ReceivedInstabilityMultiplier host 1.5 / guest 1, guest designmask-load|kept DesignMaskWhirlwind):
// the rows below log both peers' masks so the next leg can name the cause; this field is a candidate there, not a proven one.
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
    public static class CoopActorTransientStateSync
    {
        const string Key = "actor-transient";
        const string Tag = "actor-transient";
        static Action<string> log;
        static PropertyInfo battleTechGame;
        static FieldInfo fTempHeat, fBecoming, fKnock, fDeath, fHandled;
        static Type mechType;
        static MethodInfo findActor;
        static PropertyInfo occupiedMask;
        static MethodInfo setOccupiedMask;
        static int rows, maskRows;
        const string NoMask = "~";

        sealed class State { public int TempHeat; public bool Knock, Becoming, Death, Handled; public string Mask = NoMask; }

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("Assembly-CSharp", a => ModAssemblyHook.AtFirstCombat(Install, logLine, Tag), logLine, Tag);
        }

        static void Install()
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor");
            mechType = ModAssemblyHook.GameType("BattleTech.Mech");
            fTempHeat = mechType?.GetField("_tempHeat", inst);
            fBecoming = mechType?.GetField("_isBecomingProne", inst);
            fKnock = actor?.GetField("_flaggedForKnockdown", inst);
            fDeath = actor?.GetField("_flaggedForDeath", inst);
            fHandled = actor?.GetField("_hasHandledDeath", inst);
            occupiedMask = actor?.GetProperty("occupiedDesignMask", inst);
            setOccupiedMask = occupiedMask?.GetSetMethod(true);
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", st);
            findActor = ModAssemblyHook.GameType("BattleTech.CombatGameState")?.GetMethod("FindActorByGUID", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
            MethodInfo register = ModAssemblyHook.GameType("BattleTech.CoopInvocationSideData")?.GetMethod("Register", st);
            MethodInfo serialize = ModAssemblyHook.GameType("BattleTech.Network.Server.CampaignSessionHost")?.GetMethod("SerializeSnapshot", inst);
            if (fTempHeat == null || fTempHeat.FieldType != typeof(int) || fBecoming == null || fBecoming.FieldType != typeof(bool) || fKnock == null
                || fKnock.FieldType != typeof(bool) || fDeath == null || fHandled == null || battleTechGame == null || findActor == null || register == null)
            {
                log(Tag + "|skipped|shape changed Mech._tempHeat=" + (fTempHeat != null) + " Mech._isBecomingProne=" + (fBecoming != null)
                    + " AbstractActor._flaggedForKnockdown=" + (fKnock != null) + " death=" + (fDeath != null && fHandled != null)
                    + " FindActorByGUID=" + (findActor != null) + " CoopInvocationSideData.Register=" + (register != null) + " (needs UCU patch 166)");
                return;
            }
            register.Invoke(null, new object[] { Key, new Func<object, string>(Capture), new Action<object, string>(Apply) });
            string snap = "no SerializeSnapshot row (method not found)";
            if (serialize != null)
            {
                new Harmony("battletech.port.actor-transient-coop").Patch(serialize,
                    postfix: new HarmonyMethod(typeof(CoopActorTransientStateSync).GetMethod(nameof(SnapshotPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                snap = "row at CampaignSessionHost.SerializeSnapshot";
            }
            log(Tag + "|installed|Mech._tempHeat, Mech._isBecomingProne and AbstractActor._flaggedForKnockdown sent to a peer that loads a co-op snapshot"
                + " (death flags written); occupiedDesignMask " + (setOccupiedMask != null ? "sent" : "ERROR not found, not sent") + "; " + snap);
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
                object combat = Combat();
                if (combat == null || !CoopSession()) return;
                Row("capture", Read(combat), "snapshot");
            }
            catch (Exception e) { log(Tag + "|capture-failed|snapshot|" + e.Message); }
        }

        // Host: the full state for a peer that has just loaded (invocation == null); nothing with an invocation or a SendNow.
        static string Capture(object invocation)
        {
            object combat = Combat();
            if (invocation != null || combat == null || !CoopSession()) return null;
            Dictionary<string, State> all = Read(combat);
            Row("capture", all, "send");
            return Serialize(all);
        }

        // Guest: the host's full state, right after its snapshot load (CoopInvocationSideData.ApplyLoaded).
        static void Apply(object invocation, string data)
        {
            if (invocation != null || string.IsNullOrEmpty(data)) return;
            object combat = Combat();
            if (combat == null || !CoopSession()) return;
            Dictionary<string, State> mine = Read(combat);
            Row("before", mine, "apply");
            Dictionary<string, State> host = Parse(data);
            foreach (KeyValuePair<string, State> e in host)
            {
                object actor = Find(combat, e.Key);
                if (actor == null) { log(Tag + "|unmapped|" + e.Key); continue; }
                fKnock.SetValue(actor, e.Value.Knock);
                fDeath.SetValue(actor, e.Value.Death);
                fHandled.SetValue(actor, e.Value.Handled);
                if (mechType.IsInstanceOfType(actor))
                {
                    fTempHeat.SetValue(actor, e.Value.TempHeat);
                    fBecoming.SetValue(actor, e.Value.Becoming);
                }
                ApplyMask(combat, actor, e.Key, e.Value.Mask);
            }
            Row("apply", Read(combat), "apply");
            string hd = Death(host), gd = Death(mine);
            log(Tag + "|death|host=" + hd + "|guest=" + gd + "|same=" + (hd == gd));
        }

        static Dictionary<string, State> Read(object combat)
        {
            var all = new Dictionary<string, State>(StringComparer.Ordinal);
            if (!(Get(combat, "AllActors") is IList actors)) return all;
            foreach (object a in actors)
            {
                if (a == null || !(Get(a, "GUID") is string g) || g.Length == 0) continue;
                bool mech = mechType.IsInstanceOfType(a);
                all[g] = new State
                {
                    TempHeat = mech ? (int)fTempHeat.GetValue(a) : 0,
                    Becoming = mech && (bool)fBecoming.GetValue(a),
                    Knock = (bool)fKnock.GetValue(a),
                    Death = (bool)fDeath.GetValue(a),
                    Handled = (bool)fHandled.GetValue(a),
                    Mask = MaskId(a),
                };
            }
            return all;
        }

        // The id of the actor's occupiedDesignMask, "~" for none; "?" for an id that cannot be sent (holds a separator).
        static string MaskId(object actor)
        {
            if (occupiedMask == null) return NoMask;
            object mask = occupiedMask.GetValue(actor, null);
            if (mask == null) return NoMask;
            string id = Get(mask, "Id") as string;
            if (string.IsNullOrEmpty(id)) return "?";
            return id.IndexOfAny(new[] { ':', ',', '|', ';' }) >= 0 || id == NoMask ? "?" : id;
        }

        // Guest: the host's occupiedDesignMask, by id. "?" (unsendable) leaves the guest's own.
        static void ApplyMask(object combat, object actor, string guid, string id)
        {
            if (setOccupiedMask == null || id == "?" || id == MaskId(actor)) return;
            object def = null;
            if (id != NoMask)
            {
                def = MaskById(combat, id);
                if (def == null) { if (++maskRows <= 40) log(Tag + "|mask-unmapped|" + guid + "|" + id + "|kept=" + MaskId(actor)); return; }
            }
            setOccupiedMask.Invoke(actor, new[] { def });
        }

        static object MaskById(object combat, string id)
        {
            // The game's store (DataManager.DesignMaskDefs, an IDataItemStore<string, DesignMaskDef>).
            object store = Get(Get(battleTechGame.GetValue(null, null), "DataManager"), "DesignMaskDefs");
            if (store != null)
            {
                MethodInfo tryGet = null;
                var types = new List<Type> { store.GetType() };
                types.AddRange(store.GetType().GetInterfaces());
                foreach (Type t in types)
                {
                    tryGet = t.GetMethod("TryGet", BindingFlags.Instance | BindingFlags.Public);
                    if (tryGet != null && tryGet.GetParameters().Length == 2) break;
                    tryGet = null;
                }
                if (tryGet != null)
                {
                    object[] args = { id, null };
                    try { if ((bool)tryGet.Invoke(store, args) && args[1] != null) return args[1]; } catch { }
                }
            }
            // The map's own masks (MapMetaData.designMaskDefs), then a mask another actor holds (CustomAmmoCategories' runtime masks).
            if (Get(Get(combat, "MapMetaData"), "designMaskDefs") is IDictionary map)
                foreach (object m in map.Values)
                    if (m != null && Get(m, "Id") as string == id) return m;
            if (Get(combat, "AllActors") is IList actors)
                foreach (object a in actors)
                {
                    object m = a == null || occupiedMask == null ? null : occupiedMask.GetValue(a, null);
                    if (m != null && Get(m, "Id") as string == id) return m;
                }
            return null;
        }

        // "<guid>:<tempHeat>:<knock>:<becoming>:<death>:<handled>:<mask id>,..." (flags 0/1, mask "~" for none); no '|' and no ';'.
        static string Serialize(Dictionary<string, State> all)
        {
            var sb = new StringBuilder();
            foreach (string k in Sorted(all))
            {
                if (sb.Length > 0) sb.Append(',');
                State s = all[k];
                sb.Append(k).Append(':').Append(s.TempHeat.ToString(CultureInfo.InvariantCulture)).Append(':').Append(B(s.Knock)).Append(':')
                    .Append(B(s.Becoming)).Append(':').Append(B(s.Death)).Append(':').Append(B(s.Handled)).Append(':').Append(s.Mask);
            }
            return sb.ToString();
        }

        static Dictionary<string, State> Parse(string data)
        {
            var all = new Dictionary<string, State>(StringComparer.Ordinal);
            foreach (string r in data.Split(','))
            {
                string[] f = r.Split(':');
                // 6 fields: a host without the mask field ("?" leaves the guest's mask).
                if ((f.Length != 6 && f.Length != 7) || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int heat)) continue;
                all[f[0]] = new State { TempHeat = heat, Knock = f[2] == "1", Becoming = f[3] == "1", Death = f[4] == "1", Handled = f[5] == "1", Mask = f.Length == 7 ? f[6] : "?" };
            }
            return all;
        }

        static char B(bool v) => v ? '1' : '0';

        // GUIDs in ordinal order, so two peers' rows compare as text.
        static List<string> Sorted(Dictionary<string, State> all)
        {
            var keys = new List<string>(all.Keys);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        static string Death(Dictionary<string, State> all)
        {
            var sb = new StringBuilder();
            foreach (string k in Sorted(all))
                if (all[k].Death || all[k].Handled) { if (sb.Length > 0) sb.Append(','); sb.Append(k).Append(':').Append(B(all[k].Death)).Append('/').Append(B(all[k].Handled)); }
            return sb.ToString();
        }

        // actor-transient|<kind>|n=<units>|tempHeat=<guid:v,...>|knock=<guids>|becomingProne=<guids>|death=<guid:flag/handled,...>|at=<where>
        static void Row(string kind, Dictionary<string, State> all, string at)
        {
            if (++rows > 60) return;
            var heat = new StringBuilder();
            var knock = new StringBuilder();
            var becoming = new StringBuilder();
            var mask = new StringBuilder();
            foreach (string k in Sorted(all))
            {
                var e = new KeyValuePair<string, State>(k, all[k]);
                if (e.Value.TempHeat != 0) { if (heat.Length > 0) heat.Append(','); heat.Append(e.Key).Append(':').Append(e.Value.TempHeat.ToString(CultureInfo.InvariantCulture)); }
                if (e.Value.Knock) { if (knock.Length > 0) knock.Append(','); knock.Append(e.Key); }
                if (e.Value.Becoming) { if (becoming.Length > 0) becoming.Append(','); becoming.Append(e.Key); }
                if (e.Value.Mask != NoMask) { if (mask.Length > 0) mask.Append(','); mask.Append(e.Key).Append(':').Append(e.Value.Mask); }
            }
            log(Tag + "|" + kind + "|n=" + all.Count + "|tempHeat=" + heat + "|knock=" + knock + "|becomingProne=" + becoming + "|death=" + Death(all)
                + "|mask=" + mask + "|at=" + at);
        }

        static object Find(object combat, string guid)
        {
            try { return findActor.Invoke(combat, new object[] { guid }); } catch { return null; }
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
