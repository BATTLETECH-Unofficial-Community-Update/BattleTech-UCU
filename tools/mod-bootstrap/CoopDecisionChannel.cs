// A decision made on one co-op peer, carried to the others and replayed there (gh issue 24; Sam, 2026-11-22, D-20261122-6 = A: "the host
// decides, and a side-data source sends each result"). Shared by CBTBehaviorsEnhancedCoopStartupDecision, CustomActivatableEquipmentCoopToggle and
// StrategicOperationsCoopSwarmDecision; not registered on its own.
// The deciding peer (CoopInvocationSideData.IsAuthority for the unit: the host for AI units, the owner for a player unit) records each decision
// as one record: a kind, the unit's GUID, UnityEngine.Random.state at the moment the mod's own call began, and the call's arguments and outcome.
// Records go out with the next invocation this peer sends (CoopInvocationSideData, UCU patch 166, as CustomAmmoCategoriesCoopArtillerySync), or
// at once (SendNow, UCU patch 176) when the decision was a HUD click outside any invocation. Every other peer, right before that invocation (or the
// click's order point), sets UnityEngine.Random.state to the recorded one, makes the same mod call with the recorded outcome forced, puts its own
// state back, and never replays a record for a unit it is itself the authority for.
// Rows: decider `<tag>|decide|<kind>|<actor>|<result>`, receiver `<tag>|apply|<kind>|<actor>|<result>` (the same result text, so the two rows
// compare as text), `<tag>|apply-failed|...` or `<tag>|unmapped|...` when a record does not apply, `<tag>|count|decide=<n>|apply=<n>` at
// CombatGameState.OnCombatGameDestroyed. A record: kind;actor;state;field;... (state s0,s1,s2,s3 or ~), records joined by '/'.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace BattleTechPort
{
    public sealed class CoopDecisionChannel
    {
        public delegate string Replay(object actor, string[] fields);

        public readonly string Key, Tag;
        public bool Replaying { get; private set; }
        public Action<object> BeforeCapture;   // may add records for the invocation being sent
        public Action<string, object> Ordered;   // the deciding peer: its own HUD record (kind, actor) reached its order point on every peer
        readonly Action<string> log;
        readonly Dictionary<string, Replay> replays = new Dictionary<string, Replay>(StringComparer.Ordinal);
        readonly List<string> queued = new List<string>();
        int decided, applied, rows;

        static MethodInfo register, isAuthority, sendNow, findActor;
        static FieldInfo currentInvocation;
        static PropertyInfo randomState, battleTechGame;
        static FieldInfo[] stateFields;
        static Type stateType;
        static object nowMarker;
        static readonly List<CoopDecisionChannel> channels = new List<CoopDecisionChannel>();
        static bool countHooked;

        public CoopDecisionChannel(string key, string tag, Action<string> logLine) { Key = key; Tag = tag; log = logLine; }

        // Resolves UCU's side channel and the engine members once; false (with a skipped row) when this game lacks them.
        public static bool Init(Action<string> log, string tag)
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            if (register != null) return true;
            Type side = ModAssemblyHook.GameType("BattleTech.CoopInvocationSideData");
            Type random = ModAssemblyHook.GameType("UnityEngine.Random");
            stateType = ModAssemblyHook.GameType("UnityEngine.Random+State");
            if (stateType != null)
            {
                stateFields = new FieldInfo[4];
                for (int i = 0; i < 4; i++) stateFields[i] = stateType.GetField("s" + i, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }
            randomState = random?.GetProperty("state", st);
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", st);
            currentInvocation = ModAssemblyHook.GameType("BattleTech.CoopStrayShots")?.GetField("CurrentInvocation", st);
            findActor = ModAssemblyHook.GameType("BattleTech.CombatGameState")?.GetMethod("FindActorByGUID", new[] { typeof(string) });
            MethodInfo reg = side?.GetMethod("Register", st);
            isAuthority = side?.GetMethod("IsAuthority", st);
            sendNow = side?.GetMethod("SendNow", st);
            nowMarker = side?.GetField("Now", st)?.GetValue(null);
            if (reg == null || isAuthority == null || sendNow == null || nowMarker == null || randomState == null || stateFields == null || Array.IndexOf(stateFields, null) >= 0
                || battleTechGame == null || currentInvocation == null || findActor == null)
            {
                log(tag + "|skipped|shape changed CoopInvocationSideData.Register/IsAuthority/SendNow/Now=" + (reg != null) + "/" + (isAuthority != null) + "/" + (sendNow != null) + "/" + (nowMarker != null)
                    + " Random.state/State.s0-s3=" + (randomState != null) + "/" + (stateFields != null && Array.IndexOf(stateFields, null) < 0) + " BattleTechGame=" + (battleTechGame != null)
                    + " CoopStrayShots.CurrentInvocation=" + (currentInvocation != null) + " FindActorByGUID=" + (findActor != null) + " (needs UCU patches 166 and 176)");
                return false;
            }
            register = reg;
            return true;
        }

        public void On(string kind, Replay replay) => replays[kind] = replay;

        // Harmony.Patch, with the method named in the error so an install failure row says which patch failed.
        public static void Patch(Harmony h, MethodBase m, HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod finalizer = null)
        {
            try { h.Patch(m, prefix: prefix, postfix: postfix, finalizer: finalizer); }
            catch (Exception e)
            {
                Exception inner = e;
                while (inner.InnerException != null) inner = inner.InnerException;
                throw new InvalidOperationException("patch " + m.DeclaringType.FullName + "." + m.Name + ": " + inner.GetType().Name + ": " + inner.Message, e);
            }
        }

        // Registers the source; the first channel also hooks the per-combat count rows.
        public void Install(string what)
        {
            register.Invoke(null, new object[] { Key, new Func<object, string>(Capture), new Action<object, string>(Apply) });
            lock (channels)
            {
                channels.Add(this);
                if (!countHooked)
                {
                    countHooked = true;
                    MethodInfo destroyed = ModAssemblyHook.GameType("BattleTech.CombatGameState")?.GetMethod("OnCombatGameDestroyed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (destroyed == null) log(Tag + "|FAILED|CombatGameState.OnCombatGameDestroyed not found: no count rows");
                    else Patch(new Harmony("battletech.port.coop-decisions"), destroyed,
                        prefix: new HarmonyMethod(typeof(CoopDecisionChannel).GetMethod(nameof(CountRows), BindingFlags.Static | BindingFlags.NonPublic)));
                }
            }
            log(Tag + "|installed|" + Key + "|" + what);
        }

        static void CountRows()
        {
            lock (channels)
                foreach (CoopDecisionChannel c in channels)
                {
                    c.log(c.Tag + "|count|decide=" + c.decided + "|apply=" + c.applied);
                    c.decided = c.applied = c.rows = 0;
                    c.queued.Clear();
                }
        }

        // Decider: queue one record. state is RandomState() taken when the mod's call began ("~" for none).
        public void Decide(string kind, object actor, string result, string state, params string[] fields)
        {
            string guid = Get(actor, "GUID") as string;
            if (string.IsNullOrEmpty(guid) || !Safe(guid) || !Safe(state)) { log(Tag + "|decide-failed|" + kind + "|" + guid + "|unsafe text"); return; }
            var sb = new StringBuilder(kind).Append(';').Append(guid).Append(';').Append(state);
            foreach (string f in fields)
            {
                if (!Safe(f)) { log(Tag + "|decide-failed|" + kind + "|" + guid + "|unsafe field " + f); return; }
                sb.Append(';').Append(f);
            }
            queued.Add(sb.ToString());
            decided++;
            Row("decide|" + kind + "|" + guid + "|" + result);
        }

        // Decider: a HUD decision has no invocation to ride; send what is queued now.
        public void SendNow()
        {
            try { sendNow.Invoke(null, new object[] { Key }); }
            catch (Exception e) { log(Tag + "|FAILED|SendNow|" + e.Message); }
        }

        string Capture(object invocation)
        {
            if (invocation == null) return null;   // a peer that loaded a snapshot: every decision already applied is in it
            if (!ReferenceEquals(invocation, nowMarker) && BeforeCapture != null)
            {
                try { BeforeCapture(invocation); }
                catch (Exception e) { log(Tag + "|capture-failed|" + e.Message); }
            }
            if (queued.Count == 0) return null;
            string data = string.Join("/", queued.ToArray());
            queued.Clear();
            return data;
        }

        void Apply(object invocation, string data)
        {
            if (invocation == null || string.IsNullOrEmpty(data)) return;
            object combat = Combat();
            foreach (string record in data.Split('/'))
            {
                string[] p = record.Split(';');
                if (p.Length < 3 || !replays.TryGetValue(p[0], out Replay replay)) { Row("apply-failed|" + (p.Length > 0 ? p[0] : "?") + "|?|bad record " + record); continue; }
                object actor = combat == null ? null : findActor.Invoke(combat, new object[] { p[1] });
                if (actor == null) { Row("unmapped|" + p[0] + "|" + p[1]); continue; }
                if (Authority(actor))   // this peer decided it (a HUD click's order point reaches the clicker too)
                {
                    if (ReferenceEquals(invocation, nowMarker) && Ordered != null)
                    {
                        try { Ordered(p[0], actor); }
                        catch (Exception e) { log(Tag + "|ordered-failed|" + p[0] + "|" + e.Message); }
                    }
                    continue;
                }
                var fields = new string[p.Length - 3];
                Array.Copy(p, 3, fields, 0, fields.Length);
                object own = randomState.GetValue(null, null);
                try
                {
                    Replaying = true;
                    if (p[2] != "~") SetRandomState(p[2]);
                    string result = replay(actor, fields);
                    applied++;
                    Row("apply|" + p[0] + "|" + p[1] + "|" + result);
                }
                catch (Exception e) { Row("apply-failed|" + p[0] + "|" + p[1] + "|" + (e.InnerException ?? e).Message); }
                finally
                {
                    Replaying = false;
                    try { randomState.SetValue(null, own, null); } catch (Exception e) { log(Tag + "|apply-failed|restore|" + e.Message); }
                }
            }
        }

        void Row(string text)
        {
            if (++rows <= 300) log(Tag + "|" + text);
        }

        public static object Combat() => Get(battleTechGame.GetValue(null, null), "Combat");

        public static bool CoopCombat()
        {
            object net = Get(Combat(), "NetworkGameInstance");
            return net != null && Get(net, "IsCoopCombat") is bool coop && coop;
        }

        public static bool Authority(object actor) => actor != null && (bool)isAuthority.Invoke(null, new[] { actor });

        // Inside a replicated invocation's run every peer makes the same change itself, so nothing is a one-peer decision there.
        public static bool InInvocation() => currentInvocation.GetValue(null) != null;

        public static string RandomState()
        {
            object s = randomState.GetValue(null, null);
            var parts = new string[4];
            for (int i = 0; i < 4; i++) parts[i] = ((int)stateFields[i].GetValue(s)).ToString(CultureInfo.InvariantCulture);
            return string.Join(",", parts);
        }

        static void SetRandomState(string text)
        {
            string[] parts = text.Split(',');
            if (parts.Length != 4) throw new FormatException("random state " + text);
            object s = Activator.CreateInstance(stateType);
            for (int i = 0; i < 4; i++) stateFields[i].SetValue(s, int.Parse(parts[i], CultureInfo.InvariantCulture));
            randomState.SetValue(null, s, null);
        }

        public static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);
        public static float PF(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        public static int PI(string s) => int.Parse(s, CultureInfo.InvariantCulture);
        public static string B(bool b) => b ? "1" : "0";

        static bool Safe(string s) => s != null && s.IndexOfAny(new[] { ';', '/', '|' }) < 0;

        public static object Get(object o, string name)
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
