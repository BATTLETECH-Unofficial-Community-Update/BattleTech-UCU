// StrategicOperations drop-pod landings of units an AI command ability spawned (BADrop and siblings) on a co-op host and its guests
// (post-4B housekeeping, work item so-ai-badrop-coop; R-fix16/R-fix17: the squads spawned equal on both peers, then the MECHS compare named
// SUPPORT.0/.1 at the next report, because the landing is a per-peer frame-time coroutine).
// The path, from the decompile (StrategicOperations.dll): CustomSpawner.OnBeaconDepsLoaded (Classes.cs:649) creates the unit, teleports it to y=-1000
// and writes it into ONE shared DropPodSpawner component (Unit, DropPodPosition, DropPodRotation, Classes.cs:697 ff), then starts
// StartDropPodAnimation (DropPodUtils.cs): WaitForSeconds 0.5, 1, 3, then TeleportUnitToSpawnPoint() (:284) lands the shared Unit at the shared
// DropPodPosition, WaitForSeconds 2, then Utils.DeployEvasion(shared Unit) (:297). Two drops less than ~4.5 s apart overwrite the shared fields, so
// the first coroutine lands the second unit and the first stays at y=-1000 (SO's own bug, also in single player: recorded in
// docs/mods/strategicoperations.md, not changed here). Each peer runs the coroutine on its own clock, so landing, the evasion pips and which unit
// lands differ between peers at a compare.
// This adapter, only when StrategicOperations is loaded, only in a co-op combat and only for units of an AITeam (player command drops keep
// SO's own flow on every peer):
//  - the host's own landing runs as SO wrote it; its postfix on TeleportUnitToSpawnPoint records, in CoopDecisionChannel, the unit that
//    TeleportActor actually landed (the shared Unit at that moment), its final position and rotation, and the drop it came from (the n-th
//    StartDropPodAnimation call belongs to the n-th landing call); when the landing call belongs to a drop whose unit is not the one landed, that
//    unit was left in the pod by SO's overwrite and a `left` record says so; its postfix on DeployEvasion records the pips. The `land` record is
//    made when the invocation carrying it is captured (CoopDecisionChannel.BeforeCapture, or just before the evasion record), and it carries the
//    landing pose AND the unit's pose at that moment: an AI unit can be sent to move before its pod lands, and the guest then runs that move before
//    it receives the record (R-fix19c, SUPPORT.1: the record's teleport put the guest unit back on the landing point after its own copy of the move
//    had finished);
//  - a guest suppresses its own TeleportUnitToSpawnPoint and DeployEvasion for such units (no landing from a guest's timer, no double
//    teleport) and applies the host's record right before the invocation it rode: TeleportActor to the recorded position, the recorded rotation,
//    the fade-in, the visibility, then (when the host's pose at capture differs from the landing pose) the carried pose, and later the evasion
//    pips. The host does not run SO's Utils.DeployEvasion when SO's coroutine does: it queues the call (row deferred-evasion|<unit>|queued) and runs it at the next
//    invocation capture (BeforeCapture, after the land record), deciding the evasion record there, so the host's pips and the guest's apply (right before that same
//    invocation) change at the same invocation boundary. R-fix23: UCU's N8 digest does not honour the compare hold and caught the unit in the gap between the host's
//    deploy and the carrying invocation (host 8, guest 2); R-fix24: CoopDecisionChannel.SendNow does not close it, the host holds a SendNow record for the next order
//    point too. The deferral never runs the deploy earlier than SO would, only at the first capture at or after it. A unit the host never landed stays at -1000 on the guests too;
//  - compare hold: from its creation on a peer until the host's `evasion` or `left` record is sent (host) or applied (guest), the unit is left out of
//    the MECHS compare data that peer gathers (MechStatsReporter.OnTrigger postfix: its StatsCompare entry is removed before the hash is made, so
//    the CMP-9 hash and the N8 unit hashes both lack it). The release is the record, never a timer. The registry belongs to the combat object, so a
//    guest that loads a snapshot mid-drop starts with no hold while the host still holds (R-fix25: the guest's MECHS data had SUPPORT.0/.1, the host's
//    lacked them, compare 117 named them and cost a resync): the host's hold set therefore rides the snapshot (CoopInvocationSideData source
//    `so-drop-hold`, capture(null) / apply(null)) and the guest holds the same units from its load (row hold|<unit>|carried) until the record releases them;
//  - a land record applies only to a unit still in the pod (y <= -500) on that peer: a snapshot taken after the host landed the unit already has it
//    (row apply|land|skipped|already-landed), and a record for a unit this combat object never saw drop (the guest loaded after StartDropPodAnimation, with
//    no carried hold) is applied all the same and logged apply|land|untracked|<unit> (evasion: apply|evasion|untracked|<unit>);
// Installed at the first combat (fault M); single player is untouched.
// Rows (bootstrap log, both peers; every row this file writes ends with |t=<epoch ms>, the channel's own installed and count rows do not):
// so-drop-coop|installed|..., |hold|<unit>|created, |landed|<unit>|from=..,pos=.. (host, the landing call), |release|<unit>|<evasion|left>, channel rows
// decide/apply of kinds land (from, landing position and rotation, carried position and rotation), left, evasion, |suppressed|<unit>|<landing|evasion>
// with pos= on a guest, |apply|land|moved-after-land|<unit>|landing=..|carried=.. on a guest when the carried pose differs from the landing pose,
// |hold-carry|capture|n=.. (host) and |hold-carry|apply|n=.. / |hold|<unit>|carried (guest) for the snapshot's hold set, |apply|land|untracked|<unit>,
// |apply|evasion|untracked|<unit>, |apply|land|skipped|already-landed|<unit>|pos=...
// A failure is |FAILED| or |apply-failed| and a Unity Debug.LogError.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class StrategicOperationsCoopDropPod
    {
        const string Tag = "so-drop-coop";
        static Action<string> log;
        static CoopDecisionChannel channel;
        static PropertyInfo spUnit;
        static FieldInfo spPos;
        static MethodInfo debugError;
        static Type vector3, quaternion;

        sealed class Drop { public object Unit; public string Guid; }
        // A landing the host has made and not yet recorded: the record is made when it is captured (see the header).
        sealed class Landing { public object Unit; public string Guid, From, Pos, Rot, At; }
        static readonly List<Landing> pendingLands = new List<Landing>();
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static readonly Queue<Drop> drops = new Queue<Drop>();
        static readonly Dictionary<string, string> held = new Dictionary<string, string>();
        static readonly List<string> awaitingSend = new List<string>();
        static readonly List<object> pendingDeploy = new List<object>();   // host: AI units whose DeployEvasion waits for the next invocation capture
        static MethodInfo deployMethod, findActor;
        static bool inDeferred;
        static readonly Dictionary<string, bool> releasedLogged = new Dictionary<string, bool>(), suppressedLogged = new Dictionary<string, bool>();
        static object registryCombat;
        static int reports;
        // Per dropped unit: 0 = not yet seen in a report, 1 = first report that left it out logged, 2 = first report that included it logged.
        static readonly Dictionary<string, int> tracked = new Dictionary<string, int>();

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("StrategicOperations", a => ModAssemblyHook.AtFirstCombat(() => Apply(a), logLine, Tag), logLine, Tag);
        }

        static void Apply(Assembly so)
        {
            try
            {
                if (!CoopDecisionChannel.Init(log, Tag)) return;
                const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                Type spawner = so.GetType("StrategicOperations.Framework.DropPodUtils+DropPodSpawner", false);
                Type utils = so.GetType("StrategicOperations.Framework.Utils", false);
                Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor");
                vector3 = ModAssemblyHook.GameType("UnityEngine.Vector3");
                quaternion = ModAssemblyHook.GameType("UnityEngine.Quaternion");
                debugError = ModAssemblyHook.GameType("UnityEngine.Debug")?.GetMethod("LogError", new[] { typeof(object) });
                spUnit = spawner?.GetProperty("Unit", inst);
                spPos = spawner?.GetField("DropPodPosition", inst);
                MethodInfo teleport = spawner?.GetMethod("TeleportUnitToSpawnPoint", inst, null, Type.EmptyTypes, null);
                MethodInfo start = spawner?.GetMethod("StartDropPodAnimation", inst, null, new[] { typeof(float) }, null);
                MethodInfo deploy = utils?.GetMethod("DeployEvasion", st, null, new[] { actor }, null);
                deployMethod = deploy;
                MethodInfo stats = ModAssemblyHook.GameType("BattleTech.MechStatsReporter")?.GetMethod("OnTrigger", inst);
                if (spUnit == null || spPos == null || teleport == null || start == null || deploy == null || stats == null || vector3 == null || quaternion == null)
                {
                    Error("skipped|shape changed DropPodSpawner.Unit=" + (spUnit != null) + " .DropPodPosition=" + (spPos != null) + " .TeleportUnitToSpawnPoint=" + (teleport != null)
                        + " .StartDropPodAnimation=" + (start != null) + " Utils.DeployEvasion=" + (deploy != null) + " MechStatsReporter.OnTrigger=" + (stats != null));
                    return;
                }
                channel = new CoopDecisionChannel("so-drop", Tag, log);
                channel.On("land", ReplayLand);
                channel.On("left", ReplayLeft);
                channel.On("evasion", ReplayEvasion);
                channel.BeforeCapture = ReleaseSent;
                var h = new Harmony("battletech.port.so-drop-coop");
                CoopDecisionChannel.Patch(h, start, prefix: Mine(nameof(StartPrefix), Priority.First));
                CoopDecisionChannel.Patch(h, teleport, prefix: Mine(nameof(TeleportPrefix), Priority.First), postfix: Mine(nameof(TeleportPostfix), Priority.Last));
                CoopDecisionChannel.Patch(h, deploy, prefix: Mine(nameof(DeployPrefix), Priority.First), postfix: Mine(nameof(DeployPostfix), Priority.Last));
                CoopDecisionChannel.Patch(h, stats, postfix: Mine(nameof(StatsPostfix), Priority.Last));
                channel.Install("AI drop-pod landing and deploy evasion recorded on the host and applied on guests; a dropped unit is held out of the MECHS compare until then");
                InstallHoldCarry();
            }
            catch (Exception e) { Error("FAILED|install|" + ModStateCarry.Inner(e)); }
        }

        static HarmonyMethod Mine(string name, int priority)
            => new HarmonyMethod(typeof(StrategicOperationsCoopDropPod).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };

        static string Now() => ((long)(DateTime.UtcNow - Epoch).TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

        static void Row(string text) => log(Tag + "|" + text + "|t=" + Now());

        static void Error(string row)
        {
            Row(row);
            try { debugError?.Invoke(null, new object[] { "[" + Tag + "] " + row }); } catch { }
        }

        static bool Coop() => CoopDecisionChannel.CoopCombat();

        static bool IsAi(object unit) => unit != null && CoopDecisionChannel.Get(unit, "team")?.GetType().Name == "AITeam";

        static string GuidOf(object unit) => CoopDecisionChannel.Get(unit, "GUID") as string;

        // The registry belongs to the combat object: a guest that loads a snapshot starts it empty.
        static void Fresh()
        {
            object c = CoopDecisionChannel.Combat();
            if (ReferenceEquals(c, registryCombat)) return;
            registryCombat = c;
            drops.Clear(); pendingLands.Clear(); pendingDeploy.Clear(); held.Clear(); awaitingSend.Clear(); releasedLogged.Clear(); suppressedLogged.Clear(); tracked.Clear(); reports = 0;
        }

        static void Hold(string guid, string why)
        {
            if (held.ContainsKey(guid)) return;
            held[guid] = why;
            Row("hold|" + guid + "|" + why);
        }

        static void Release(string guid, string why)
        {
            if (!held.Remove(guid)) return;
            Row("release|" + guid + "|" + why + "|afterReport=" + reports);
        }

        // The host's hold set for a guest that loads a snapshot (CoopInvocationSideData source with capture(null) / apply(null), as ModStateCarry registers its own).
        static void InstallHoldCarry()
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo reg = ModAssemblyHook.GameType("BattleTech.CoopInvocationSideData")?.GetMethod("Register", st);
            findActor = ModAssemblyHook.GameType("BattleTech.CombatGameState")?.GetMethod("FindActorByGUID", new[] { typeof(string) });
            if (reg == null || findActor == null) { Error("FAILED|hold-carry|shape changed CoopInvocationSideData.Register=" + (reg != null) + " CombatGameState.FindActorByGUID=" + (findActor != null)); return; }
            reg.Invoke(null, new object[] { "so-drop-hold", new Func<object, string>(CaptureHolds), new Action<object, string>(ApplyHolds) });
            Row("installed|hold-carry|the host's hold set is sent to a peer that loads a co-op snapshot");
        }

        static string CaptureHolds(object invocation)
        {
            if (invocation != null || held.Count == 0) return null;
            try
            {
                string list = string.Join(",", new List<string>(held.Keys).ToArray());
                Row("hold-carry|capture|n=" + held.Count + "|" + list);
                return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(list));
            }
            catch (Exception e) { Error("FAILED|hold-carry-capture|" + e.Message); return null; }
        }

        static void ApplyHolds(object invocation, string data)
        {
            if (invocation != null || string.IsNullOrEmpty(data)) return;
            try
            {
                Fresh();
                object combat = CoopDecisionChannel.Combat();
                string[] guids = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data)).Split(',');
                Row("hold-carry|apply|n=" + guids.Length);
                foreach (string guid in guids)
                {
                    if (combat == null || findActor.Invoke(combat, new object[] { guid }) == null) { Row("hold-carry|unmapped|" + guid); continue; }
                    if (!tracked.ContainsKey(guid)) tracked[guid] = 0;
                    Hold(guid, "carried");
                }
            }
            catch (Exception e) { Error("FAILED|hold-carry-apply|" + e.Message); }
        }

        // True while the unit is still at the pod point SO leaves it at (y = -1000).
        static bool InPod(object unit)
        {
            object pos = CoopDecisionChannel.Get(unit, "CurrentPosition");
            return pos != null && CoopDecisionChannel.Get(pos, "y") is float y && y <= -500f;
        }

        // StartDropPodAnimation: the caller has just written the new unit into the shared component.
        static void StartPrefix(object __instance)
        {
            try
            {
                if (!Coop()) return;
                Fresh();
                object unit = spUnit.GetValue(__instance, null);
                if (!IsAi(unit)) return;
                string guid = GuidOf(unit);
                drops.Enqueue(new Drop { Unit = unit, Guid = guid });
                if (!tracked.ContainsKey(guid)) tracked[guid] = 0;
                Hold(guid, "created");
            }
            catch (Exception e) { Error("FAILED|start|" + e.Message); }
        }

        // TeleportUnitToSpawnPoint (DropPodUtils.cs:284): the host lands as SO does and records it; a guest never lands from its own timer.
        static bool TeleportPrefix(object __instance, ref object __state)
        {
            __state = null;
            try
            {
                if (!Coop()) return true;
                Fresh();
                object unit = spUnit.GetValue(__instance, null);
                if (!IsAi(unit)) return true;
                if (CoopDecisionChannel.Authority(unit))
                {
                    __state = drops.Count > 0 ? drops.Dequeue() : new Drop();
                    return true;
                }
                string guid = GuidOf(unit);
                if (!suppressedLogged.ContainsKey(guid + "|landing")) { suppressedLogged[guid + "|landing"] = true; Row("suppressed|" + guid + "|landing|pos=" + Vec(CoopDecisionChannel.Get(unit, "CurrentPosition"))); }
                return false;
            }
            catch (Exception e) { Error("FAILED|teleport-prefix|" + e.Message); return true; }
        }

        static void TeleportPostfix(object __instance, object __state)
        {
            if (!(__state is Drop d)) return;
            try
            {
                object landed = spUnit.GetValue(__instance, null);
                string guid = GuidOf(landed), from = d.Guid ?? "~";
                string pos = Vec(CoopDecisionChannel.Get(landed, "CurrentPosition")), rot = Quat(CoopDecisionChannel.Get(landed, "CurrentRotation"));
                pendingLands.Add(new Landing { Unit = landed, Guid = guid, From = from, Pos = pos, Rot = rot, At = Now() });
                Row("landed|" + guid + "|from=" + from + ",pos=" + pos);
                if (d.Unit != null && !ReferenceEquals(d.Unit, landed))
                {
                    string left = d.Guid;
                    channel.Decide("left", d.Unit, "overwritten-by=" + guid + ",t=" + Now(), "~", guid);
                    awaitingSend.Add(left);
                }
            }
            catch (Exception e) { Error("FAILED|land-send|" + e.Message); }
        }

        // DeployEvasion (DropPodUtils.cs:297): the last step of the coroutine; the host's pips are the truth.
        static bool DeployPrefix(object actor)
        {
            try
            {
                if (!Coop() || !IsAi(actor)) return true;
                string guid = GuidOf(actor);
                if (CoopDecisionChannel.Authority(actor))
                {
                    if (inDeferred) return true;   // our own run at the capture point
                    Fresh();
                    pendingDeploy.Add(actor);
                    Row("deferred-evasion|" + guid + "|queued");
                    return false;
                }
                if (!suppressedLogged.ContainsKey(guid + "|evasion")) { suppressedLogged[guid + "|evasion"] = true; Row("suppressed|" + guid + "|evasion"); }
                return false;
            }
            catch (Exception e) { Error("FAILED|deploy-prefix|" + e.Message); return true; }
        }

        static void DeployPostfix(object actor)
        {
            try
            {
                if (!inDeferred || !Coop() || !IsAi(actor) || !CoopDecisionChannel.Authority(actor)) return;
                string cur = Convert.ToString(CoopDecisionChannel.Get(actor, "EvasivePipsCurrent"), CultureInfo.InvariantCulture);
                string tot = Convert.ToString(CoopDecisionChannel.Get(actor, "EvasivePipsTotal"), CultureInfo.InvariantCulture);
                RecordLandings();   // the records are applied in order: the landing goes before the evasion
                channel.Decide("evasion", actor, "pips=" + cur + "/" + tot + ",t=" + Now(), "~", cur, tot);
                awaitingSend.Add(GuidOf(actor));
                Row("deferred-evasion|" + GuidOf(actor) + "|pips=" + cur + "/" + tot + "|captured");
            }
            catch (Exception e) { Error("FAILED|evasion-send|" + e.Message); }
        }

        // Host: the records are in the invocation being sent, so the hold on those units ends with it (a guest ends it when it applies them).
        // Host: run the queued DeployEvasion calls at this invocation capture (the postfix decides the record, which goes out with this invocation).
        static void RunDeferredEvasions()
        {
            if (pendingDeploy.Count == 0 || deployMethod == null) return;
            foreach (object actor in pendingDeploy.ToArray())
            {
                try
                {
                    inDeferred = true;
                    deployMethod.Invoke(null, new object[] { actor });
                }
                catch (Exception e) { Error("FAILED|deferred-evasion|" + GuidOf(actor) + "|" + (e.InnerException ?? e).Message); }
                finally { inDeferred = false; }
            }
            pendingDeploy.Clear();
        }

        static void ReleaseSent(object invocation)
        {
            RecordLandings();
            RunDeferredEvasions();
            if (awaitingSend.Count == 0) return;
            foreach (string guid in awaitingSend.ToArray()) Release(guid, "sent");
            awaitingSend.Clear();
        }

        // Host: the landings made since the last capture become `land` records now, each with the pose the unit has at this moment.
        static void RecordLandings()
        {
            if (pendingLands.Count == 0) return;
            foreach (Landing l in pendingLands.ToArray())
            {
                try
                {
                    string nowPos = Vec(CoopDecisionChannel.Get(l.Unit, "CurrentPosition")), nowRot = Quat(CoopDecisionChannel.Get(l.Unit, "CurrentRotation"));
                    channel.Decide("land", l.Unit, "from=" + l.From + ",pos=" + l.Pos + ",carried=" + nowPos + ",landedAt=" + l.At + ",t=" + Now(), "~", l.From, l.Pos, l.Rot, nowPos, nowRot);
                }
                catch (Exception e) { Error("FAILED|land-send|" + e.Message); }
            }
            pendingLands.Clear();
        }

        // fields: from drop, landing position, landing rotation, position and rotation at capture (absent in a record from an older build).
        static string ReplayLand(object unit, string[] f)
        {
            try
            {
                Fresh();
                string who = GuidOf(unit);
                if (!InPod(unit))
                {
                    Row("apply|land|skipped|already-landed|" + who + "|pos=" + Vec(CoopDecisionChannel.Get(unit, "CurrentPosition")));
                    return "skipped=already-landed,t=" + Now();
                }
                if (!tracked.ContainsKey(who)) Row("apply|land|untracked|" + who + "|pos=" + f[1]);
                object pos = ParseVec(f[1]);
                Call(unit, "TeleportActor", pos);
                SetRotation(unit, pos, ParseQuat(f[2]));
                object rep = CoopDecisionChannel.Get(unit, "GameRep");
                rep?.GetType().GetMethod("FadeIn", new[] { typeof(float) })?.Invoke(rep, new object[] { 1f });
                MethodInfo vis = unit.GetType().GetMethod("OnPlayerVisibilityChanged");
                vis?.Invoke(unit, new[] { Enum.ToObject(vis.GetParameters()[0].ParameterType, 9) });
                string final = f[1];
                if (f.Length >= 5 && (f[3] != f[1] || f[4] != f[2]))
                {
                    // The host's unit had already left the landing pose when this record was sent: the carried pose is the result.
                    object carried = ParseVec(f[3]);
                    if (f[3] != f[1]) Call(unit, "TeleportActor", carried);
                    SetRotation(unit, carried, ParseQuat(f[4]));
                    final = f[3];
                    Row("apply|land|moved-after-land|" + GuidOf(unit) + "|landing=" + f[1] + "|carried=" + f[3]);
                }
                return "from=" + f[0] + ",pos=" + final + ",t=" + Now();
            }
            catch (Exception e) { Error("apply-failed|land|" + GuidOf(unit) + "|" + (e.InnerException ?? e).Message); throw; }
        }

        static void SetRotation(object unit, object pos, object rot)
        {
            if (Quat(CoopDecisionChannel.Get(unit, "CurrentRotation")) == Quat(rot)) return;
            MethodInfo up = unit.GetType().GetMethod("OnPositionUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            up.Invoke(unit, new object[] { pos, rot, -1, true, null, false });
        }

        static string ReplayLeft(object unit, string[] f)
        {
            Fresh();
            Release(GuidOf(unit), "left");
            return "overwritten-by=" + f[0] + ",t=" + Now();
        }

        static string ReplayEvasion(object unit, string[] f)
        {
            try
            {
                Fresh();
                if (!tracked.ContainsKey(GuidOf(unit))) Row("apply|evasion|untracked|" + GuidOf(unit));
                int cur = CoopDecisionChannel.PI(f[0]), tot = CoopDecisionChannel.PI(f[1]);
                Type t = unit.GetType();
                for (Type b = t; b != null; b = b.BaseType)
                {
                    PropertyInfo pc = b.GetProperty("EvasivePipsCurrent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (pc != null) pc.SetValue(unit, cur, null);
                    PropertyInfo pt = b.GetProperty("EvasivePipsTotal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (pt != null) pt.SetValue(unit, tot, null);
                }
                try
                {
                    object combat = CoopDecisionChannel.Combat();
                    object mc = CoopDecisionChannel.Get(combat, "MessageCenter");
                    Type msg = ModAssemblyHook.GameType("BattleTech.EvasiveChangedMessage");
                    object m = msg?.GetConstructor(new[] { typeof(string), typeof(int) })?.Invoke(new object[] { GuidOf(unit), cur });
                    if (m != null) mc?.GetType().GetMethod("PublishMessage")?.Invoke(mc, new[] { m });
                }
                catch (Exception e) { Row("evasion-ui-skipped|" + GuidOf(unit) + "|" + e.Message); }
                Release(GuidOf(unit), "evasion");
                return "pips=" + f[0] + "/" + f[1] + ",t=" + Now();
            }
            catch (Exception e) { Error("apply-failed|evasion|" + GuidOf(unit) + "|" + (e.InnerException ?? e).Message); throw; }
        }

        // MechStatsReporter.OnTrigger runs before the compare data is hashed: a held unit's entry is left out of it.
        static void StatsPostfix(object __result)
        {
            try
            {
                if (__result == null || !Coop()) return;
                Fresh();
                reports++;
                if (tracked.Count == 0 && held.Count == 0) return;
                if (!(CoopDecisionChannel.Get(__result, "data") is IDictionary data)) return;
                object td = CoopDecisionChannel.Get(CoopDecisionChannel.Combat(), "TurnDirector");
                var present = new Dictionary<string, object>();
                foreach (object key in data.Keys)
                {
                    string k = key as string;
                    int bar = k == null ? -1 : k.LastIndexOf('|');
                    if (bar >= 0) present[k.Substring(bar + 1)] = key;
                }
                // Rows for the scoring check: the report index each dropped unit is first left out of and first included in, per peer.
                foreach (string guid in new List<string>(tracked.Keys))
                {
                    bool isHeld = held.ContainsKey(guid), inData = present.ContainsKey(guid);
                    int state = tracked[guid];
                    if (isHeld && state == 0) { tracked[guid] = 1; Row("report|n=" + reports + "|r=" + CoopDecisionChannel.Get(td, "CurrentRound") + "|p=" + CoopDecisionChannel.Get(td, "CurrentPhase") + "|" + guid + "|excluded"); }
                    else if (!isHeld && inData && state < 2) { tracked[guid] = 2; Row("report|n=" + reports + "|r=" + CoopDecisionChannel.Get(td, "CurrentRound") + "|p=" + CoopDecisionChannel.Get(td, "CurrentPhase") + "|" + guid + "|included"); }
                }
                foreach (string guid in held.Keys)
                    if (present.TryGetValue(guid, out object key)) data.Remove(key);
            }
            catch (Exception e) { Error("FAILED|hold|" + e.Message); }
        }

        static void Call(object o, string name, params object[] args)
        {
            foreach (MethodInfo m in o.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (m.Name == name && m.GetParameters().Length == args.Length) { m.Invoke(o, args); return; }
            throw new MissingMethodException(o.GetType().Name + "." + name);
        }

        static string F(object f) => f is float x ? CoopDecisionChannel.F(x) : "0";

        static string Vec(object v)
            => v == null ? "0_0_0" : F(CoopDecisionChannel.Get(v, "x")) + "_" + F(CoopDecisionChannel.Get(v, "y")) + "_" + F(CoopDecisionChannel.Get(v, "z"));

        static string Quat(object q)
            => q == null ? "0_0_0_1" : F(CoopDecisionChannel.Get(q, "x")) + "_" + F(CoopDecisionChannel.Get(q, "y")) + "_" + F(CoopDecisionChannel.Get(q, "z")) + "_" + F(CoopDecisionChannel.Get(q, "w"));

        static object ParseVec(string s)
        {
            string[] p = s.Split('_');
            return Activator.CreateInstance(vector3, new object[] { CoopDecisionChannel.PF(p[0]), CoopDecisionChannel.PF(p[1]), CoopDecisionChannel.PF(p[2]) });
        }

        static object ParseQuat(string s)
        {
            string[] p = s.Split('_');
            return Activator.CreateInstance(quaternion, new object[] { CoopDecisionChannel.PF(p[0]), CoopDecisionChannel.PF(p[1]), CoopDecisionChannel.PF(p[2]), CoopDecisionChannel.PF(p[3]) });
        }
    }
}
