// CustomAmmoCategories' AMS interception geometry decided by the co-op host (gh issue 24, CBTBE-9 compare 163;
// .hermes/reports/issue24-cbtbe9-read-1/fix-report.md).
// CAC shoots AMS at each missile along its trajectory spline: genreateAMSInterceptionInfo (in its AttackSequence.GenerateToHitInfo postfix)
// samples the spline every 10 m (`while (num3 < length)`, `num3 += 10f`) and fires each AMS whose owner's CurrentPosition is within Range.
// The spline (AdvWeaponHitInfoRec.GenerateTrajectory) runs from the launcher bone, weaponRep.vfxTransforms[hitIndex % n].position, to
// hitPosition, the target's hit-location bone or the miss point: both are this peer's own animated scene poses. CoopRngSweepDeterminism
// seeds the random draws (R1 hit-info and to-hit), so the shape is equal, but the ends are not. CBTBE-9 seq 231141 (Cyclops LRM10 on the Atlas): the start differed
// about 0.03 m and the ends up to 0.62 m between peers; on missile 8 the host's spline had one more 10 m sample inside the Battlemaster's
// range, so its AMS fired 16 shots on the host and 15 on the guest (AMS ammo 124/125, CAC-AMShootsCount 16/15) and compare 163 forced a resync.
// This adapter, only when an assembly named CustomAmmoCategories is loaded and only in a co-op combat, makes the host's two points the
// guest's: inside AdvWeaponHitInfoHelper.initGenericAdvInfo (CAC's first build of each shot's record, in the GenerateHitInfo seed bracket),
//  - the host, after AdvWeaponHitInfoRec.GenerateTrajectory(), puts its startPosition and hitPosition in UCU's stray-shot bundle
//    (CoopStrayShots, patch 014), which the host sends when the AttackInvocation has run and which a guest waits for before it runs the
//    same invocation (InvocationHandler.CMP9StrayHold);
//  - the guest, before GenerateTrajectory(), takes both points from the bundle (CoopStrayShots.TryApply), writes hitPosition and builds
//    the spline from the host's start (GenerateTrajectory(Vector3)), so CAC's own interception then runs on the same geometry. The random
//    draws are unchanged: the spline is built once, as before. A record that is missing (a sequence made outside an invocation, such as a
//    melee or DFA weapon sequence, or a bundle that timed out) leaves the guest on its own geometry, with a row.
// The intercept re-builds (GenerateTrajectory after an interception) and the frag and stray re-builds run outside initGenericAdvInfo and
// are left alone.
// shortcut: writes UCU's private CoopStrayShots.outbox by reflection and reuses its 8-field stray record (dummy hit fields, the point in
// x,y,z), so a change to that record format breaks it (it then logs FAILED or missing); upgrade in the post-4B co-op adapter fold to a public
// CoopStrayShots.AddPoint(key, point) / TryGetPoint(key, out point) in patch 014 (a UCU full rebuild).
// Also, on both peers in a co-op combat: a row per AMS weapon whose shot count an attack sequence's GenerateToHitInfo changed, so scoring
// does not depend on CAC's own log (it rotates).
// Rows: host `cac-ams-geo|sent|seq=<seq>.<group>.<weapon>|n=<shots>|weapon=<def>|inv=<id>`; guest
// `cac-ams-geo|apply|seq=<seq>.<group>.<weapon>|n=<shots>|maxStartDelta=<m>|maxEndDelta=<m>|weapon=<def>|inv=<id>` (the largest distance
// between the guest's own point and the host's) and `cac-ams-geo|missing|seq=<seq>.<group>.<weapon>|hit=<i>|weapon=<def>|inv=<id>`;
// both peers `cac-ams|seq=<seq>|<owner>/<uid>|shots=<n>|total=<n>|act=<n>|round=<r>`; `cac-ams-geo|installed|`, `|FAILED|`, `|failed|`.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomAmmoCategoriesCoopAmsGeometry
    {
        const string Tag = "cac-ams-geo";
        static Action<string> log;
        static PropertyInfo battleTechGame;
        static FieldInfo fStart, fHit, fHitIndex, fParent, fOutbox, fInvocation, fSeqId, vx, vy, vz;
        static MethodInfo tryApply, isHost, isClient, fromStart, shootsCount, activationsCount, isAms;
        static Type vector3;
        static int geoRows, amsRows;

        sealed class Ctx { public int Seq; public bool Host, Client; public int N; public float MaxStart, MaxEnd; public string Group = "?", Weapon = "?", Def = "?"; public bool Missing; }
        static Ctx current;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            // CAC methods are patched only at the first combat, after CAC's Init (fault M).
            ModAssemblyHook.When("CustomAmmoCategories", cac => ModAssemblyHook.AtFirstCombat(() => Install(cac), logLine, Tag), logLine, Tag);
        }

        static void Install(Assembly cac)
        {
            try
            {
                const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                vector3 = ModAssemblyHook.GameType("UnityEngine.Vector3");
                Type weapon = ModAssemblyHook.GameType("BattleTech.Weapon");
                Type hitInfo = ModAssemblyHook.GameType("BattleTech.WeaponHitInfo");
                Type combat = ModAssemblyHook.GameType("BattleTech.CombatGameState");
                Type sequence = ModAssemblyHook.GameType("BattleTech.AttackDirector+AttackSequence");
                Type stray = ModAssemblyHook.GameType("BattleTech.CoopStrayShots");
                Type direction = ModAssemblyHook.GameType("BattleTech.AttackDirection");
                Type main = cac.GetType("CustAmmoCategories.CustomAmmoCategories", false);
                Type helper = cac.GetType("CustAmmoCategories.AdvWeaponHitInfoHelper", false);
                Type rec = cac.GetType("CustAmmoCategories.AdvWeaponHitInfoRec", false);
                Type amsHelper = cac.GetType("CustAmmoCategories.AMSWeaponEffectStaticHelper", false);
                MethodInfo init = hitInfo == null || sequence == null || combat == null ? null
                    : helper?.GetMethod("initGenericAdvInfo", st, null, new[] { hitInfo, typeof(float), sequence, combat, typeof(bool) }, null);
                MethodInfo build = rec?.GetMethod("GenerateTrajectory", inst, null, Type.EmptyTypes, null);
                fromStart = vector3 == null ? null : rec?.GetMethod("GenerateTrajectory", inst, null, new[] { vector3 }, null);
                MethodInfo toHit = sequence?.GetMethod("GenerateToHitInfo", inst, null, Type.EmptyTypes, null);
                fStart = rec?.GetField("startPosition", inst);
                fHit = rec?.GetField("hitPosition", inst);
                fHitIndex = rec?.GetField("hitIndex", inst);
                fParent = rec?.GetField("parent", inst);
                fSeqId = sequence?.GetField("id", inst);
                fOutbox = stray?.GetField("outbox", st);
                fInvocation = stray?.GetField("CurrentInvocation", st);
                tryApply = stray == null || direction == null || vector3 == null ? null : stray.GetMethod("TryApply", st, null, new[] { typeof(string), typeof(int).MakeByRefType(),
                    direction.MakeByRefType(), typeof(string).MakeByRefType(), typeof(int).MakeByRefType(), vector3.MakeByRefType() }, null);
                isHost = combat == null ? null : stray?.GetMethod("IsHost", st, null, new[] { combat }, null);
                isClient = combat == null ? null : stray?.GetMethod("IsClient", st, null, new[] { combat }, null);
                shootsCount = weapon == null ? null : main?.GetMethod("AMSShootsCount", st, null, new[] { weapon }, null);
                activationsCount = weapon == null ? null : main?.GetMethod("AMSActivationsCount", st, null, new[] { weapon }, null);
                isAms = weapon == null ? null : amsHelper?.GetMethod("isAMS", st, null, new[] { weapon }, null);
                vx = vector3?.GetField("x"); vy = vector3?.GetField("y"); vz = vector3?.GetField("z");
                battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", st);
                var missing = new List<string>();
                foreach (KeyValuePair<string, object> m in new Dictionary<string, object> {
                    { "AdvWeaponHitInfoHelper.initGenericAdvInfo", init }, { "AdvWeaponHitInfoRec.GenerateTrajectory()", build }, { "GenerateTrajectory(Vector3)", fromStart },
                    { "AttackSequence.GenerateToHitInfo", toHit }, { "startPosition", fStart }, { "hitPosition", fHit }, { "hitIndex", fHitIndex }, { "parent", fParent },
                    { "AttackSequence.id", fSeqId }, { "CoopStrayShots.outbox", fOutbox }, { "CoopStrayShots.CurrentInvocation", fInvocation },
                    { "CoopStrayShots.TryApply", tryApply }, { "CoopStrayShots.IsHost", isHost }, { "CoopStrayShots.IsClient", isClient },
                    { "AMSShootsCount(Weapon)", shootsCount }, { "AMSActivationsCount(Weapon)", activationsCount }, { "AMSWeaponEffectStaticHelper.isAMS", isAms },
                    { "Vector3.x/y/z", vz }, { "BattleTechGame", battleTechGame } })
                    if (m.Value == null) missing.Add(m.Key);
                if (missing.Count > 0) { log(Tag + "|FAILED|shape changed: " + string.Join(",", missing.ToArray()) + " not found (needs UCU patch 014)"); return; }
                Patch(init, build, toHit);
                log(Tag + "|installed|" + cac.GetName().Version + "|AMS trajectory start and hit points from the host's stray-shot bundle; cac-ams rows per sequence");
            }
            catch (Exception e) { log(Tag + "|FAILED|" + ModStateCarry.Inner(e)); }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Patch(MethodInfo init, MethodInfo build, MethodInfo toHit)
        {
            var harmony = new Harmony("battletech.port.cac-ams-geo");
            HarmonyMethod Mine(string name, int priority) => new HarmonyMethod(typeof(CustomAmmoCategoriesCoopAmsGeometry).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };
            harmony.Patch(init, prefix: Mine(nameof(InitPrefix), Priority.First), finalizer: Mine(nameof(InitFinalizer), Priority.Last));
            harmony.Patch(build, prefix: Mine(nameof(BuildPrefix), Priority.First), postfix: Mine(nameof(BuildPostfix), Priority.Last));
            harmony.Patch(toHit, prefix: Mine(nameof(ToHitPrefix), Priority.First), postfix: Mine(nameof(ToHitPostfix), Priority.Last));
        }

        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string M(float v) => v.ToString("0.0000", CultureInfo.InvariantCulture);

        // __args: hitInfo, hitChance, sequence, combat, indirectFire.
        static void InitPrefix(object[] __args, ref Ctx __state)
        {
            __state = current;
            current = null;
            try
            {
                object combat = __args[3];
                if (combat == null) return;
                bool host = (bool)isHost.Invoke(null, new[] { combat }), client = !host && (bool)isClient.Invoke(null, new[] { combat });
                if (!host && !client) return;
                current = new Ctx { Seq = (int)fSeqId.GetValue(__args[2]), Host = host, Client = client };
            }
            catch (Exception e) { log(Tag + "|failed|init " + ModStateCarry.Inner(e)); }
        }

        static void InitFinalizer(Ctx __state)
        {
            Ctx c = current;
            current = __state;
            if (c == null || c.N == 0 || ++geoRows > 2000) return;
            string seq = "seq=" + c.Seq + "." + c.Group + "." + c.Weapon + "|n=" + c.N;
            string tail = "|weapon=" + c.Def + "|inv=" + (fInvocation.GetValue(null) as string ?? "-");
            if (c.Host) log(Tag + "|sent|" + seq + tail);
            else log(Tag + "|apply|" + seq + "|maxStartDelta=" + M(c.MaxStart) + "|maxEndDelta=" + M(c.MaxEnd) + tail);
        }

        static string Key(object rec, Ctx c)
        {
            object parent = fParent.GetValue(rec);
            c.Group = ModStateCarry.Get(parent, "groupIdx")?.ToString() ?? "?";
            c.Weapon = ModStateCarry.Get(parent, "weaponIdx")?.ToString() ?? "?";
            c.Def = ModStateCarry.Get(ModStateCarry.Get(parent, "weapon"), "defId") as string ?? "?";
            // CoopStrayShots.ShotKey's form, in a namespace of its own; no ',' or ';' (the bundle's separators).
            return (fInvocation.GetValue(null) as string ?? "-") + "#cacams." + c.Seq + "." + c.Group + "." + c.Weapon + "." + fHitIndex.GetValue(rec);
        }

        // Guest: the host's points, then the spline from the host's start; the original (which would read this peer's launcher bone) is skipped.
        static bool BuildPrefix(object __instance)
        {
            Ctx c = current;
            if (c == null || !c.Client) return true;
            try
            {
                string key = Key(__instance, c);
                object start = Take(key + ".s"), end = Take(key + ".e");
                if (start == null || end == null)
                {
                    c.Missing = true;
                    if (++geoRows <= 2000) log(Tag + "|missing|seq=" + c.Seq + "." + c.Group + "." + c.Weapon + "|hit=" + fHitIndex.GetValue(__instance) + "|weapon=" + c.Def
                        + "|inv=" + (fInvocation.GetValue(null) as string ?? "-"));
                    return true;
                }
                object ownStart = OwnStart(__instance), ownEnd = fHit.GetValue(__instance);
                if (ownStart != null) c.MaxStart = Math.Max(c.MaxStart, Distance(ownStart, start));
                c.MaxEnd = Math.Max(c.MaxEnd, Distance(ownEnd, end));
                fHit.SetValue(__instance, end);
                fromStart.Invoke(__instance, new[] { start });
                c.N++;
                return false;
            }
            catch (Exception e) { log(Tag + "|failed|guest " + ModStateCarry.Inner(e)); return true; }
        }

        // Host: what the original built from.
        static void BuildPostfix(object __instance)
        {
            Ctx c = current;
            if (c == null || !c.Host) return;
            try
            {
                string key = Key(__instance, c);
                var outbox = (IList)fOutbox.GetValue(null);
                outbox.Add(Record(key + ".s", fStart.GetValue(__instance)));
                outbox.Add(Record(key + ".e", fHit.GetValue(__instance)));
                c.N++;
            }
            catch (Exception e) { log(Tag + "|failed|host " + ModStateCarry.Inner(e)); }
        }

        // CoopStrayShots' record: key, hit, secondary, secondaryLocation, direction, x, y, z.
        static string Record(string key, object v) => key + ",0,-,0,0," + F((float)vx.GetValue(v)) + "," + F((float)vy.GetValue(v)) + "," + F((float)vz.GetValue(v));

        static object Take(string key)
        {
            object[] args = { key, 0, Enum.ToObject(tryApply.GetParameters()[2].ParameterType.GetElementType(), 0), null, 0, null };
            return (bool)tryApply.Invoke(null, args) ? args[5] : null;
        }

        // As GenerateTrajectory(): the launcher bone for this shot, or the owner's position when the weapon has no representation.
        static object OwnStart(object rec)
        {
            object weapon = ModStateCarry.Get(fParent.GetValue(rec), "weapon");
            object rep = ModStateCarry.Get(weapon, "weaponRep");
            if (rep != null && !rep.Equals(null) && ModStateCarry.Get(rep, "vfxTransforms") is Array t && t.Length > 0)
            {
                object tr = t.GetValue((int)fHitIndex.GetValue(rec) % t.Length);
                if (tr != null && !tr.Equals(null)) return ModStateCarry.Get(tr, "position");
            }
            return ModStateCarry.Get(ModStateCarry.Get(weapon, "parent"), "CurrentPosition");
        }

        static float Distance(object a, object b)
        {
            float dx = (float)vx.GetValue(a) - (float)vx.GetValue(b), dy = (float)vy.GetValue(a) - (float)vy.GetValue(b), dz = (float)vz.GetValue(a) - (float)vz.GetValue(b);
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // Both peers: each AMS weapon's shot and activation counts before the sequence's to-hit generation (CAC's AMS postfix runs inside it).
        static void ToHitPrefix(ref Dictionary<object, int[]> __state)
        {
            __state = null;
            try
            {
                object combat = ModStateCarry.Get(battleTechGame.GetValue(null, null), "Combat");
                if (combat == null || !((bool)isHost.Invoke(null, new[] { combat }) || (bool)isClient.Invoke(null, new[] { combat }))) return;
                __state = new Dictionary<object, int[]>();
                foreach (object actor in ModStateCarry.Get(combat, "AllActors") as IEnumerable ?? new object[0])
                    foreach (object w in ModStateCarry.Get(actor, "Weapons") as IEnumerable ?? new object[0])
                    {
                        // One weapon CAC cannot read does not cost the rest of the sequence's rows.
                        try
                        {
                            if (w != null && (bool)isAms.Invoke(null, new[] { w }))
                                __state[w] = new[] { (int)shootsCount.Invoke(null, new[] { w }), (int)activationsCount.Invoke(null, new[] { w }) };
                        }
                        catch (Exception e) { if (++amsRows <= 1000) log(Tag + "|failed|ams-before " + ModStateCarry.Get(w, "defId") + " " + ModStateCarry.Inner(e)); }
                    }
            }
            catch (Exception e) { __state = null; log(Tag + "|failed|ams-before " + ModStateCarry.Inner(e)); }
        }

        static void ToHitPostfix(object __instance, Dictionary<object, int[]> __state)
        {
            if (__state == null) return;
            try
            {
                object td = ModStateCarry.Get(ModStateCarry.Get(battleTechGame.GetValue(null, null), "Combat"), "TurnDirector");
                foreach (KeyValuePair<object, int[]> kv in __state)
                {
                    int shots = (int)shootsCount.Invoke(null, new[] { kv.Key }), acts = (int)activationsCount.Invoke(null, new[] { kv.Key });
                    if (shots == kv.Value[0] && acts == kv.Value[1]) continue;
                    if (++amsRows > 1000) return;
                    log("cac-ams|seq=" + fSeqId.GetValue(__instance) + "|" + (ModStateCarry.Get(ModStateCarry.Get(kv.Key, "parent"), "GUID") as string ?? "-") + "/"
                        + (ModStateCarry.Get(kv.Key, "uid") as string ?? "-") + "|shots=" + (shots - kv.Value[0]) + "|total=" + shots + "|act=" + acts
                        + "|round=" + ModStateCarry.Get(td, "CurrentRound"));
                }
            }
            catch (Exception e) { log(Tag + "|failed|ams-after " + ModStateCarry.Inner(e)); }
        }
    }
}
