// CustomAmmoCategories' jam and ammo-explosion queues after a co-op resync or rejoin load (gh issue 24, CBTBE-9 compare 264;
// .hermes/reports/issue24-cbtbe9-read-1/fix-report.md).
// CAC keeps three static queues that a combat's destruction empties (CustomAmmoCategories.ClearJammingInfo, from CAC's
// CombatGameState.OnCombatGameDestroyed prefix), and the save holds none of them, so a guest's reload drops them while the host keeps them:
//  - jammAMSQueue (Queue<AMSJammInfoMessage>): one entry per AMS that fired in an attack (genreateAMSInterceptionInfo enqueues it).
//    JammingEnabler.jammAMS drains it at TurnDirector.BeginNewRound and is the only writer that resets CAC-AMShootsCount and
//    CAC-AMSActivationsCount to 0. CBTBE-9: the AMS of the Atlas and the Battlemaster fired in round 4 (seq 231141); resync 2 reloaded the
//    guest in round 4; at round 5 the host drained [Atlas 20, Battlemaster 20, Battlemaster 0] and the guest [Battlemaster 20], so the guest's
//    Atlas kept 20 shots / 1 activation (its AMS could not fire in round 5) and the MECHS compare 264 forced resync 3.
//  - jammQueue (Queue<JammMessage>: actor, floatie text), drained by prosessJummingMessages. Same gap, not measured.
//  - ammoExposionQueue (Queue<AmmunitionBox>), drained by prosessExposion (an ammo box exhaustion check). Same gap, not measured.
// Entries are written on every peer alike while play runs (the attack resolution, seeded by CustomAmmoCategoriesCoopImpact), so only the
// reload loses them and nothing is sent with an invocation.
// This adapter, only when an assembly named CustomAmmoCategories is loaded, registers a source with UCU's co-op side channel (patch 166).
// The host's full state for a peer that has just loaded (capture(null)) is the three queues in dequeue order: an AMS entry is the weapon
// (owner GUID, uid) and its chance, unsafechance, description, damage, destroy and cooldown; a jam message is the actor GUID and the text;
// an ammo box is its owner GUID and uid. The guest empties its own queues and refills them from those values (the host decides), building an
// AMSJammInfoMessage without its constructor so every field is the host's. Only game methods are patched (CampaignSessionHost.SerializeSnapshot,
// for the host's snapshot row); CAC is read and written by reflection at capture and apply time, after its Init.
// Rows (same list format on both peers): host `cac-jamq|capture|at=snapshot|<list>` and `cac-jamq|capture|at=send|<list>`, guest
// `cac-jamq|before|<list>` and `cac-jamq|apply|<list>`, where <list> is
// `ams=<n>:<owner>/<uid>@<shots>/<activations>,...;jam=<n>:<actor>,...;expl=<n>:<owner>/<uid>,...` (shots/activations are the weapon's
// CAC-AMShootsCount / CAC-AMSActivationsCount when the row is written); `cac-jamq|unmapped|<what>|<key>`, `|installed|`, `|FAILED|`,
// `|apply-failed|`, and `cac-jamq|count|...` at each combat's end.
// shortcut: a third peer's held order and the full state share the host's one relay order, as for patch 166's other sources.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomAmmoCategoriesCoopJamQueue
    {
        const string Tag = "cac-jamq";
        static Action<string> log;
        static CoopCarryCounter counter;
        static PropertyInfo battleTechGame, qAms, qJam, qExpl;
        static PropertyInfo pWeapon, pChance, pUnsafe, pDescription, pDamage, pDestroy, pCooldown;
        static FieldInfo fJamActor, fJamMessage;
        static MethodInfo shootsCount, activationsCount, findActor;
        static Type amsType, jamType;
        static int rows;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("CustomAmmoCategories", Apply, logLine, Tag);
        }

        static void Apply(Assembly cac)
        {
            try
            {
                const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type main = cac.GetType("CustAmmoCategories.CustomAmmoCategories", false);
                amsType = cac.GetType("CustAmmoCategories.AMSJammInfoMessage", false);
                jamType = cac.GetType("CustAmmoCategories.JammMessage", false);
                Type weapon = ModAssemblyHook.GameType("BattleTech.Weapon");
                qAms = main?.GetProperty("jammAMSQueue", st);
                qJam = main?.GetProperty("jammQueue", st);
                qExpl = main?.GetProperty("ammoExposionQueue", st);
                shootsCount = weapon == null ? null : main?.GetMethod("AMSShootsCount", st, null, new[] { weapon }, null);
                activationsCount = weapon == null ? null : main?.GetMethod("AMSActivationsCount", st, null, new[] { weapon }, null);
                pWeapon = amsType?.GetProperty("weapon", inst);
                pChance = amsType?.GetProperty("chance", inst);
                pUnsafe = amsType?.GetProperty("unsafechance", inst);
                pDescription = amsType?.GetProperty("description", inst);
                pDamage = amsType?.GetProperty("damage", inst);
                pDestroy = amsType?.GetProperty("destroy", inst);
                pCooldown = amsType?.GetProperty("cooldown", inst);
                fJamActor = jamType?.GetField("actor", inst);
                fJamMessage = jamType?.GetField("message", inst);
                battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", st);
                findActor = ModAssemblyHook.GameType("BattleTech.CombatGameState")?.GetMethod("FindActorByGUID", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                MethodInfo register = ModAssemblyHook.GameType("BattleTech.CoopInvocationSideData")?.GetMethod("Register", st);
                MethodInfo serialize = ModAssemblyHook.GameType("BattleTech.Network.Server.CampaignSessionHost")?.GetMethod("SerializeSnapshot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                var missing = new List<string>();
                foreach (KeyValuePair<string, object> m in new Dictionary<string, object> {
                    { "CustomAmmoCategories.jammAMSQueue", qAms }, { "jammQueue", qJam }, { "ammoExposionQueue", qExpl },
                    { "AMSShootsCount(Weapon)", shootsCount }, { "AMSActivationsCount(Weapon)", activationsCount },
                    { "AMSJammInfoMessage.weapon", pWeapon?.GetSetMethod(true) }, { "chance", pChance?.GetSetMethod(true) }, { "unsafechance", pUnsafe?.GetSetMethod(true) },
                    { "description", pDescription?.GetSetMethod(true) }, { "damage", pDamage?.GetSetMethod(true) }, { "destroy", pDestroy?.GetSetMethod(true) },
                    { "cooldown", pCooldown?.GetSetMethod(true) }, { "JammMessage.actor", fJamActor }, { "JammMessage.message", fJamMessage },
                    { "BattleTechGame", battleTechGame }, { "CombatGameState.FindActorByGUID", findActor }, { "CoopInvocationSideData.Register", register } })
                    if (m.Value == null) missing.Add(m.Key);
                if (missing.Count > 0) { log(Tag + "|FAILED|shape changed: " + string.Join(",", missing.ToArray()) + " not found (needs UCU patch 166)"); return; }
                counter = CoopCarryCounter.For(Tag, log);
                register.Invoke(null, new object[] { Tag, new Func<object, string>(Capture), new Action<object, string>(ApplyData) });
                string snap = "no snapshot row (CampaignSessionHost.SerializeSnapshot not found)";
                if (serialize != null)
                {
                    PatchSnapshot(serialize);
                    snap = "row at CampaignSessionHost.SerializeSnapshot";
                }
                log(Tag + "|installed|" + cac.GetName().Version + "|jammAMSQueue, jammQueue and ammoExposionQueue sent to a peer that loads a co-op snapshot; " + snap);
            }
            catch (Exception e) { log(Tag + "|FAILED|" + ModStateCarry.Inner(e)); }
        }

        // Its own method, so Apply's JIT does not need HarmonyLib before the shape check.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void PatchSnapshot(MethodInfo serialize)
        {
            new Harmony("battletech.port.cac-jamq").Patch(serialize,
                postfix: new HarmonyMethod(typeof(CustomAmmoCategoriesCoopJamQueue).GetMethod(nameof(SnapshotPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        static object Combat() => ModStateCarry.Get(battleTechGame.GetValue(null, null), "Combat");

        static bool CoopSession()
        {
            if (AppDomain.CurrentDomain.GetData("BattleTech.UCU.CoopCombat.v1") as string == "on") return true;
            object net = ModStateCarry.Get(battleTechGame.GetValue(null, null), "NetworkGameInstance");
            return ModStateCarry.Get(net, "IsCampaignHost") is bool h && h || ModStateCarry.Get(net, "IsCampaignClient") is bool c && c;
        }

        sealed class Ams { public string Owner, Uid, Description; public float Chance, Unsafe; public bool Damage, Destroy; public int Cooldown; }
        sealed class Jam { public string Actor, Message; }
        sealed class Box { public string Owner, Uid; }
        sealed class State { public List<Ams> AmsList = new List<Ams>(); public List<Jam> Jams = new List<Jam>(); public List<Box> Boxes = new List<Box>(); }

        static string Guid(object o) => ModStateCarry.Get(o, "GUID") as string ?? "";
        static string Uid(object o) => ModStateCarry.Get(o, "uid") as string ?? "";

        static State Read()
        {
            var s = new State();
            foreach (object e in qAms.GetValue(null, null) as IEnumerable ?? new object[0])
            {
                if (e == null) continue;
                object w = pWeapon.GetValue(e, null);
                s.AmsList.Add(new Ams
                {
                    Owner = Guid(ModStateCarry.Get(w, "parent")), Uid = Uid(w), Description = pDescription.GetValue(e, null) as string ?? "",
                    Chance = (float)pChance.GetValue(e, null), Unsafe = (float)pUnsafe.GetValue(e, null),
                    Damage = (bool)pDamage.GetValue(e, null), Destroy = (bool)pDestroy.GetValue(e, null), Cooldown = (int)pCooldown.GetValue(e, null),
                });
            }
            foreach (object e in qJam.GetValue(null, null) as IEnumerable ?? new object[0])
                if (e != null) s.Jams.Add(new Jam { Actor = Guid(fJamActor.GetValue(e)), Message = fJamMessage.GetValue(e) as string ?? "" });
            foreach (object b in qExpl.GetValue(null, null) as IEnumerable ?? new object[0])
                if (b != null) s.Boxes.Add(new Box { Owner = Guid(ModStateCarry.Get(b, "parent")), Uid = Uid(b) });
            return s;
        }

        static void SnapshotPostfix()
        {
            try
            {
                if (Combat() == null || !CoopSession()) return;
                Row("capture|at=snapshot", Read());
            }
            catch (Exception e) { log(Tag + "|FAILED|capture-snapshot|" + ModStateCarry.Inner(e)); }
        }

        // Host: the full state for a peer that has just loaded (invocation == null); nothing with an invocation or a SendNow.
        static string Capture(object invocation)
        {
            if (invocation != null) return null;
            try
            {
                if (Combat() == null || !CoopSession()) return null;
                State s = Read();
                var sb = new StringBuilder();
                Int(sb, s.AmsList.Count);
                foreach (Ams a in s.AmsList)
                {
                    Str(sb, a.Owner); Str(sb, a.Uid); Str(sb, a.Description); Str(sb, F(a.Chance)); Str(sb, F(a.Unsafe));
                    Int(sb, a.Damage ? 1 : 0); Int(sb, a.Destroy ? 1 : 0); Int(sb, a.Cooldown);
                }
                Int(sb, s.Jams.Count);
                foreach (Jam j in s.Jams) { Str(sb, j.Actor); Str(sb, j.Message); }
                Int(sb, s.Boxes.Count);
                foreach (Box b in s.Boxes) { Str(sb, b.Owner); Str(sb, b.Uid); }
                counter.Capture++;
                if (s.AmsList.Count + s.Jams.Count + s.Boxes.Count > 0) counter.NonEmpty++;
                Row("capture|at=send", s);
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
            }
            catch (Exception e) { log(Tag + "|FAILED|capture|" + ModStateCarry.Inner(e)); return null; }
        }

        static void ApplyData(object invocation, string data)
        {
            if (invocation != null || string.IsNullOrEmpty(data)) return;
            try
            {
                object combat = Combat();
                if (combat == null) return;
                string text = Encoding.UTF8.GetString(Convert.FromBase64String(data));
                int pos = 0;
                var s = new State();
                for (int i = 0, n = Int(text, ref pos); i < n; i++)
                {
                    var a = new Ams { Owner = Str(text, ref pos), Uid = Str(text, ref pos), Description = Str(text, ref pos) };
                    a.Chance = float.Parse(Str(text, ref pos), NumberStyles.Float, CultureInfo.InvariantCulture);
                    a.Unsafe = float.Parse(Str(text, ref pos), NumberStyles.Float, CultureInfo.InvariantCulture);
                    a.Damage = Int(text, ref pos) != 0; a.Destroy = Int(text, ref pos) != 0; a.Cooldown = Int(text, ref pos);
                    s.AmsList.Add(a);
                }
                for (int i = 0, n = Int(text, ref pos); i < n; i++) s.Jams.Add(new Jam { Actor = Str(text, ref pos), Message = Str(text, ref pos) });
                for (int i = 0, n = Int(text, ref pos); i < n; i++) s.Boxes.Add(new Box { Owner = Str(text, ref pos), Uid = Str(text, ref pos) });
                if (pos != text.Length) throw new FormatException("trailing data");
                Row("before", Read());
                // The host decides: this peer's own entries go, the host's come in its order.
                object ams = qAms.GetValue(null, null), jam = qJam.GetValue(null, null), expl = qExpl.GetValue(null, null);
                Clear(ams); Clear(jam); Clear(expl);
                foreach (Ams a in s.AmsList)
                {
                    object w = Component(combat, a.Owner, a.Uid, "Weapons", "weapon");
                    if (w == null) continue;
                    object m = FormatterServices.GetUninitializedObject(amsType);
                    pWeapon.GetSetMethod(true).Invoke(m, new[] { w });
                    pChance.GetSetMethod(true).Invoke(m, new object[] { a.Chance });
                    pUnsafe.GetSetMethod(true).Invoke(m, new object[] { a.Unsafe });
                    pDescription.GetSetMethod(true).Invoke(m, new object[] { a.Description });
                    pDamage.GetSetMethod(true).Invoke(m, new object[] { a.Damage });
                    pDestroy.GetSetMethod(true).Invoke(m, new object[] { a.Destroy });
                    pCooldown.GetSetMethod(true).Invoke(m, new object[] { a.Cooldown });
                    Enqueue(ams, m);
                }
                foreach (Jam j in s.Jams)
                {
                    object actor = findActor.Invoke(combat, new object[] { j.Actor });
                    if (actor == null) { Unmapped("jam-actor", j.Actor); continue; }
                    Enqueue(jam, Activator.CreateInstance(jamType, new[] { actor, j.Message }));
                }
                foreach (Box b in s.Boxes)
                {
                    object box = Component(combat, b.Owner, b.Uid, "ammoBoxes", "ammo-box");
                    if (box != null) Enqueue(expl, box);
                }
                counter.Apply++;
                if (s.AmsList.Count + s.Jams.Count + s.Boxes.Count > 0) counter.NonEmpty++;
                Row("apply", Read());
            }
            catch (Exception e) { log(Tag + "|apply-failed|" + ModStateCarry.Inner(e)); }
        }

        static object Component(object combat, string owner, string uid, string list, string what)
        {
            object actor = findActor.Invoke(combat, new object[] { owner });
            foreach (object c in ModStateCarry.Get(actor, list) as IEnumerable ?? new object[0])
                if (Uid(c) == uid) return c;
            Unmapped(what, owner + "/" + uid);
            return null;
        }

        static void Clear(object queue) => queue.GetType().GetMethod("Clear", Type.EmptyTypes).Invoke(queue, null);

        static void Enqueue(object queue, object item) => queue.GetType().GetMethod("Enqueue").Invoke(queue, new[] { item });

        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        static void Int(StringBuilder sb, int v) => sb.Append('i').Append(v.ToString(CultureInfo.InvariantCulture)).Append(';');

        static void Str(StringBuilder sb, string v) => sb.Append('s').Append(v.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(v);

        static int Int(string s, ref int pos)
        {
            if (pos >= s.Length || s[pos] != 'i') throw new FormatException("expected i at " + pos);
            int e = s.IndexOf(';', pos);
            int v = int.Parse(s.Substring(pos + 1, e - pos - 1), CultureInfo.InvariantCulture);
            pos = e + 1;
            return v;
        }

        static string Str(string s, ref int pos)
        {
            if (pos >= s.Length || s[pos] != 's') throw new FormatException("expected s at " + pos);
            int c = s.IndexOf(':', pos);
            int n = int.Parse(s.Substring(pos + 1, c - pos - 1), CultureInfo.InvariantCulture);
            string r = s.Substring(c + 1, n);
            pos = c + 1 + n;
            return r;
        }

        static void Unmapped(string what, string key) { if (++rows <= 200) log(Tag + "|unmapped|" + what + "|" + key); }

        static string Counts(object weapon)
        {
            try { return shootsCount.Invoke(null, new[] { weapon }) + "/" + activationsCount.Invoke(null, new[] { weapon }); }
            catch { return "?"; }
        }

        static void Row(string kind, State s)
        {
            if (++rows > 200) return;
            object combat = Combat();
            var ams = new List<string>();
            foreach (Ams a in s.AmsList)
            {
                object w = null;
                foreach (object c in ModStateCarry.Get(combat == null ? null : findActor.Invoke(combat, new object[] { a.Owner }), "Weapons") as IEnumerable ?? new object[0])
                    if (Uid(c) == a.Uid) { w = c; break; }
                ams.Add(a.Owner + "/" + a.Uid + "@" + (w == null ? "?" : Counts(w)));
            }
            var jams = new List<string>();
            foreach (Jam j in s.Jams) jams.Add(j.Actor);
            var boxes = new List<string>();
            foreach (Box b in s.Boxes) boxes.Add(b.Owner + "/" + b.Uid);
            log(Tag + "|" + kind + "|ams=" + ams.Count + ":" + string.Join(",", ams.ToArray()) + ";jam=" + jams.Count + ":" + string.Join(",", jams.ToArray())
                + ";expl=" + boxes.Count + ":" + string.Join(",", boxes.ToArray()));
        }
    }
}
