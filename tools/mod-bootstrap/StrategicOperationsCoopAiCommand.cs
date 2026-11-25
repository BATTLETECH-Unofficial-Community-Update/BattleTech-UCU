// StrategicOperations AI command abilities (BADrop and the other AbilityDefCMD_* the AI uses: spawn-turret / battle-armour / vehicle drops and
// strafes) on a co-op host and its guests (post-4B housekeeping, work item so-ai-badrop-coop; R-fix15 compare 110: the host spawned
// <team>.SUPPORT.0/.1 alone, the guest reported peer-has-no-such-mech and the pair hit the resync limit).
// The path, from the decompile (StrategicOperations.dll): AITeam.makeInvocationFromOrders (AI_Patches.cs, its prefix) runs only where
// AITeam.ThinksOnThisMachine, the host (AITeam.cs:247, 277). When the behaviour tree has queued an AiCmds entry it stores
// CmdInvocationParams under the key "AI_CMD_ID_<unit>_<ability>_<posA>_<posB>" in ModState.StoredCmdParams (actor resource from
// AI_Utils.AssignRandomSpawnAsset, a random pick, strafe waves, pilot override), then calls Ability.Activate(unit, posA, posB) directly and returns
// a ReserveActorInvocation. Only the ReserveActorInvocation is sent: the Activate (and so the spawn, flares, cooldown, creator effects) ran on the
// host alone. Ability.Activate -> Utils.ActivateSpawnTurretFromActor queues a DeferredInvokeSpawns entry (it needs the StoredCmdParams entry or
// it returns), which TurnDirector.IncrementActiveTurnActor drains when the active actor is the local player team; the unit GUIDs
// (<team>.SUPPORT.<n>) come from the spawn (StrategicOperationsCoopDeterminism seeds the GUID counters, Mod.Random reads and the
// GenerateAIStrategicAbilities draws on every peer).
// The route chosen is CoopDecisionChannel (the swarm decision pattern, StrategicOperationsCoopSwarmDecision), not a CommandSync extension:
// CommandSync carries the abilities' counters at a snapshot load and refunds dropped uses; the AI's activation is one decision made by one
// authority (the host) and replayed at once, which is what the channel does (record rides the host's next invocation, here the
// ReserveActorInvocation; the guest applies it right before that invocation with UnityEngine.Random.state set to the host's).
// The host stays the single source of truth: the actor resource, strafe waves and pilot override the host drew travel with the record, so the
// guest draws nothing and the GUIDs come from the same deterministic counters in the same order as the host's spawn.
// No double spawn: the guest's AI never thinks (ThinksOnThisMachine is false there), so SO's prefix on makeInvocationFromOrders never calls
// Activate on a guest, and the guest's only Activate is this replay. As a guard, an Activate(unit, posA, posB) on an AI unit this peer is not the
// authority for, outside a replay, is blocked and logged as an ERROR.
// Installed at the first combat (fault M), only when StrategicOperations is loaded; every hook acts only in a co-op combat.
// Rows (bootstrap log): so-ai-cmd-coop|installed|..., host |send|<ability>|<unit>|a=<x_y_z>|b=<x_y_z>|resource=<id>|waves=<n>, guest |apply|ability|<unit>|<result>
// (channel rows decide/apply carry the same text), on both peers |spawned|<unit guid>|team=<team guid> for each unit added to a team after an
// activation (the deferred spawn), |count|decide=<n>|apply=<n> at combat end. A failure is |FAILED| or |apply-failed| and is also a Unity
// Debug.LogError (an ERROR in the player log).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class StrategicOperationsCoopAiCommand
    {
        const string Tag = "so-ai-cmd-coop";
        static Action<string> log;
        static CoopDecisionChannel channel;
        static MethodInfo activate, think, keyOf, debugError;
        static FieldInfo storedParams, pResource, pWaves, pPilot;
        static Type paramsType, vector3;
        static bool aiScope;
        static int depth, spawnRows;
        static int activations;
        static object activationsCombat;

        sealed class Rec { public object Creator, A, B; public string State, Def, Resource, Pilot; public int Waves; public bool HasParams; }

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
                Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor"), ability = ModAssemblyHook.GameType("BattleTech.Ability");
                Type order = ModAssemblyHook.GameType("OrderInfo"), team = ModAssemblyHook.GameType("BattleTech.Team");
                vector3 = ModAssemblyHook.GameType("UnityEngine.Vector3");
                debugError = ModAssemblyHook.GameType("UnityEngine.Debug")?.GetMethod("LogError", new[] { typeof(object) });
                paramsType = so.GetType("StrategicOperations.Framework.Classes+CmdInvocationParams", false);
                storedParams = so.GetType("StrategicOperations.Framework.ModState", false)?.GetField("StoredCmdParams", st);
                keyOf = so.GetType("StrategicOperations.Framework.Utils", false)?.GetMethod("Generate2PtCMDQuasiGUID", st);
                pResource = paramsType?.GetField("ActorResource");
                pWaves = paramsType?.GetField("StrafeWaves");
                pPilot = paramsType?.GetField("PilotOverride");
                activate = ability?.GetMethod("Activate", inst, null, new[] { actor, vector3, vector3 }, null);
                think = ModAssemblyHook.GameType("AITeam")?.GetMethod("makeInvocationFromOrders", inst, null, new[] { actor, order }, null);
                MethodInfo addUnit = team?.GetMethod("AddUnit", inst, null, new[] { actor }, null);
                if (paramsType == null || storedParams == null || keyOf == null || pResource == null || pWaves == null || pPilot == null || activate == null
                    || think == null || addUnit == null || vector3 == null)
                {
                    Error("skipped|shape changed CmdInvocationParams=" + (paramsType != null) + " ModState.StoredCmdParams=" + (storedParams != null)
                        + " Utils.Generate2PtCMDQuasiGUID=" + (keyOf != null) + " params fields=" + (pResource != null && pWaves != null && pPilot != null)
                        + " Ability.Activate(AbstractActor,Vector3,Vector3)=" + (activate != null) + " AITeam.makeInvocationFromOrders=" + (think != null)
                        + " Team.AddUnit=" + (addUnit != null));
                    return;
                }
                channel = new CoopDecisionChannel("so-ai-cmd", Tag, log);
                channel.On("ability", Replay);
                var h = new Harmony("battletech.port.so-ai-cmd-coop");
                CoopDecisionChannel.Patch(h, think, prefix: Mine(nameof(ThinkPrefix), Priority.First), finalizer: Mine(nameof(ThinkFinalizer), Priority.Last));
                CoopDecisionChannel.Patch(h, activate, prefix: Mine(nameof(ActivatePrefix), Priority.First), finalizer: Mine(nameof(ActivateFinalizer), Priority.Last));
                CoopDecisionChannel.Patch(h, addUnit, postfix: Mine(nameof(AddUnitPostfix), Priority.Last));
                channel.Install("AI Ability.Activate(unit, posA, posB) replayed on the guests with the host's drawn actor resource, waves and pilot override; guest-local activation blocked");
            }
            catch (Exception e) { Error("FAILED|install|" + ModStateCarry.Inner(e)); }
        }

        static HarmonyMethod Mine(string name, int priority)
            => new HarmonyMethod(typeof(StrategicOperationsCoopAiCommand).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };

        // A failure row: the bootstrap log and an ERROR in the player log.
        static void Error(string row)
        {
            string line = Tag + "|" + row;
            log(line);
            try { debugError?.Invoke(null, new object[] { "[" + Tag + "] " + row }); } catch { }
        }

        static void ThinkPrefix(object unit)
        {
            aiScope = false;
            try { aiScope = CoopDecisionChannel.CoopCombat() && CoopDecisionChannel.Authority(unit); }
            catch (Exception e) { Error("FAILED|think|" + e.Message); }
        }

        static void ThinkFinalizer() { aiScope = false; depth = 0; }

        static bool ActivatePrefix(object __instance, object[] __args, ref object __state)
        {
            __state = null;
            try
            {
                if (channel.Replaying) return true;
                object creator = __args[0];
                if (aiScope && depth == 0)
                {
                    depth++;
                    var r = new Rec { Creator = creator, A = __args[1], B = __args[2], State = CoopDecisionChannel.RandomState(),
                        Def = CoopDecisionChannel.Get(CoopDecisionChannel.Get(__instance, "Def"), "Id") as string };
                    string guid = CoopDecisionChannel.Get(creator, "GUID") as string;
                    object stored = (storedParams.GetValue(null) as IDictionary)?[keyOf.Invoke(null, new[] { __instance, guid, r.A, r.B })];
                    if (stored != null)
                    {
                        r.HasParams = true;
                        r.Resource = pResource.GetValue(stored) as string;
                        r.Waves = (int)pWaves.GetValue(stored);
                        r.Pilot = pPilot.GetValue(stored) as string;
                    }
                    __state = r;
                    return true;
                }
                // A guest never thinks for an AI unit: a local Activate on one would spawn a second copy. Blocked.
                if (!aiScope && CoopDecisionChannel.CoopCombat() && !CoopDecisionChannel.Authority(creator)
                    && CoopDecisionChannel.Get(creator, "team")?.GetType().Name == "AITeam")
                {
                    Error("FAILED|local-activate|" + CoopDecisionChannel.Get(CoopDecisionChannel.Get(__instance, "Def"), "Id") + "|" + CoopDecisionChannel.Get(creator, "GUID")
                        + "|an AI unit's command ability was activated outside the host's AI; blocked");
                    return false;
                }
            }
            catch (Exception e) { Error("FAILED|activate-prefix|" + e.Message); }
            return true;
        }

        static void ActivateFinalizer(object __state, Exception __exception)
        {
            if (!(__state is Rec r)) return;
            depth--;
            string guid = CoopDecisionChannel.Get(r.Creator, "GUID") as string;
            if (__exception != null) { Error("FAILED|host-activate|" + r.Def + "|" + guid + "|" + __exception.GetType().Name + ": " + __exception.Message + " (not sent)"); return; }
            try
            {
                string res = Safe(r.Resource), pilot = Safe(r.Pilot), a = Vec(r.A), b = Vec(r.B);
                channel.Decide("ability", r.Creator, r.Def + ",resource=" + res + ",waves=" + r.Waves, r.State,
                    r.Def, a, b, res, r.Waves.ToString(System.Globalization.CultureInfo.InvariantCulture), pilot, CoopDecisionChannel.B(r.HasParams));
                Note();
                log(Tag + "|send|" + r.Def + "|" + guid + "|a=" + a + "|b=" + b + "|resource=" + res + "|waves=" + r.Waves + "|params=" + CoopDecisionChannel.B(r.HasParams));
            }
            catch (Exception e) { Error("FAILED|send|" + r.Def + "|" + guid + "|" + e.Message); }
        }

        // fields: ability def id, posA, posB, actor resource, waves, pilot override, has stored params.
        static string Replay(object creator, string[] f)
        {
            string guid = CoopDecisionChannel.Get(creator, "GUID") as string;
            try
            {
                object ability = null;
                foreach (object list in new[] { CoopDecisionChannel.Get(creator, "ComponentAbilities"), CoopDecisionChannel.Get(CoopDecisionChannel.Get(creator, "pilot") ?? CallPilot(creator), "Abilities") })
                {
                    foreach (object a in list as IEnumerable ?? new object[0])
                        if (CoopDecisionChannel.Get(CoopDecisionChannel.Get(a, "Def"), "Id") as string == f[0]) { ability = a; break; }
                    if (ability != null) break;
                }
                if (ability == null) throw new InvalidOperationException("ability " + f[0] + " not found on " + guid);
                if (!(CoopDecisionChannel.Get(ability, "IsAvailable") is bool ok && ok)) throw new InvalidOperationException("ability " + f[0] + " not available on this peer");
                object a1 = ParseVec(f[1]), b1 = ParseVec(f[2]);
                if (f[6] == "1")
                {
                    var dict = (IDictionary)storedParams.GetValue(null);
                    object key = keyOf.Invoke(null, new[] { ability, guid, a1, b1 });
                    if (!dict.Contains(key))
                    {
                        object p = Activator.CreateInstance(paramsType);
                        pResource.SetValue(p, f[3] == "~" ? null : f[3]);
                        pWaves.SetValue(p, CoopDecisionChannel.PI(f[4]));
                        pPilot.SetValue(p, f[5] == "~" ? null : f[5]);
                        dict[key] = p;
                    }
                }
                activate.Invoke(ability, new[] { creator, a1, b1 });
                Note();
                return f[0] + ",resource=" + f[3] + ",waves=" + f[4];
            }
            catch (Exception e)
            {
                Error("apply-failed|" + f[0] + "|" + guid + "|" + (e.InnerException ?? e).Message);
                throw;
            }
        }

        // The deferred spawn runs later (the next local-player turn change); each unit added after an activation is a row on both peers.
        static void AddUnitPostfix(object __instance, object __0)
        {
            try
            {
                if (activations == 0 || !ReferenceEquals(activationsCombat, CoopDecisionChannel.Combat()) || spawnRows >= 80) return;
                spawnRows++;
                log(Tag + "|spawned|" + CoopDecisionChannel.Get(__0, "GUID") + "|team=" + CoopDecisionChannel.Get(__instance, "GUID") + "|" + CoopDecisionChannel.Get(__0, "DisplayName"));
            }
            catch (Exception e) { Error("FAILED|spawned|" + e.Message); }
        }

        static void Note()
        {
            object c = CoopDecisionChannel.Combat();
            if (!ReferenceEquals(c, activationsCombat)) { activationsCombat = c; activations = 0; spawnRows = 0; }
            activations++;
        }

        static string Safe(string s) => string.IsNullOrEmpty(s) ? "~" : s;

        static object CallPilot(object actor) => actor?.GetType().GetMethod("GetPilot", Type.EmptyTypes)?.Invoke(actor, null);

        static string Vec(object v)
            => v == null ? "0_0_0" : F(CoopDecisionChannel.Get(v, "x")) + "_" + F(CoopDecisionChannel.Get(v, "y")) + "_" + F(CoopDecisionChannel.Get(v, "z"));

        static string F(object f) => f is float x ? CoopDecisionChannel.F(x) : "0";

        static object ParseVec(string s)
        {
            string[] p = s.Split('_');
            return Activator.CreateInstance(vector3, new object[] { CoopDecisionChannel.PF(p[0]), CoopDecisionChannel.PF(p[1]), CoopDecisionChannel.PF(p[2]) });
        }
    }
}
