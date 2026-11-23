// CustomAmmoCategories' pending deferred effects after a co-op resync or rejoin load (gh issue 24, snapshot-field sweep row
// "CAC DeferredEffectHelper"; CBTBE-8 resync 3 "deferred/queued effect frame timing"; Sam 2026-11-22: fix the not-carried class before 4B).
// A weapon with a deferred effect (artillery shells, delayed blasts: DeferredEffectDef.rounds > 0) makes a DeferredEffect component when
// it hits (DeferredEffectHelper.CreateDifferedEffect, from the attack resolution on every peer) and keeps it in the private static
// DeferredEffectHelper.deferredEffects until UpdateDefferedEffects plays it, RoundsRemain rounds later (AoE damage, effects, burns, masks).
// A combat's destruction runs DeferredEffectHelper.Clear (CAC CombatGameState_OnCombatGameDestroyed prefix), so a guest's reload drops every
// pending one while the host keeps it, and only the host's land. Creation runs on every peer, so only the reload loses them and nothing is
// sent with an invocation.
// This adapter, only when an assembly named CustomAmmoCategories is loaded, registers a source with UCU's co-op side channel (patch 166).
// The host's full state for a peer that has just loaded is DeferredEffectHelper's CurrentRound and, per pending effect: the weapon (owner
// GUID, uid), its DeferredEffectDef id, RoundStarted, SequenceId, the sticky target's GUID (the combatant whose GameRep.transform is the
// effect's ancor) or none, and the offset. The guest drops its own pending ones (DeferredEffect.Clear) and makes each as CreateDifferedEffect
// does (an inactive GameObject with a DeferredEffect component) with those fields set directly: DeferredEffect.Init is not called, because it
// takes StackManager.NextStackUID (StackManager.cs:30, stackUID++), which would put the guest's stack ids out of step with the host's. The
// def is the weapon's own DeferredEffect() and is used only when its id is the host's.
// Not made on the guest: the countdown floatie and the AoE reticle (visual; UpdateText and PlayEffect check both for null).
// shortcut: a def built from the weapon (getFromWeapon) takes the weapon's values at the load, not at the hit; upgrade by carrying the def's
// numbers if a leg shows a deferred blast differ.
// Rows: host `cac-deferred-coop|capture|n=<n>|<owner>/<uid>:<def>@<started>#<seq>^<target>~<offset>,...|round=<r>|at=full`, guest
// `cac-deferred-coop|before|...` and `|apply|...` in the same format, `|unmapped|<what>|<key>`, `|installed|`, `|FAILED|`, `|apply-failed|`,
// and `cac-deferred-coop|count|...` at each combat's end.
// shortcut: a third peer's held order and the full state share the host's one relay order, as for patch 166's other sources.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace BattleTechPort
{
    public static class CacDeferredEffectCoopSync
    {
        const string Tag = "cac-deferred-coop";
        static Action<string> log;
        static CoopCarryCounter counter;
        static PropertyInfo battleTechGame, pWeapon, pDefinition, pT, pDamageApplied, pAncor, pPlaying;
        static FieldInfo pending, currentRound, fSeq, fStarted, fOffset;
        static MethodInfo deferredOf, clearEffect;
        static Type effectType, gameObjectType, vector3;
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
                Type helper = cac.GetType("CustAmmoCategories.DeferredEffectHelper", false);
                effectType = cac.GetType("CustAmmoCategories.DeferredEffect", false);
                gameObjectType = ModAssemblyHook.GameType("UnityEngine.GameObject");
                vector3 = ModAssemblyHook.GameType("UnityEngine.Vector3");
                Type weaponType = ModAssemblyHook.GameType("BattleTech.Weapon");
                pending = helper?.GetField("deferredEffects", st);
                currentRound = helper?.GetField("CurrentRound", st);
                deferredOf = weaponType == null ? null : helper?.GetMethod("DeferredEffect", st, null, new[] { weaponType }, null);
                pWeapon = effectType?.GetProperty("weapon", inst);
                pDefinition = effectType?.GetProperty("definition", inst);
                pT = effectType?.GetProperty("t", inst);
                pDamageApplied = effectType?.GetProperty("DamageApplyied", inst);
                pPlaying = effectType?.GetProperty("Playing", inst);
                pAncor = effectType?.GetProperty("ancor", inst);
                fSeq = effectType?.GetField("SequenceId", inst);
                fStarted = effectType?.GetField("RoundStarted", inst);
                fOffset = effectType?.GetField("offset", inst);
                clearEffect = effectType?.GetMethod("Clear", inst, null, Type.EmptyTypes, null);
                battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", st);
                MethodInfo register = ModAssemblyHook.GameType("BattleTech.CoopInvocationSideData")?.GetMethod("Register", st);
                var missing = new List<string>();
                foreach (KeyValuePair<string, object> m in new Dictionary<string, object> {
                    { "DeferredEffectHelper.deferredEffects", pending }, { "CurrentRound", currentRound }, { "DeferredEffect(Weapon)", deferredOf },
                    { "DeferredEffect.weapon", pWeapon?.GetSetMethod(true) }, { "definition", pDefinition?.GetSetMethod(true) }, { "t", pT?.GetSetMethod(true) },
                    { "DamageApplyied", pDamageApplied?.GetSetMethod(true) }, { "Playing", pPlaying }, { "ancor", pAncor?.GetSetMethod(true) }, { "SequenceId", fSeq },
                    { "RoundStarted", fStarted }, { "offset", fOffset }, { "Clear", clearEffect }, { "UnityEngine.GameObject", gameObjectType }, { "Vector3", vector3 },
                    { "BattleTechGame", battleTechGame }, { "CoopInvocationSideData.Register", register } })
                    if (m.Value == null) missing.Add(m.Key);
                if (missing.Count > 0) { log(Tag + "|FAILED|shape changed: " + string.Join(",", missing.ToArray()) + " not found (needs UCU patch 166)"); return; }
                counter = CoopCarryCounter.For(Tag, log);
                register.Invoke(null, new object[] { Tag, new Func<object, string>(Capture), new Action<object, string>(ApplyData) });
                log(Tag + "|installed|" + cac.GetName().Version + "|pending deferred effects sent to a peer that loads a co-op snapshot (no floatie or reticle made)");
            }
            catch (Exception e) { log(Tag + "|FAILED|" + ModStateCarry.Inner(e)); }
        }

        static object Combat() => ModStateCarry.Get(battleTechGame.GetValue(null, null), "Combat");

        sealed class Rec { public string Owner, Uid, Def, Target, Offset; public int Started, Seq; }

        static List<Rec> Read(object combat)
        {
            var list = new List<Rec>();
            if (!(pending.GetValue(null) is IEnumerable all)) return list;
            Dictionary<object, string> byTransform = Transforms(combat);
            foreach (object e in all)
            {
                if (e == null || ((UnityEngine_Object_IsNull(e)))) continue;
                object w = pWeapon.GetValue(e, null);
                object anc = pAncor.GetValue(e, null);
                string target = "";
                if (anc != null && !byTransform.TryGetValue(anc, out target)) target = "?";
                list.Add(new Rec
                {
                    Owner = ModStateCarry.Get(ModStateCarry.Get(w, "parent"), "GUID") as string ?? "",
                    Uid = ModStateCarry.Get(w, "uid") as string ?? "",
                    Def = ModStateCarry.Get(pDefinition.GetValue(e, null), "id") as string ?? "",
                    Started = (int)fStarted.GetValue(e),
                    Seq = (int)fSeq.GetValue(e),
                    Target = target ?? "",
                    Offset = Vec(fOffset.GetValue(e)),
                });
            }
            list.Sort((a, b) => string.CompareOrdinal(Key(a), Key(b)));
            return list;
        }

        // A destroyed Unity object compares equal to null through its own operator; ReferenceEquals does not see that.
        static bool UnityEngine_Object_IsNull(object o) => o.Equals(null);

        static string Key(Rec r) => r.Owner + "/" + r.Uid + ":" + r.Def + "@" + r.Started.ToString(CultureInfo.InvariantCulture) + "#" + r.Seq.ToString(CultureInfo.InvariantCulture)
            + "^" + r.Target + "~" + r.Offset;

        static Dictionary<object, string> Transforms(object combat)
        {
            var map = new Dictionary<object, string>(new SameObject());
            var combatants = new List<object>();
            if (ModStateCarry.Get(combat, "AllActors") is IEnumerable actors) foreach (object a in actors) combatants.Add(a);
            MethodInfo misc = combat.GetType().GetMethod("GetAllMiscCombatants", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (misc?.Invoke(combat, null) is IEnumerable others) foreach (object o in others) combatants.Add(o);
            foreach (object c in combatants)
            {
                object rep = ModStateCarry.Get(c, "GameRep");
                object tr = rep == null || rep.Equals(null) ? null : ModStateCarry.Get(rep, "transform");
                string guid = ModStateCarry.Get(c, "GUID") as string;
                if (tr != null && guid != null && !map.ContainsKey(tr)) map[tr] = guid;
            }
            return map;
        }

        static string Capture(object invocation)
        {
            if (invocation != null) return null;
            try
            {
                object combat = Combat();
                if (combat == null) return null;
                List<Rec> recs = Read(combat);
                var sb = new StringBuilder();
                sb.Append('i').Append(((int)currentRound.GetValue(null)).ToString(CultureInfo.InvariantCulture)).Append(';');
                sb.Append('i').Append(recs.Count.ToString(CultureInfo.InvariantCulture)).Append(';');
                foreach (Rec r in recs)
                {
                    foreach (string s in new[] { r.Owner, r.Uid, r.Def, r.Target, r.Offset }) sb.Append('s').Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s);
                    sb.Append('i').Append(r.Started.ToString(CultureInfo.InvariantCulture)).Append(';').Append('i').Append(r.Seq.ToString(CultureInfo.InvariantCulture)).Append(';');
                }
                counter.Capture++;
                if (recs.Count > 0) counter.NonEmpty++;
                Row("capture", recs, (int)currentRound.GetValue(null), "full");
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
                string s = Encoding.UTF8.GetString(Convert.FromBase64String(data));
                int pos = 0;
                int round = Int(s, ref pos), n = Int(s, ref pos);
                var recs = new List<Rec>();
                for (int i = 0; i < n; i++)
                {
                    var r = new Rec { Owner = Str(s, ref pos), Uid = Str(s, ref pos), Def = Str(s, ref pos), Target = Str(s, ref pos), Offset = Str(s, ref pos) };
                    r.Started = Int(s, ref pos); r.Seq = Int(s, ref pos);
                    recs.Add(r);
                }
                if (pos != s.Length) throw new FormatException("trailing data");
                Row("before", Read(combat), (int)currentRound.GetValue(null), "apply");
                // The host decides: this peer's own pending effects go.
                var mine = new List<object>();
                foreach (object e in (IEnumerable)pending.GetValue(null)) mine.Add(e);
                MethodInfo remove = pending.FieldType.GetMethod("Remove");
                MethodInfo add = pending.FieldType.GetMethod("Add");
                foreach (object e in mine)
                {
                    remove.Invoke(pending.GetValue(null), new[] { e });
                    if (e != null && !e.Equals(null)) { try { clearEffect.Invoke(e, null); } catch (Exception x) { log(Tag + "|apply-failed|clear|" + ModStateCarry.Inner(x)); } DestroyOwner(e); }
                }
                foreach (Rec r in recs)
                {
                    object effect = Make(combat, r);
                    if (effect != null) add.Invoke(pending.GetValue(null), new[] { effect });
                }
                currentRound.SetValue(null, round);
                counter.Apply++;
                if (recs.Count > 0) counter.NonEmpty++;
                Row("apply", Read(combat), round, "apply");
            }
            catch (Exception e) { log(Tag + "|apply-failed|" + ModStateCarry.Inner(e)); }
        }

        static object Make(object combat, Rec r)
        {
            MethodInfo find = combat.GetType().GetMethod("FindCombatantByGUID", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(bool) }, null);
            object owner = find.Invoke(combat, new object[] { r.Owner, false });
            object weapon = null;
            foreach (object w in ModStateCarry.Get(owner, "Weapons") as IEnumerable ?? new object[0])
                if (ModStateCarry.Get(w, "uid") as string == r.Uid) { weapon = w; break; }
            if (weapon == null) { Unmapped("weapon", r.Owner + "/" + r.Uid); return null; }
            object def = deferredOf.Invoke(null, new[] { weapon });
            if (def == null || ModStateCarry.Get(def, "id") as string != r.Def) { Unmapped("def", r.Def + "|weapon has " + (ModStateCarry.Get(def, "id") ?? "none")); return null; }
            object anc = null;
            if (r.Target.Length > 0)
            {
                object target = r.Target == "?" ? null : find.Invoke(combat, new object[] { r.Target, false });
                object rep = ModStateCarry.Get(target, "GameRep");
                anc = rep == null || rep.Equals(null) ? null : ModStateCarry.Get(rep, "transform");
                if (anc == null) { Unmapped("target", r.Target); return null; }
            }
            object offset = ParseVec(r.Offset);
            if (offset == null) { Unmapped("offset", r.Offset); return null; }
            // As CreateDifferedEffect: new GameObject(), SetActive(false), AddComponent<DeferredEffect>().
            object go = Activator.CreateInstance(gameObjectType);
            gameObjectType.GetMethod("SetActive", new[] { typeof(bool) }).Invoke(go, new object[] { false });
            object effect = gameObjectType.GetMethod("AddComponent", new[] { typeof(Type) }).Invoke(go, new object[] { effectType });
            pWeapon.GetSetMethod(true).Invoke(effect, new[] { weapon });
            pDefinition.GetSetMethod(true).Invoke(effect, new[] { def });
            fStarted.SetValue(effect, r.Started);
            pT.GetSetMethod(true).Invoke(effect, new object[] { 0f });
            pDamageApplied.GetSetMethod(true).Invoke(effect, new object[] { false });
            fSeq.SetValue(effect, r.Seq);
            pAncor.GetSetMethod(true).Invoke(effect, new[] { anc });
            fOffset.SetValue(effect, offset);
            return effect;
        }

        static void DestroyOwner(object component)
        {
            try
            {
                object go = ModStateCarry.Get(component, "gameObject");
                MethodInfo destroy = ModAssemblyHook.GameType("UnityEngine.Object")?.GetMethod("Destroy", new[] { ModAssemblyHook.GameType("UnityEngine.Object") });
                if (go != null && destroy != null) destroy.Invoke(null, new[] { go });
            }
            catch (Exception e) { log(Tag + "|apply-failed|destroy|" + ModStateCarry.Inner(e)); }
        }

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

        static string Vec(object v)
        {
            if (v == null) return "0_0_0";
            string F(string c) => ((float)v.GetType().GetField(c).GetValue(v)).ToString("R", CultureInfo.InvariantCulture);
            return F("x") + "_" + F("y") + "_" + F("z");
        }

        static object ParseVec(string s)
        {
            string[] p = s.Split('_');
            if (p.Length != 3) return null;
            float[] c = new float[3];
            for (int i = 0; i < 3; i++) if (!float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out c[i])) return null;
            return Activator.CreateInstance(vector3, new object[] { c[0], c[1], c[2] });
        }

        static void Unmapped(string what, string key) { if (++rows <= 200) log(Tag + "|unmapped|" + what + "|" + key); }

        static void Row(string kind, List<Rec> recs, int round, string at)
        {
            if (++rows > 200) return;
            var parts = new List<string>();
            foreach (Rec r in recs) parts.Add(Key(r));
            log(Tag + "|" + kind + "|n=" + recs.Count + "|" + string.Join(",", parts.ToArray()) + "|round=" + round + "|at=" + at);
        }

        sealed class SameObject : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }
}
