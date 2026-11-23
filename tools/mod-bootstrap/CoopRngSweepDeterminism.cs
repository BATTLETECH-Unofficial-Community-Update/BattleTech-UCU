// Mod random draws left unseeded after the per-mod adapters (gh issue 24 RNG sweep; .hermes/reports/issue24-sweep-1/rng-draws.tsv).
// A sweep of every mod assembly BTA loads in combat (decompile/, scan-rng.py) found these draws, which run on both co-op peers and change game
// state, outside every existing seed scope (CAC impact, panic, tbas, CBTBE, FieldRepairs, IRTweaks, SPAM, StrategicOperations, MissionControl):
//  - TurnDirector.IncrementActiveTurnActor: ConcreteJungle's ambush roll, ambush type, spawn count and unit picks (ConcreteJungle.Mod.Random, a
//    time-seeded static System.Random, plus List.Shuffle on UnityEngine.Random), its ambush lance GUID (Guid.NewGuid); SPAM's merc bribe rolls
//    (ModInit.Random, read through SpamCoopDeterminism.CurrentRandom, which asks ScopedRandom here).
//  - TurnDirector.StartFirstRound: CollateralDamage's objective roll and destruction threshold and cap (ModInit.Random, UnityEngine.Random).
//  - Team.AddUnit: TisButAScratch's overcrowding effect pick (GetRandomElement) and its AI pilot iGUID tag, Abilifier's RESOLVERATOR tag (Guid).
//  - MechStartupInvocation.Invoke, MechStandInvocation.Invoke: CBTBehaviorsEnhanced's system failure component pick (GetRandomElement) and CAE's
//    stand-up roll (UnityEngine.Random.Range).
//  - AbstractActor.OnNewRound(int): CAE's AI activation pass (CAEAIHelper.AIActivatableProc) from its prefix there, on every peer.
//  - CAE ActivatableComponent.activateComponent and AoEComponentExplodeHelper.AoEExplodeComponent: activation fail roll, fail crit slot, AoE
//    explosion roll and building hit points (UnityEngine.Random.Range), including activations from damage, heat and phase start.
//  - Ability.Activate(AbstractActor, ICombatant), AbstractActor.HandleDeath(string), MechJumpSequence.CompleteOrders and StrategicOperations'
//    StrategicMovementSequence.CompleteOrders: StrategicOperations' deswarm cluster locations, battle armor dismount hexes and mounted
//    location picks (GetRandomElement, UnityEngine.Random.Range; its Mod.Random reads are StrategicOperationsCoopDeterminism's).
//  - MissionControl TagUnitsInRegionResult.Trigger and SetUnitsInRegionToBeTaggedObjectiveTargetsResult.Trigger (which buildings become
//    objective targets, List.Shuffle), SetStatusAtRandomResult.Trigger, TriggerResultAtRandomResult.Trigger and SetChunkStateAtRandomActivator
//    Update / Execute (which encounter object, result or chunk, ListExtensions.GetRandom): encounter results fired mid-combat.
//  - Guid.NewGuid in SoldiersPiratesAssassinsMercs (lance and objective GUIDs in its MissionControl constructor prefix), MonsterMashup (linked
//    lances), ConcreteJungle, TisButAScratch, Abilifier and CustomUnits' HotDropPoint.OnDepsLoaded / DeployManualHelper.SpawnDeployDirector
//    (unit GUIDs).
// Each scope, only in a co-op combat (NetworkGameInstance.IsCoopCombat), saves UnityEngine.Random.state and calls InitState from FNV(contract GUID
// | site | key both peers share: actor or invocation GUID, ability, region, round, phase, turn actor index) in a Priority.First prefix, and puts
// the peer's own state back in a Priority.Last finalizer, so every mod prefix and postfix on the method is inside it. Reads of a listed static
// System.Random field in that mod's methods return, inside a scope, a System.Random seeded from the scope's seed and the field, and the field's
// own instance outside. A redirected Guid.NewGuid returns FNV(contract | scope key | site | n) inside a scope, FNV(contract | site | n) outside
// one, and a real GUID outside a co-op combat. Patches go in at the first combat (ModAssemblyHook.AtFirstCombat), after each mod's Init (fault M).
// Rows: "<mod>-coop|seed|<site>|<key>|<seed>" and "<mod>-coop|guid|<site>|<n>|<scope>", up to 40 per site. Single player is untouched.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CoopRngSweepDeterminism
    {
        const string OwnId = "battletech.port.rngsweep-coop";
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        delegate string KeyOf(object instance, object[] args, object turn);
        sealed class Site { public string Tag, Name; public KeyOf Key; }
        sealed class Frame { public Frame Outer; public string Key; public int Seed; public object UnityState; public Dictionary<string, Random> Randoms; public Dictionary<string, int> Guids; }

        static Action<string> log;
        static Harmony harmony;
        static PropertyInfo randomState, battleTechGame;
        static MethodInfo initState;
        static readonly Dictionary<MethodBase, Site> sites = new Dictionary<MethodBase, Site>();
        static readonly List<FieldInfo> fields = new List<FieldInfo>();
        static readonly Dictionary<string, string> guidTags = new Dictionary<string, string>();
        static readonly Dictionary<string, int> guidCounts = new Dictionary<string, int>(), rowCounts = new Dictionary<string, int>();
        static string guidContract;
        // Co-op state of the running combat, read once per combat: 0 not read yet, 1 co-op (coopContract set), 2 not co-op. Reset at every
        // CombatGameState.FirstTimeInit, _InitFromSave and OnCombatGameDestroyed, so a single-player combat costs one read, not one per call.
        static int coopState;
        static string coopContract;
        static readonly Dictionary<KeyValuePair<Type, string>, MemberInfo> members = new Dictionary<KeyValuePair<Type, string>, MemberInfo>();
        static Frame current;
        static readonly MethodInfo fieldRandom = typeof(CoopRngSweepDeterminism).GetMethod(nameof(FieldRandom), BindingFlags.Static | BindingFlags.NonPublic);
        static readonly MethodInfo newGuidFor = typeof(CoopRngSweepDeterminism).GetMethod(nameof(NewGuidFor), BindingFlags.Static | BindingFlags.NonPublic);
        static readonly MethodInfo guidNewGuid = typeof(Guid).GetMethod("NewGuid", Type.EmptyTypes);

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            Arm("ConcreteJungle", "concretejungle-coop", a => { Turn("concretejungle-coop"); Field(a, "ConcreteJungle.Mod", "Random", "concretejungle-coop"); Guids(a, null, "concretejungle-coop"); });
            Arm("SoldiersPiratesAssassinsMercs", "spam-coop", a => { Turn("spam-coop"); Guids(a, null, "spam-coop"); });
            Arm("CollateralDamage", "collateraldamage-coop", a =>
            {
                Scope(Game("BattleTech.TurnDirector", "StartFirstRound"), "collateraldamage-coop", "first-round", (i, x, t) => "r" + Get(t, "CurrentRound"));
                Field(a, "CollateralDamage.ModInit", "Random", "collateraldamage-coop");
            });
            Arm("TisButAScratch", "tbas-coop", a => { AddUnit("tbas-coop"); Guids(a, null, "tbas-coop"); });
            Arm("Abilifier", "abilifier-coop", a => { AddUnit("abilifier-coop"); Guids(a, null, "abilifier-coop"); });
            Arm("CBTBehaviorsEnhanced", "cbtbe-coop", a => Invocations("cbtbe-coop"));
            Arm("CustomActivatableEquipment", "cae-coop", a =>
            {
                Invocations("cae-coop");
                // CAE's AbstractActor.OnNewRound prefix runs CAEAIHelper.AIActivatableProc on every peer (AICheatRoll, test rolls, activations);
                // both peers share LocalPlayerTeamGuid, so both treat the same units as AI. The same method CAE patches is scoped here.
                Scope(Game("BattleTech.AbstractActor", "OnNewRound", typeof(int)), "cae-coop", "new-round", (i, x, t) => Get(i, "GUID") + "|r" + x[0]);
                Type component = ModAssemblyHook.GameType("BattleTech.MechComponent");
                Scope(a.GetType("CustomActivatableEquipment.ActivatableComponent", false)?.GetMethod("activateComponent", All, null, new[] { component, typeof(bool), typeof(bool) }, null),
                    "cae-coop", "activate", (i, x, t) => Component(x[0]) + "|" + x[1] + "|" + x[2] + RoundPhase(t));
                Scope(a.GetType("CustomActivatableEquipment.AoEComponentExplodeHelper", false)?.GetMethod("AoEExplodeComponent", All, null, new[] { component }, null),
                    "cae-coop", "aoe-explode", (i, x, t) => Component(x[0]) + RoundPhase(t));
            });
            Arm("StrategicOperations", "strategicops-coop", a =>
            {
                Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor"), combatant = ModAssemblyHook.GameType("BattleTech.ICombatant");
                Scope(Game("BattleTech.Ability", "Activate", actor, combatant), "strategicops-coop", "ability",
                    (i, x, t) => Get(Get(i, "Def"), "Id") + "|" + Get(x[0], "GUID") + "|" + Get(x[1], "GUID") + RoundPhase(t));
                Scope(Game("BattleTech.AbstractActor", "HandleDeath", typeof(string)), "strategicops-coop", "death", (i, x, t) => Get(i, "GUID") + "|" + x[0] + RoundPhase(t));
                Scope(Game("BattleTech.MechJumpSequence", "CompleteOrders"), "strategicops-coop", "jump", (i, x, t) => Get(Get(i, "OwningMech"), "GUID") + RoundPhase(t));
                Scope(a.GetType("StrategicOperations.Framework.Classes+StrategicMovementSequence", false)?.GetMethod("CompleteOrders", All, null, Type.EmptyTypes, null),
                    "strategicops-coop", "so-move", (i, x, t) => Get(Get(i, "owningActor"), "GUID") + RoundPhase(t));
            });
            Arm("MissionControl", "missioncontrol-coop", a =>
            {
                Type message = ModAssemblyHook.GameType("MessageCenterMessage");
                foreach (string result in new[] { "MissionControl.Result.TagUnitsInRegionResult", "MissionControl.Result.SetUnitsInRegionToBeTaggedObjectiveTargetsResult" })
                    Scope(a.GetType(result, false)?.GetMethod("Trigger", All, null, new[] { message, typeof(string) }, null), "missioncontrol-coop", "tag-region",
                        (i, x, t) => Get(i, "RegionGuid") + "|" + Get(i, "Type") + "|" + x[1]);
                Scope(a.GetType("MissionControl.Result.SetStatusAtRandomResult", false)?.GetMethod("Trigger", All, null, new[] { message, typeof(string) }, null),
                    "missioncontrol-coop", "status-at-random", (i, x, t) => Join(Get(i, "EncounterGuids")) + "|" + Get(i, "Status") + "|" + x[1]);
                Scope(a.GetType("MissionControl.Result.TriggerResultAtRandomResult", false)?.GetMethod("Trigger", All, null, new[] { message, typeof(string) }, null),
                    "missioncontrol-coop", "result-at-random", (i, x, t) => Join(Get(i, "Results")) + "|" + x[1]);
                Type chunk = a.GetType("MissionControl.LogicComponents.Activators.SetChunkStateAtRandomActivator", false);
                // Update runs every frame: outside a co-op combat the prefix returns on the cached co-op state with no reflection; in co-op a scope
                // opens only on the frame it can pick (chunk set, not activated yet), keyed by the activator, not the frame.
                Scope(chunk?.GetMethod("Update", All, null, Type.EmptyTypes, null), "missioncontrol-coop", "chunk-at-random",
                    (i, x, t) => Get(i, "chunk") == null || Get(i, "HasActivated") is bool done && done ? null : Get(i, "encounterObjectGuid") + "|" + Join(Get(i, "ChunkGuids")));
                Scope(chunk?.GetMethod("Execute", All, null, Type.EmptyTypes, null), "missioncontrol-coop", "chunk-at-random-exec",
                    (i, x, t) => Get(i, "encounterObjectGuid") + "|" + Join(Get(i, "ChunkGuids")));
            });
            Arm("MonsterMashup", "monstermashup-coop", a => Guids(a, null, "monstermashup-coop"));
            Arm("CustomUnits", "customunits-coop", a => Guids(a, new[] { "CustomUnits.HotDropPoint::OnDepsLoaded", "CustomUnits.DeployManualHelper::SpawnDeployDirector" }, "customunits-coop"));
        }

        // Called by SpamCoopDeterminism.CurrentRandom: the open scope's instance for that field, or null outside a scope.
        internal static Random ScopedRandom(FieldInfo field)
        {
            Frame f = current;
            if (f == null || field == null) return null;
            string name = field.DeclaringType.FullName + "::" + field.Name;
            if (f.Randoms == null) f.Randoms = new Dictionary<string, Random>();
            if (!f.Randoms.TryGetValue(name, out Random r)) f.Randoms[name] = r = new Random(f.Seed ^ (int)(uint)Fnv(name));
            return r;
        }

        static void Arm(string assembly, string tag, Action<Assembly> install)
            // Init patches CombatGameState.FirstTimeInit and _InitFromSave, so it runs at the mod's assembly load: from AtFirstCombat it ran
            // inside FirstTimeInit's own prefix, and re-patching the method on the stack hung the main thread on both peers (CBTBE-10b).
            => ModAssemblyHook.When(assembly, a => { Init(); ModAssemblyHook.AtFirstCombat(() => install(a), log, tag); }, log, tag);

        static void Init()
        {
            if (harmony != null) return;
            const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type random = ModAssemblyHook.GameType("UnityEngine.Random");
            randomState = random?.GetProperty("state", stat);
            initState = random?.GetMethod("InitState", stat, null, new[] { typeof(int) }, null);
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", stat);
            harmony = new Harmony(OwnId);
            Type combat = ModAssemblyHook.GameType("BattleTech.CombatGameState");
            foreach (string name in new[] { "FirstTimeInit", "_InitFromSave", "OnCombatGameDestroyed" })
            {
                MethodInfo m = combat?.GetMethod(name, All);
                if (m == null) { log("rngsweep-coop|skipped|CombatGameState." + name + " not found: the co-op state is re-read on every call"); continue; }
                try { harmony.Patch(m, postfix: Mine(nameof(ResetCoop), Priority.Last)); }
                catch (Exception e) { log("rngsweep-coop|site-failed|CombatGameState." + name + " reset|" + e.Message); }
            }
            if (randomState == null || initState == null || battleTechGame == null)
                log("rngsweep-coop|skipped|shape changed Random.state=" + (randomState != null) + " InitState=" + (initState != null) + " BattleTechGame=" + (battleTechGame != null));
        }

        static void Turn(string tag) => Scope(Game("BattleTech.TurnDirector", "IncrementActiveTurnActor"), tag, "turn",
            (i, x, t) => "r" + Get(t, "CurrentRound") + "|p" + Get(t, "CurrentPhase") + "|a" + Get(t, "ActiveTurnActorIndex"));

        static void AddUnit(string tag) => Scope(Game("BattleTech.Team", "AddUnit", ModAssemblyHook.GameType("BattleTech.AbstractActor")), tag, "addunit", (i, x, t) => Get(x[0], "GUID") as string);

        static void Invocations(string tag)
        {
            foreach (string name in new[] { "MechStartupInvocation", "MechStandInvocation" })
                Scope(Game("BattleTech." + name, "Invoke", ModAssemblyHook.GameType("BattleTech.CombatGameState")), tag, name, (i, x, t) => Get(i, "MechGUID") + RoundPhase(t));
        }

        static MethodBase Game(string type, string method, params Type[] args)
        {
            Type t = ModAssemblyHook.GameType(type);
            if (t == null || Array.IndexOf(args, null) >= 0) return null;
            return t.GetMethod(method, All, null, args, null);
        }

        static void Scope(MethodBase m, string tag, string name, KeyOf key)
        {
            if (randomState == null || initState == null || battleTechGame == null) return;
            if (m == null) { log(tag + "|skipped|shape changed " + name + " not found"); return; }
            if (sites.ContainsKey(m)) { log(tag + "|scope|" + name + "|shared with " + sites[m].Tag); return; }
            sites[m] = new Site { Tag = tag, Name = name, Key = key };
            string prefix = m.IsStatic ? nameof(StaticPrefix) : nameof(InstancePrefix);
            try { harmony.Patch(m, prefix: Mine(prefix, Priority.First), finalizer: Mine(nameof(Finalizer), Priority.Last)); }
            catch (Exception e) { sites.Remove(m); log(tag + "|site-failed|scope " + name + "|" + e.Message); return; }   // the other sites still install
            log(tag + "|installed|scope " + name + " on " + m.DeclaringType.FullName + "." + m.Name);
        }

        static HarmonyMethod Mine(string name, int priority)
            => new HarmonyMethod(typeof(CoopRngSweepDeterminism).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };

        static void Field(Assembly a, string type, string name, string tag)
        {
            FieldInfo f = a.GetType(type, false)?.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null || f.FieldType != typeof(Random)) { log(tag + "|skipped|shape changed " + type + "." + name + "=" + (f != null)); return; }
            fields.Add(f);
            int methods = 0, reads = 0;
            foreach (MethodBase m in Methods(a, null))
            {
                int n = Count(m, ins => ins.opcode == OpCodes.Ldsfld && ins.operand as FieldInfo == f);
                if (n == 0) continue;
                try { harmony.Patch(m, transpiler: Mine(nameof(FieldTranspiler), Priority.Normal)); methods++; reads += n; }
                catch (Exception e) { log(tag + "|site-failed|" + m.DeclaringType.FullName + "::" + m.Name + "|" + e.Message); }
            }
            log(tag + "|installed|" + type + "." + name + " reads redirected: " + reads + " in " + methods + " methods");
        }

        static void Guids(Assembly a, string[] only, string tag)
        {
            int methods = 0, calls = 0;
            foreach (MethodBase m in Methods(a, only))
            {
                int n = Count(m, ins => (ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && ins.operand as MethodInfo == guidNewGuid);
                if (n == 0) continue;
                guidTags[Name(m)] = tag;
                try { harmony.Patch(m, transpiler: Mine(nameof(GuidTranspiler), Priority.Normal)); methods++; calls += n; }
                catch (Exception e) { log(tag + "|site-failed|" + Name(m) + "|" + e.Message); }
            }
            log(tag + "|installed|Guid.NewGuid calls redirected: " + calls + " in " + methods + " methods" + (only != null ? " of " + only.Length + " named" : ""));
        }

        static string Name(MethodBase m) => m.DeclaringType.FullName + "::" + m.Name;

        static IEnumerable<MethodBase> Methods(Assembly a, string[] only)
        {
            Type[] types;
            try { types = a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, x => x != null); }
            foreach (Type t in types)
            {
                if (t.ContainsGenericParameters) continue;
                var list = new List<MethodBase>();
                foreach (MethodBase m in t.GetMethods(All)) if (!m.IsAbstract && !m.ContainsGenericParameters && HasBody(m)) list.Add(m);
                foreach (MethodBase m in t.GetConstructors(All)) if (HasBody(m)) list.Add(m);
                foreach (MethodBase m in list) if (only == null || Array.IndexOf(only, Name(m)) >= 0) yield return m;
            }
        }

        // A body whose locals name a type that does not load (an optional dependency) throws here; skip that method, not the assembly.
        static bool HasBody(MethodBase m)
        {
            try { return m.GetMethodBody() != null; }
            catch { return false; }
        }

        static int Count(MethodBase m, Func<CodeInstruction, bool> match)
        {
            try { int n = 0; foreach (CodeInstruction ins in PatchProcessor.GetOriginalInstructions(m)) if (match(ins)) n++; return n; }
            catch { return 0; }
        }

        static IEnumerable<CodeInstruction> FieldTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction ins in instructions)
            {
                int idx = ins.opcode == OpCodes.Ldsfld ? fields.IndexOf(ins.operand as FieldInfo) : -1;
                if (idx < 0) { yield return ins; continue; }
                var first = new CodeInstruction(OpCodes.Ldc_I4, idx);
                first.labels.AddRange(ins.labels);
                first.blocks.AddRange(ins.blocks);
                yield return first;
                yield return new CodeInstruction(OpCodes.Call, fieldRandom);
            }
        }

        static IEnumerable<CodeInstruction> GuidTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            string site = Name(__originalMethod);
            foreach (CodeInstruction ins in instructions)
            {
                if (!((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && ins.operand as MethodInfo == guidNewGuid)) { yield return ins; continue; }
                var first = new CodeInstruction(OpCodes.Ldstr, site);
                first.labels.AddRange(ins.labels);
                first.blocks.AddRange(ins.blocks);
                yield return first;
                yield return new CodeInstruction(OpCodes.Call, newGuidFor);
            }
        }

        // In place of ldsfld <mod>.Random.
        static Random FieldRandom(int index) => ScopedRandom(fields[index]) ?? (Random)fields[index].GetValue(null);

        // In place of Guid.NewGuid() in a listed mod's methods.
        static Guid NewGuidFor(string site)
        {
            string contract = CoopContract();
            if (contract == null) return Guid.NewGuid();
            Frame f = current;
            string key;
            int n;
            if (f != null)
            {
                if (f.Guids == null) f.Guids = new Dictionary<string, int>();
                f.Guids.TryGetValue(site, out n); f.Guids[site] = ++n;
                key = contract + "|" + f.Key + "|" + site + "|" + n;
            }
            else
            {
                if (guidContract != contract) { guidContract = contract; guidCounts.Clear(); }
                guidCounts.TryGetValue(site, out n); guidCounts[site] = ++n;
                key = contract + "|" + site + "|" + n;
            }
            ulong a = Fnv(key), b = Fnv(key + "|b");
            byte[] bytes = new byte[16];
            for (int i = 0; i < 8; i++) { bytes[i] = (byte)(a >> (8 * i)); bytes[8 + i] = (byte)(b >> (8 * i)); }
            guidTags.TryGetValue(site, out string tag);
            Row(tag ?? "rngsweep-coop", site, "guid|" + site + "|" + n + "|" + (f != null ? f.Key : "-"));
            return new Guid(bytes);
        }

        static void InstancePrefix(MethodBase __originalMethod, object __instance, object[] __args, ref object __state) => Open(__originalMethod, __instance, __args, ref __state);

        static void StaticPrefix(MethodBase __originalMethod, object[] __args, ref object __state) => Open(__originalMethod, null, __args, ref __state);

        static void Open(MethodBase original, object instance, object[] args, ref object state)
        {
            state = null;
            if (!sites.TryGetValue(original, out Site site)) return;
            try
            {
                string contract = CoopContract();   // cached: no reflection outside a co-op combat once read
                if (contract == null) return;
                string key = site.Key(instance, args, Get(Combat(), "TurnDirector"));
                if (key == null) return;
                ulong h = Fnv(contract + "|rngsweep|" + site.Name + "|" + key);
                var frame = new Frame { Outer = current, Key = site.Name + "|" + key, Seed = (int)(uint)(h ^ (h >> 32)), UnityState = randomState.GetValue(null, null) };
                initState.Invoke(null, new object[] { frame.Seed });
                current = frame;
                state = frame;
                Row(site.Tag, site.Name, "seed|" + site.Name + "|" + key + "|" + frame.Seed);
            }
            catch (Exception e) { log(site.Tag + "|prefix-failed|" + site.Name + "|" + e.Message); }   // a saved state is still put back
        }

        static void Finalizer(object __state)
        {
            if (!(__state is Frame f)) return;
            try { randomState.SetValue(null, f.UnityState, null); }
            catch (Exception e) { log("rngsweep-coop|restore-failed|" + e.Message); }
            current = f.Outer;
        }

        static void ResetCoop() { coopState = 0; coopContract = null; }

        static string CoopContract()
        {
            if (coopState == 2) return null;
            if (coopState == 1) return coopContract;
            try
            {
                object combat = Combat();
                if (combat == null) return null;   // no combat yet: read again next time
                object net = Get(combat, "NetworkGameInstance");
                if (net == null || !(Get(net, "IsCoopCombat") is bool coop) || !coop) { coopState = 2; return null; }
                string contract = Get(Get(combat, "ActiveContract"), "GUID") as string;
                if (contract == null) return null;   // co-op, contract not attached yet: read again next time
                coopContract = contract;
                coopState = 1;
                return contract;
            }
            catch { return null; }
        }

        static object Combat() => battleTechGame == null ? null : Get(battleTechGame.GetValue(null, null), "Combat");

        static string RoundPhase(object turn) => "|r" + Get(turn, "CurrentRound") + "|p" + Get(turn, "CurrentPhase");

        static string Join(object list)
        {
            if (!(list is System.Collections.IEnumerable e)) return "-";
            var sb = new System.Text.StringBuilder();
            foreach (object o in e) sb.Append(o is string s ? s : o?.GetType().Name).Append(',');
            return sb.ToString();
        }

        static string Component(object c) => Get(Get(c, "parent"), "GUID") + "|" + Get(c, "uid") + "|" + Get(c, "defId");

        static void Row(string tag, string site, string text)
        {
            rowCounts.TryGetValue(tag + site, out int n);
            if (n >= 40) return;
            rowCounts[tag + site] = n + 1;
            log(tag + "|" + text);
        }

        static ulong Fnv(string text)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < text.Length; i++) h = (h ^ text[i]) * 1099511628211UL;
            return h;
        }

        // The member is looked up once per (runtime type, name) and cached, a miss included.
        static object Get(object o, string name)
        {
            if (o == null) return null;
            var k = new KeyValuePair<Type, string>(o.GetType(), name);
            MemberInfo m;
            lock (members)
            {
                if (!members.TryGetValue(k, out m)) members[k] = m = Find(k.Key, name);
            }
            if (m is PropertyInfo p) return p.GetValue(o, null);
            return (m as FieldInfo)?.GetValue(o);
        }

        static MemberInfo Find(Type type, string name)
        {
            const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type t = type; t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, f);
                if (p != null && p.GetIndexParameters().Length == 0) return p;
                FieldInfo fi = t.GetField(name, f);
                if (fi != null) return fi;
            }
            return null;
        }
    }
}
