// An actor's occupied design mask after a co-op snapshot load (gh issue 24 snapshot-field sweep,
// .hermes/reports/issue24-sweep-1/snapshot-fields.tsv; CBTBE-8 resync 2).
// The rest of this adapter's state (Mech._tempHeat and _isBecomingProne, AbstractActor._flaggedForKnockdown, _flaggedForDeath and
// _hasHandledDeath: CBTBE-6b compares 104 and 206, CBTBE-7) is a LoadSteps carry now (`actor-fields`, ModStateCarry's generic actor-field
// encoder). This file keeps the one member that is not an encodable value: AbstractActor.occupiedDesignMask is a def object, sent as its
// id and found again on the guest, which no generic encoder does.
// AbstractActor.occupiedDesignMask: not in a save (an auto-property with no SerializableMember), so a loaded actor starts with none and
// DesignMaskStickyLoadCompat (CustomStatisticEffects only) infers it from the cell's priority mask. The host's live value is the mask of its
// last SetOccupiedDesignMask, which decides whether the next move applies a mask's sticky effect (AbstractActor.SetOccupiedDesignMask;
// CustomAmmoCategories' temp masks and deferred ReapplyDesignMasks make the cell's mask a guess). The host's mask id is sent per actor and the
// guest sets the property to the def with that id (DataManager.DesignMaskDefs, then MapMetaData.designMaskDefs, then another actor's current
// mask); no sticky effect is applied. An id the guest cannot resolve is logged (`actor-transient|mask-unmapped|<guid>|<id>`) and the
// guest's own value is left.
// This adapter, only in a co-op session, registers a source with UCU's co-op side channel (CoopInvocationSideData, patch 166). The host's
// full state for a peer that has just loaded (capture(null)) is every actor's mask id by GUID; the guest's apply(null) sets it. Nothing is
// sent with an invocation. Order on the guest: the full state is applied on receipt (CoopInvocationSideData.ClientPayload -> ApplyLoaded),
// before any held order of the next activation (CampaignSessionHost.cs:2108-2140, :1760-1766).
// Rows: host `actor-transient|capture|n=<units>|mask=<guid:id,...>|at=send`; guest `actor-transient|before|...|at=apply` and `|apply|...`,
// `actor-transient|unmapped|<guid>`, `actor-transient|mask-unmapped|<guid>|<id>|kept=<id>`.
// shortcut: a third peer's held order and the full state share the host's one relay order, as for patch 166's other sources.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace BattleTechPort
{
    public static class CoopActorTransientStateSync
    {
        const string Key = "actor-transient";
        const string Tag = "actor-transient";
        static Action<string> log;
        static PropertyInfo battleTechGame;
        static MethodInfo findActor;
        static PropertyInfo occupiedMask;
        static MethodInfo setOccupiedMask;
        static int rows, maskRows;
        const string NoMask = "~";

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("Assembly-CSharp", a => ModAssemblyHook.AtFirstCombat(Install, logLine, Tag), logLine, Tag);
        }

        static void Install()
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            occupiedMask = ModAssemblyHook.GameType("BattleTech.AbstractActor")?.GetProperty("occupiedDesignMask", inst);
            setOccupiedMask = occupiedMask?.GetSetMethod(true);
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", st);
            findActor = ModAssemblyHook.GameType("BattleTech.CombatGameState")?.GetMethod("FindActorByGUID", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
            MethodInfo register = ModAssemblyHook.GameType("BattleTech.CoopInvocationSideData")?.GetMethod("Register", st);
            if (occupiedMask == null || setOccupiedMask == null || battleTechGame == null || findActor == null || register == null)
            {
                log(Tag + "|skipped|shape changed occupiedDesignMask=" + (occupiedMask != null) + " setter=" + (setOccupiedMask != null)
                    + " FindActorByGUID=" + (findActor != null) + " CoopInvocationSideData.Register=" + (register != null) + " (needs UCU patch 166)");
                return;
            }
            register.Invoke(null, new object[] { Key, new Func<object, string>(Capture), new Action<object, string>(Apply) });
            log(Tag + "|installed|AbstractActor.occupiedDesignMask sent to a peer that loads a co-op snapshot (the actor fields are LoadSteps' actor-fields carry)");
        }

        static object Combat() => Get(battleTechGame.GetValue(null, null), "Combat");

        // ComponentOrderLoadCompat.CoopSession: a co-op campaign session or a co-op combat running.
        static bool CoopSession()
        {
            if (AppDomain.CurrentDomain.GetData("BattleTech.UCU.CoopCombat.v1") as string == "on") return true;
            object net = Get(battleTechGame.GetValue(null, null), "NetworkGameInstance");
            return Get(net, "IsCampaignHost") is bool h && h || Get(net, "IsCampaignClient") is bool c && c;
        }

        // Host: the full state for a peer that has just loaded (invocation == null); nothing with an invocation or a SendNow.
        static string Capture(object invocation)
        {
            object combat = Combat();
            if (invocation != null || combat == null || !CoopSession()) return null;
            Dictionary<string, string> all = Read(combat);
            Row("capture", all, "send");
            return Serialize(all);
        }

        // Guest: the host's full state, right after its snapshot load (CoopInvocationSideData.ApplyLoaded).
        static void Apply(object invocation, string data)
        {
            if (invocation != null || string.IsNullOrEmpty(data)) return;
            object combat = Combat();
            if (combat == null || !CoopSession()) return;
            Row("before", Read(combat), "apply");
            foreach (KeyValuePair<string, string> e in Parse(data))
            {
                object actor = Find(combat, e.Key);
                if (actor == null) { log(Tag + "|unmapped|" + e.Key); continue; }
                ApplyMask(combat, actor, e.Key, e.Value);
            }
            Row("apply", Read(combat), "apply");
        }

        static Dictionary<string, string> Read(object combat)
        {
            var all = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!(Get(combat, "AllActors") is IList actors)) return all;
            foreach (object a in actors)
                if (a != null && Get(a, "GUID") is string g && g.Length > 0) all[g] = MaskId(a);
            return all;
        }

        // The id of the actor's occupiedDesignMask, "~" for none; "?" for an id that cannot be sent (holds a separator).
        static string MaskId(object actor)
        {
            object mask = occupiedMask.GetValue(actor, null);
            if (mask == null) return NoMask;
            string id = Get(mask, "Id") as string;
            if (string.IsNullOrEmpty(id)) return "?";
            return id.IndexOfAny(new[] { ':', ',', '|', ';' }) >= 0 || id == NoMask ? "?" : id;
        }

        // Guest: the host's occupiedDesignMask, by id. "?" (unsendable) leaves the guest's own.
        static void ApplyMask(object combat, object actor, string guid, string id)
        {
            if (id == "?" || id == MaskId(actor)) return;
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
                    object m = a == null ? null : occupiedMask.GetValue(a, null);
                    if (m != null && Get(m, "Id") as string == id) return m;
                }
            return null;
        }

        // "<guid>:<mask id>,..." (mask "~" for none); no '|' and no ';'.
        static string Serialize(Dictionary<string, string> all)
        {
            var sb = new StringBuilder();
            foreach (string k in Sorted(all))
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(k).Append(':').Append(all[k]);
            }
            return sb.ToString();
        }

        static Dictionary<string, string> Parse(string data)
        {
            var all = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string r in data.Split(','))
            {
                string[] f = r.Split(':');
                if (f.Length == 2) all[f[0]] = f[1];
            }
            return all;
        }

        // GUIDs in ordinal order, so two peers' rows compare as text.
        static List<string> Sorted(Dictionary<string, string> all)
        {
            var keys = new List<string>(all.Keys);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        // actor-transient|<kind>|n=<units>|mask=<guid:id,...>|at=<where>   (units with no mask are left out of mask=)
        static void Row(string kind, Dictionary<string, string> all, string at)
        {
            if (++rows > 60) return;
            var mask = new StringBuilder();
            foreach (string k in Sorted(all))
                if (all[k] != NoMask) { if (mask.Length > 0) mask.Append(','); mask.Append(k).Append(':').Append(all[k]); }
            log(Tag + "|" + kind + "|n=" + all.Count + "|mask=" + mask + "|at=" + at);
        }

        static object Find(object combat, string guid)
        {
            try { return findActor.Invoke(combat, new object[] { guid }); } catch { return null; }
        }

        static object Get(object o, string name) => ModStateCarry.Get(o, name);
    }
}
