// CBTBehaviorsEnhanced fall and melee damage locations in co-op (gh issue 24, W-L7 compare 218; .hermes/reports/issue24-WL7-residual-read-1.md B2).
// CBTBehaviorsEnhanced deals the damage of a fall (MechFallSequence.OnComplete prefix) and its melee and DFA extra clusters
// (MechMeleeSequence / MechDFASequence.OnMeleeComplete prefixes) through AttackHelper.CreateImaginaryAttack. That method rolls each
// cluster's location with its own CBTBehaviorsEnhanced.Mod.Random (IL_05BF ldsfld, NextDouble, then HitLocation.GetHitLocation), a
// static readonly System.Random built by Mod's type initializer with no seed (new System.Random(), time-seeded per process). CAC's
// GenerateHitInfo returns location 0 for that attack, so the CAC impact adapter never covers it. In W-L7 the same knockdown of the
// Blackjack put the two 25-point clusters on LT and RA on the host and on LA and LT on the guest, with equal position, rotation and
// attack direction, and the MECHS compare resynced. It happens on every fall in BTA co-op.
// This adapter, only when an assembly named CBTBehaviorsEnhanced is loaded and only in a co-op combat, seeds that generator around
// CreateImaginaryAttack from contract GUID, attacker and target GUID, melee and damage type, cluster count, round and phase (all equal
// on both peers), and puts the peer's own generator state back in a finalizer. Mod.Random is readonly, so the adapter copies the state
// of a new System.Random(seed) into that instance (_seedArray, _inext, _inextp) instead of replacing the field. Its other user,
// QuipHelper.PublishQuip (quip text only), keeps the peer's own sequence. The patch is installed at the first combat
// (ModAssemblyHook.AtFirstCombat), after CBTBehaviorsEnhanced's Init, so none of its type initializers runs early (fault M). Single
// player is untouched.
using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CBTBehaviorsEnhancedCoopDeterminism
    {
        const string OwnId = "battletech.port.cbtbe-coop";
        const string Tag = "cbtbe-coop";
        static Action<string> log;
        static Assembly cbtbe;
        static PropertyInfo battleTechGame;
        static FieldInfo modRandom, seedArray, inext, inextp;
        static int rows;

        sealed class Saved { public int[] Seeds; public int Next, NextP; }

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("CBTBehaviorsEnhanced", a => { cbtbe = a; ModAssemblyHook.AtFirstCombat(Apply, logLine, Tag); }, logLine, Tag);
        }

        static void Apply()
        {
            const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic;
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", stat);
            modRandom = cbtbe.GetType("CBTBehaviorsEnhanced.Mod", false)?.GetField("Random", stat);
            seedArray = typeof(Random).GetField("_seedArray", inst);
            inext = typeof(Random).GetField("_inext", inst);
            inextp = typeof(Random).GetField("_inextp", inst);
            MethodInfo attack = null;
            Type helper = cbtbe.GetType("CBTBehaviorsEnhanced.Helper.AttackHelper", false);
            if (helper != null)
                foreach (MethodInfo m in helper.GetMethods(stat | BindingFlags.DeclaredOnly))
                    if (m.Name == "CreateImaginaryAttack" && m.GetParameters().Length == 7) attack = m;
            if (battleTechGame == null || modRandom == null || modRandom.FieldType != typeof(Random) || seedArray == null || inext == null || inextp == null
                || attack == null)
            {
                log(Tag + "|skipped|shape changed BattleTechGame=" + (battleTechGame != null) + " Mod.Random=" + (modRandom != null)
                    + " Random._seedArray/_inext/_inextp=" + (seedArray != null && inext != null && inextp != null)
                    + " AttackHelper.CreateImaginaryAttack=" + (attack != null));
                return;
            }
            new Harmony(OwnId).Patch(attack,
                prefix: new HarmonyMethod(typeof(CBTBehaviorsEnhancedCoopDeterminism).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(CBTBehaviorsEnhancedCoopDeterminism).GetMethod(nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
            log(Tag + "|installed|" + cbtbe.GetName().Version + "|AttackHelper.CreateImaginaryAttack: Mod.Random seeded in co-op combat");
        }

        // __args: attacker, weapon, target, int, damageClusters, damageType, meleeAttackType. __state holds the peer's own generator state.
        static void Prefix(object[] __args, ref object __state)
        {
            __state = null;
            try
            {
                object combat = Get(battleTechGame.GetValue(null, null), "Combat");
                object net = Get(combat, "NetworkGameInstance");
                if (net == null || !(Get(net, "IsCoopCombat") is bool coop) || !coop) return;
                object turn = Get(combat, "TurnDirector");
                string where = Get(__args[0], "GUID") + "|" + Get(__args[2], "GUID") + "|" + __args[6] + "|" + __args[5] + "|"
                    + ((__args[4] as ICollection)?.Count ?? -1) + "|r" + Get(turn, "CurrentRound") + "|p" + Get(turn, "CurrentPhase");
                string full = (Get(Get(combat, "ActiveContract"), "GUID") as string) + "|cbtbe|" + where;
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < full.Length; i++) h = (h ^ full[i]) * 1099511628211UL;
                int seed = (int)(uint)(h ^ (h >> 32)) & int.MaxValue;
                var target = (Random)modRandom.GetValue(null);
                var saved = new Saved { Seeds = (int[])((int[])seedArray.GetValue(target)).Clone(), Next = (int)inext.GetValue(target), NextP = (int)inextp.GetValue(target) };
                var fresh = new Random(seed);
                seedArray.SetValue(target, ((int[])seedArray.GetValue(fresh)).Clone());
                inext.SetValue(target, inext.GetValue(fresh));
                inextp.SetValue(target, inextp.GetValue(fresh));
                __state = saved;
                if (++rows <= 80) log(Tag + "|seed|" + where + "|" + seed);
            }
            catch (Exception e) { log(Tag + "|prefix-failed|" + e.Message); }   // a saved state is still put back
        }

        static void Finalizer(object __state)
        {
            if (!(__state is Saved s)) return;
            try
            {
                var target = (Random)modRandom.GetValue(null);
                seedArray.SetValue(target, s.Seeds);
                inext.SetValue(target, s.Next);
                inextp.SetValue(target, s.NextP);
            }
            catch (Exception e) { log(Tag + "|restore-failed|" + e.Message); }
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
