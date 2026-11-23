// SoldiersPiratesAssassinsMercs (SPAM) co-op determinism (gh issue 24; Sam, 2026-11-19: co-op desyncs are fixed, not hidden).
// SPAM's prefix on Contract.BeginRequestResources(bool) may replace the contract's opposing faction (planet-alternate opfor, merc lances, hostile-to-all). Whether it does,
// and with which faction, is drawn from ModInit.Random, a System.Random created by "new Random()" (time seeded, one per process), plus UnityEngine.Random through
// Utilities.GetRandomElement. UnityEngine.Random is already seeded from the contract GUID around that method by MissionControlCoopDeterminism (same Priority.First prefix);
// ModInit.Random is not, so two co-op peers entered BeginRequestResources with target faction Liao and left it with LethalInjection (host) and WarriorHouseImarra (guest)
// (pair-rp-statscope1, contract Bodyguards): other enemy candidate lists, other units, a desync before the first round.
// What it does, only when an assembly named SoldiersPiratesAssassinsMercs is loaded: every read of the static field ModInit.Random in SPAM's own methods (a transpiler,
// ldsfld -> a call) takes a System.Random seeded from the contract GUID and a phase key while a co-op BeginRequestResources or EncounterLayerParent.InitializeContract is
// running, and the mod's own instance otherwise. The scope ends with the method, so single-player, a game without the mod, and every other use of the field are untouched.
// Both peers run the same draws in the same order from the same seed, so they agree as long as the inputs of the draw (the contract, the system, the mod's settings) agree.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace BattleTechPort
{
    public static class SpamCoopDeterminism
    {
        const string OwnId = "battletech.port.spam-coop";
        static Action<string> log;
        static FieldInfo randomField;
        static string salt;                 // contract GUID of the co-op contract being run; null = not co-op
        static Random scoped;                // non-null while a scoped method runs
        static readonly MethodInfo currentRandom = typeof(SpamCoopDeterminism).GetMethod(nameof(CurrentRandom), BindingFlags.Static | BindingFlags.NonPublic);

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("SoldiersPiratesAssassinsMercs", Apply, logLine, "spam-coop");
        }

        static void Apply(Assembly spam)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type modInit = spam.GetType("SoldiersPiratesAssassinsMercs.ModInit", false);
            randomField = modInit?.GetField("Random", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Type contract = ModAssemblyHook.GameType("BattleTech.Contract");
            Type layerParent = ModAssemblyHook.GameType("BattleTech.EncounterLayerParent");
            MethodInfo begin = contract?.GetMethod("BeginRequestResources", all, null, new[] { typeof(bool) }, null);
            MethodInfo init = layerParent?.GetMethod("InitializeContract", all);
            if (randomField == null || randomField.FieldType != typeof(Random) || begin == null || init == null)
            {
                log("spam-coop|skipped|shape changed ModInit.Random=" + (randomField != null) + " BeginRequestResources(bool)=" + (begin != null) + " InitializeContract=" + (init != null));
                return;
            }
            var harmony = new Harmony(OwnId);
            HarmonyMethod Mine(string name, int priority) => new HarmonyMethod(typeof(SpamCoopDeterminism).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };
            harmony.Patch(begin, prefix: Mine(nameof(BeginPrefix), Priority.First), finalizer: Mine(nameof(Finalizer), Priority.Last));
            harmony.Patch(init, prefix: Mine(nameof(InitPrefix), Priority.First), finalizer: Mine(nameof(Finalizer), Priority.Last));
            int sites = 0, reads = 0;
            var transpiler = Mine(nameof(Transpiler), Priority.Normal);
            foreach (Type t in Types(spam))
                foreach (MethodBase m in Methods(t, all))
                {
                    int n = CountReads(m);
                    if (n == 0) continue;
                    try { harmony.Patch(m, transpiler: transpiler); sites++; reads += n; }
                    catch (Exception e) { log("spam-coop|site-failed|" + t.FullName + "::" + m.Name + "|" + e.Message); }
                }
            log("spam-coop|installed|ModInit.Random reads redirected: " + reads + " in " + sites + " methods; scope Contract.BeginRequestResources and EncounterLayerParent.InitializeContract");
        }

        static IEnumerable<Type> Types(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, x => x != null); }
        }

        static IEnumerable<MethodBase> Methods(Type t, BindingFlags all)
        {
            if (t.ContainsGenericParameters) yield break;
            foreach (MethodBase m in t.GetMethods(all)) if (!m.IsAbstract && m.GetMethodBody() != null && !m.ContainsGenericParameters) yield return m;
            foreach (MethodBase m in t.GetConstructors(all)) if (m.GetMethodBody() != null) yield return m;
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

        // Called in place of the ldsfld ModInit.Random. Outside this adapter's two scopes, a CoopRngSweepDeterminism scope (the merc bribe rolls
        // under TurnDirector.IncrementActiveTurnActor) supplies the instance.
        static Random CurrentRandom() { return scoped ?? CoopRngSweepDeterminism.ScopedRandom(randomField) ?? (Random)randomField.GetValue(null); }

        static ulong Fnv64(string text, ulong basis)
        {
            ulong h = basis;
            for (int i = 0; i < text.Length; i++) h = (h ^ text[i]) * 1099511628211UL;
            return h;
        }

        static Random SeededFor(string phase)
        {
            ulong h = Fnv64(salt + "|spam|" + phase, 14695981039346656037UL);
            return new Random((int)(uint)(h ^ (h >> 32)));
        }

        static void BeginPrefix(object __instance, ref bool __state)
        {
            __state = false;
            try
            {
                salt = null;
                object net = Get(Get(__instance, "BattleTechGame"), "NetworkGameInstance");
                if (net == null || !(Get(net, "IsCoopCombat") is bool coop) || !coop) return;
                salt = Get(__instance, "GUID") as string;
                if (salt == null) return;
                scoped = SeededFor("begin"); __state = true;
                log("spam-coop|seed|BeginRequestResources|contract=" + salt);
            }
            catch (Exception e) { log("spam-coop|begin-prefix-failed|" + e.Message); }
        }

        static void InitPrefix(ref bool __state)
        {
            __state = false;
            try
            {
                if (salt == null) return;
                scoped = SeededFor("init"); __state = true;
                log("spam-coop|seed|InitializeContract|contract=" + salt);
            }
            catch (Exception e) { log("spam-coop|init-prefix-failed|" + e.Message); }
        }

        static void Finalizer(bool __state) { if (__state) scoped = null; }

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
