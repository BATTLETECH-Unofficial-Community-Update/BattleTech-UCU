// IRTweaks co-op determinism (gh issue 24 fault M; .hermes/reports/issue24-faultM-read-1.md).
// IRTweaks draws every combat roll from IRTweaks.Mod.Random (public static readonly, a time-seeded `new Random()`, Mod.cs:105), one per
// process: its pilot-skill bonuses to a hit's damage, heat, AP and stability (CAC damage modifiers IRT_CAC_Skill*DmgMod, registered with
// CustomAmmoCategories' DamageModifiersCache and called per hit from AdvWeaponHitInfoRec.ApplyVariance -> DamageModifiers.Calculate), the
// self-knockdown and forced-shutdown checks in its AttackDirector.OnAttackComplete prefix, and the pain-tolerance check in its
// Pilot.InjurePilot prefix (PainHelper.MakeResistCheck). So two co-op peers rolled each one separately: S2 #10, the Viper's ATM hit the
// Stormcrow with a 15% x1.15 stability bonus (BTA's GunneryStabBonus1) on one peer only (CAC_WeaponStabilityDamage 2 / 2.15, Stormcrow
// stability 1 / 1.075).
// While a co-op combat runs (NetworkGameInstance.IsCoopCombat), and only when an assembly named IRTweaks is loaded, each of those three entry
// points is a seeded scope: a System.Random seeded from the contract GUID, the scope and identifiers both peers share (the hit's attack
// sequence, group, weapon and hit index; the completed sequence; the pilot and its injury count), plus the round and phase. Every
// `ldsfld IRTweaks.Mod::Random` in IRTweaks' own methods becomes a call that returns the current scope's instance, or IRTweaks' own instance
// outside a scope (HUD damage previews call the same modifiers through Calculate(ui: true), outside ApplyVariance, on one peer only, so they
// must not touch a shared stream). The readonly field is never written. The pattern is SpamCoopDeterminism's. Single player, a game without
// IRTweaks, and every draw outside the three scopes are untouched.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace BattleTechPort
{
    public static class IRTweaksCoopDeterminism
    {
        const string OwnId = "battletech.port.irtweaks-coop";
        static Action<string> log;
        static FieldInfo randomField;
        static Random scoped;
        static Harmony harmony;
        static PropertyInfo battleTechGame;
        static int seedRows;
        static readonly MethodInfo currentRandom = typeof(IRTweaksCoopDeterminism).GetMethod(nameof(CurrentRandom), BindingFlags.Static | BindingFlags.NonPublic);

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("IRTweaks", irt => ModAssemblyHook.AtFirstCombat(() => Apply(irt), logLine, "irtweaks-coop"), logLine, "irtweaks-coop");
        }

        // Runs at the first combat, after ModTek has called IRTweaks' Init: patching IRTweaks' methods when its assembly loads ran
        // IRTweaks.ModState's type initializer before Init (it threw, Init failed, and every mod that depends on IRTweaks was skipped:
        // S1/R1 host, the BT Advanced contract types missing).
        static void Apply(Assembly irt)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            randomField = irt.GetType("IRTweaks.Mod", false)?.GetField("Random", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo complete = ModAssemblyHook.GameType("BattleTech.AttackDirector")?.GetMethod("OnAttackComplete", all);
            MethodInfo injure = ModAssemblyHook.GameType("BattleTech.Pilot")?.GetMethod("InjurePilot", all);
            if (randomField == null || randomField.FieldType != typeof(Random) || complete == null || injure == null)
            {
                log("irtweaks-coop|skipped|shape changed Mod.Random=" + (randomField != null) + " AttackDirector.OnAttackComplete=" + (complete != null)
                    + " Pilot.InjurePilot=" + (injure != null));
                return;
            }
            harmony = new Harmony(OwnId);
            harmony.Patch(complete, prefix: Mine(nameof(CompletePrefix), Priority.First), finalizer: Mine(nameof(Finalizer), Priority.Last));
            harmony.Patch(injure, prefix: Mine(nameof(InjurePrefix), Priority.First), finalizer: Mine(nameof(Finalizer), Priority.Last));
            int sites = 0, reads = 0;
            HarmonyMethod transpiler = Mine(nameof(Transpiler), Priority.Normal);
            foreach (Type t in Types(irt))
                foreach (MethodBase m in Methods(t, all))
                {
                    int n = CountReads(m);
                    if (n == 0) continue;
                    try { harmony.Patch(m, transpiler: transpiler); sites++; reads += n; log("irtweaks-coop|site|" + t.FullName + "::" + m.Name + "|reads=" + n); }
                    catch (Exception e) { log("irtweaks-coop|site-failed|" + t.FullName + "::" + m.Name + "|" + e.Message); }
                }
            log("irtweaks-coop|installed|Mod.Random reads redirected: " + reads + " in " + sites + " methods; scope AttackDirector.OnAttackComplete, Pilot.InjurePilot");
            // The skill damage modifiers run per hit inside CustomAmmoCategories' hit resolution.
            ModAssemblyHook.When("CustomAmmoCategories", ApplyCac, log, "irtweaks-coop-cac");   // already loaded by now: runs at once
        }

        static void ApplyCac(Assembly cac)
        {
            MethodInfo variance = cac.GetType("CustAmmoCategories.AdvWeaponHitInfoRec", false)?.GetMethod("ApplyVariance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (variance == null) { log("irtweaks-coop|cac-skipped|shape changed AdvWeaponHitInfoRec.ApplyVariance=false"); return; }
            harmony.Patch(variance, prefix: Mine(nameof(HitPrefix), Priority.First), finalizer: Mine(nameof(Finalizer), Priority.Last));
            log("irtweaks-coop|installed|scope CustAmmoCategories AdvWeaponHitInfoRec.ApplyVariance (per hit)");
        }

        static HarmonyMethod Mine(string name, int priority)
            => new HarmonyMethod(typeof(IRTweaksCoopDeterminism).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };

        static IEnumerable<Type> Types(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, x => x != null); }
        }

        static IEnumerable<MethodBase> Methods(Type t, BindingFlags all)
        {
            if (t.ContainsGenericParameters) yield break;
            foreach (MethodBase m in t.GetMethods(all)) if (!m.IsAbstract && m.GetMethodBody() != null && !m.ContainsGenericParameters) yield return m;
            foreach (MethodBase m in t.GetConstructors(all)) if (m.GetMethodBody() != null && !m.IsStatic) yield return m;
        }

        static int CountReads(MethodBase m)
        {
            try
            {
                int n = 0;
                foreach (CodeInstruction ins in PatchProcessor.GetOriginalInstructions(m)) if (ins.opcode == OpCodes.Ldsfld && ins.operand as FieldInfo == randomField) n++;
                return n;
            }
            catch { return 0; }
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.opcode == OpCodes.Ldsfld && ins.operand as FieldInfo == randomField)
                {
                    var call = new CodeInstruction(OpCodes.Call, currentRandom);
                    call.labels.AddRange(ins.labels);
                    call.blocks.AddRange(ins.blocks);
                    yield return call;
                }
                else yield return ins;
            }
        }

        // Called in place of the ldsfld IRTweaks.Mod::Random.
        static Random CurrentRandom() { return scoped ?? (Random)randomField.GetValue(null); }

        // Opens a scope seeded from the contract and key in a co-op combat; __state keeps the enclosing scope (an injury inside a hit).
        static void Open(string key, ref object __state)
        {
            __state = null;
            try
            {
                if (battleTechGame == null)
                    battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                object combat = Get(battleTechGame?.GetValue(null, null), "Combat");
                object net = Get(combat, "NetworkGameInstance");
                if (net == null || !(Get(net, "IsCoopCombat") is bool coop) || !coop) return;
                object turn = Get(combat, "TurnDirector");
                string contract = Get(Get(combat, "ActiveContract"), "GUID") as string ?? "";
                string full = contract + "|irt|" + key + "|r" + Get(turn, "CurrentRound") + "|p" + Get(turn, "CurrentPhase");
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < full.Length; i++) h = (h ^ full[i]) * 1099511628211UL;
                __state = new object[] { scoped };
                scoped = new Random((int)(uint)(h ^ (h >> 32)));
                if (++seedRows <= 30) log("irtweaks-coop|seed|" + key);
            }
            catch (Exception e) { log("irtweaks-coop|scope-failed|" + key + "|" + e.Message); }
        }

        static void HitPrefix(object __instance, ref object __state)
        {
            object parent = Get(__instance, "parent");
            Open("hit|" + Get(parent, "attackSequenceId") + "|" + Get(parent, "groupIdx") + "|" + Get(parent, "weaponIdx") + "|" + Get(__instance, "hitIndex")
                + "|" + Get(__instance, "isAOE") + "|" + Get(__instance, "AOEKey"), ref __state);
        }

        static void CompletePrefix(object message, ref object __state) => Open("complete|" + Get(message, "sequenceId"), ref __state);

        static void InjurePrefix(object __instance, ref object __state) => Open("injure|" + Get(__instance, "GUID") + "|" + Get(__instance, "Injuries"), ref __state);

        static void Finalizer(object __state) { if (__state is object[] previous) scoped = previous[0] as Random; }

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
