// Generic co-op random-number mechanism for mods (`.hermes/plans/2026-11-23-post4b-coop-adapter-generalization.md`, family R;
// the maintainer, 2026-11-23, D-20261123-4 = A). It replaces the per-mod family R adapters:
//  R1. One fixed list of shared game points (SharedPoints below), one owner per method. Each point saves UnityEngine.Random.state,
//      InitState(FNV(contract GUID | "rngsweep" | site | key)) in a Priority.First prefix and puts the peer's own state back in a
//      Priority.Last finalizer, so every mod prefix/postfix on the method is inside the scope. Keys are the ones the per-mod adapters
//      chose; where two or three adapters scoped one method: OnActivationEnd uses the unit GUID | round | phase (TisButAScratch's key),
//      AttackDirector.OnAttackComplete the attack sequence id | round | phase, Pilot.InjurePilot the pilot's actor GUID | Injuries | dmg |
//      round | phase (TisButAScratch's key, a superset of IRTweaks').
//  R2. At first combat, in every loaded non-UCU mod assembly: (a) call/callvirt System.Random::Next()/Next(int)/Next(int,int)/NextDouble()/
//      NextBytes(byte[]) becomes a shim that draws from the open frame's per-site Random, and calls the receiver unchanged outside a
//      frame; (b) Guid.NewGuid() goes to NewGuidFor; (c) new System.Random() goes to NewRandomFor. This covers static, instance and local
//      Randoms in every mod, so it replaces Field/ldsfld transpilers and CBTBE's Mod.Random internals swap. Keyed-identity GUID sites
//      (ComponentGuidCoopDeterminism, StrategicOperations' support teams) keep their own transpilers and are left to them.
//  R3. Mod-internal entry points no shared point reaches are rows of one declaration table (mod, type.method, key) and are scoped the
//      same way, not written as adapters: CBTBE CreateImaginaryAttack, CAC hit variance/jam/dynamic-map, MissionControl spawn nodes,
//      SO AI_utils and strategic movement, CAE activation and AoE.
// Guards: R1 and R2 arm only at first combat, only in a co-op combat with at least one non-UCU mod assembly loaded. One row says armed or
// not and why. `current` is [ThreadStatic], so a Timer-thread draw sees no frame and calls its receiver unchanged.
// Harness knob COOP_RNG_GENERIC=0 (read once at arm, logged) disables R2 only; it exists for the probe control leg (P-ctl).
// Fault M: every mod method is patched at AtFirstCombat, after the mods' Init, never at assembly load. Single player and unmodded co-op
// install nothing. Rows: "<tag>|seed|...", "<tag>|installed|...", "<tag>|armed|...".
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CoopRngSweepDeterminism
    {
        const string OwnId = "battletech.port.rngsweep-coop";
        const string Tag = "rngsweep-coop";
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        delegate string KeyOf(object instance, object[] args, object turn);
        sealed class Site { public string Tag, Name; public KeyOf Key; public Func<object, object[], string> Salt; }
        sealed class Frame
        {
            public Frame Outer; public string Name, Key; public int Seed; public object UnityState;
            public Dictionary<string, Random> Randoms; public Dictionary<string, int> Guids;
        }

        static Action<string> log;
        static Harmony harmony;
        static PropertyInfo randomState, battleTechGame;
        static MethodInfo initState;
        static readonly Dictionary<MethodBase, Site> sites = new Dictionary<MethodBase, Site>();
        static readonly Dictionary<string, string> guidTags = new Dictionary<string, string>();
        static readonly Dictionary<string, int> guidCounts = new Dictionary<string, int>(), rowCounts = new Dictionary<string, int>();
        static string guidContract;
        static string contractSalt;                            // contract GUID recorded at a shared point, for points before ActiveContract is set
        static readonly Dictionary<KeyValuePair<Type, string>, MemberInfo> members = new Dictionary<KeyValuePair<Type, string>, MemberInfo>();
        [ThreadStatic] static Frame current;                   // a Timer-thread draw must not see the main thread's frame
        static readonly MethodInfo newGuidFor = typeof(CoopRngSweepDeterminism).GetMethod(nameof(NewGuidFor), BindingFlags.Static | BindingFlags.NonPublic);
        static readonly MethodInfo newRandomFor = typeof(CoopRngSweepDeterminism).GetMethod(nameof(NewRandomFor), BindingFlags.Static | BindingFlags.NonPublic);
        static readonly MethodInfo drawShim = typeof(CoopRngSweepDeterminism).GetMethod(nameof(DrawNext), BindingFlags.Static | BindingFlags.NonPublic);
        static readonly MethodInfo drawMaxShim = typeof(CoopRngSweepDeterminism).GetMethod(nameof(DrawNextMax), BindingFlags.Static | BindingFlags.NonPublic);
        static readonly MethodInfo drawRangeShim = typeof(CoopRngSweepDeterminism).GetMethod(nameof(DrawNextRange), BindingFlags.Static | BindingFlags.NonPublic);
        static readonly MethodInfo drawDoubleShim = typeof(CoopRngSweepDeterminism).GetMethod(nameof(DrawNextDouble), BindingFlags.Static | BindingFlags.NonPublic);
        static readonly MethodInfo drawBytesShim = typeof(CoopRngSweepDeterminism).GetMethod(nameof(DrawNextBytes), BindingFlags.Static | BindingFlags.NonPublic);
        static readonly MethodInfo guidNewGuid = typeof(Guid).GetMethod("NewGuid", Type.EmptyTypes);
        static readonly MethodInfo randomNext = typeof(Random).GetMethod("Next", Type.EmptyTypes);
        static readonly MethodInfo randomNextMax = typeof(Random).GetMethod("Next", new[] { typeof(int) });
        static readonly MethodInfo randomNextRange = typeof(Random).GetMethod("Next", new[] { typeof(int), typeof(int) });
        static readonly MethodInfo randomNextDouble = typeof(Random).GetMethod("NextDouble", Type.EmptyTypes);
        static readonly MethodInfo randomNextBytes = typeof(Random).GetMethod("NextBytes", new[] { typeof(byte[]) });
        static readonly ConstructorInfo randomCtor = typeof(Random).GetConstructor(Type.EmptyTypes);
        static readonly Dictionary<string, int> declarationRuns = new Dictionary<string, int>();   // R3 node/read ordinals
        static readonly Dictionary<string, int> frameCounts = new Dictionary<string, int>();       // R1/R3 point -> frames opened
        static readonly Dictionary<string, int> drawCounts = new Dictionary<string, int>();        // R1/R3 point -> draws R2 served inside it
        static readonly Dictionary<string, int> drawAssemblies = new Dictionary<string, int>();    // mod assembly -> draws R2 served inside a frame
        static readonly Dictionary<string, int> nullKeys = new Dictionary<string, int>();         // R1/R3 point -> calls whose key was null (no frame)
        static bool summaryHooked;
        // Harness and UCU mods load from Mods too; they are not the non-UCU mod assemblies the guard counts.
        static readonly string[] UcuAssemblies = { "CoopMod", "CorruptOnce", "FirstRunSkip", "SaveProbe", "LoadProbe", "RouteMod" };
        static readonly string[] NodeKeyFields = { "lanceKey", "objectKey", "lanceGuid" };

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            // HEAD timing, fault M: Register only registers callbacks. No type resolution, reflection, Harmony patch or IL scan here.
            // FirstTimeInit cannot be patched from its own prefix (R-fix6 hung with EncounterLayerData=null after the last installed R1 row
            // was request-unit): HEAD FieldRepairs installed that one game-point scope at its mod assembly's load, so this engine installs
            // only that point at the first mod assembly load (WhenAnyMod, after ModTek's preloader) and everything else at first combat.
            ModAssemblyHook.WhenAnyMod(a => ArmEarly(a), logLine, Tag);
            ModAssemblyHook.When("Assembly-CSharp", a => ModAssemblyHook.AtFirstCombat(ArmGeneric, logLine, Tag), logLine, Tag);
        }

        // The one R1 point that must exist before combat starts: CombatGameState.FirstTimeInit is on the stack when AtFirstCombat runs,
        // so patching it there hangs the combat load (R-fix6). Installed at the first mod assembly load, the timing HEAD's
        // FieldRepairsCoopDeterminism used for the same method.
        static void ArmEarly(Assembly mod)
        {
            try
            {
                Resolve();
                Scope(Game("BattleTech.CombatGameState", "FirstTimeInit", ModAssemblyHook.GameType("BattleTech.Contract"),
                    ModAssemblyHook.GameType("BattleTech.GameInstance"), typeof(string)), Tag, "first-time-init", (i, x, t) => "",
                    (i, x) => x.Length > 0 ? Get(x[0], "GUID") as string : null);
                log(Tag + "|early|first-time-init installed at " + mod.GetName().Name + " load");
            }
            catch (Exception ex) { log(Tag + "|early-failed|" + ex.Message); }
        }

        // Runs inside the AtFirstCombat callback only (fault M): resolves the game members and creates the Harmony instance.
        static void Resolve()
        {
            if (harmony != null) return;
            const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type random = ModAssemblyHook.GameType("UnityEngine.Random");
            randomState = random?.GetProperty("state", stat);
            initState = random?.GetMethod("InitState", stat, null, new[] { typeof(int) }, null);
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", stat);
            harmony = new Harmony(OwnId);
            contractSalt = null; guidContract = null; guidCounts.Clear();
            lock (declarationRuns) declarationRuns.Clear();
            if (randomState == null || initState == null || battleTechGame == null)
                log(Tag + "|init-missing|Random.state=" + (randomState != null) + " InitState=" + (initState != null) + " BattleTechGame=" + (battleTechGame != null));
        }

        // At first combat (fault M): arm R1 and R2 only in a co-op combat with at least one non-UCU mod assembly loaded. One row says why.
        static void ArmGeneric()
        {
            Resolve();
            string knob = Environment.GetEnvironmentVariable("COOP_RNG_GENERIC");   // HARNESS knob: read once here, logged below
            bool coop = IsCoopCombatNow();
            List<Assembly> mods = ModAssemblies();
            var names = new List<string>();
            foreach (Assembly a in mods) names.Add(a.GetName().Name);
            var excluded = ExcludedNames(names);
            log(Tag + "|scope|mod-assemblies=" + mods.Count + "|excluded=" + excluded.Count + "|excluded-names=" + string.Join(",", excluded.ToArray()));
            if (!coop || mods.Count == 0)
            {
                log(Tag + "|not-armed|coop=" + coop + "|mod-assemblies=" + mods.Count + "|reason="
                    + (!coop ? "not a co-op combat" : "no non-UCU mod assembly loaded") + "|COOP_RNG_GENERIC=" + (knob ?? "(unset)"));
                return;   // nothing to install in a single-player or unmodded co-op combat
            }
            if (!summaryHooked)
            {
                summaryHooked = true;
                try
                {
                    MethodInfo destroyed = ModAssemblyHook.GameType("BattleTech.CombatGameState")?.GetMethod("OnCombatGameDestroyed", All);
                    if (destroyed != null) harmony.Patch(destroyed, postfix: Mine(nameof(LogSummary), Priority.Last));
                }
                catch (Exception e) { log(Tag + "|summary-patch-failed|" + e.Message); }
            }
            bool r2 = knob != "0";
            log(Tag + "|armed|coop=True|mod-assemblies=" + mods.Count + "|r1=True|r2=" + r2 + "|r3=True|COOP_RNG_GENERIC=" + (knob ?? "(unset)")
                + "|mods=" + string.Join(",", names.ToArray()));
            long started = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            log(Tag + "|phase|r1-start");
            SharedPoints();
            long r1 = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond - started;
            log(Tag + "|phase|r1-done|ms=" + r1);
            log(Tag + "|phase|r3-start");
            Declarations(mods);
            log(Tag + "|phase|r3-done|ms=" + (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond - started - r1));
            if (!r2)
            {
                log(Tag + "|r2-off|COOP_RNG_GENERIC=0: Guid.NewGuid, new System.Random() and System.Random draws left as they are; R1 and R3 armed");
                long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond - started;
                log(Tag + "|arm-time|ms=" + now + "|r1r3=" + r1 + "|r2=0|mods=" + mods.Count);
                return;
            }
            int calls = 0, methods = 0;
            log(Tag + "|phase|r2-start");
            foreach (Assembly a in mods)
            {
                log(Tag + "|phase|r2-assembly|" + a.GetName().Name);
                int c = Redirect(a, out int gm, out int nm);
                calls += c; methods += gm + nm;
            }
            log(Tag + "|phase|r2-done");
            log(Tag + "|installed|R2 generic redirect over " + mods.Count + " mod assemblies: System.Random draws=" + calls + " in " + methods + " methods"
                + "; Guid.NewGuid and new System.Random() redirected");
            long done = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond - started;
            log(Tag + "|arm-time|ms=" + done + "|r1r3=" + r1 + "|r2=" + (done - r1) + "|mods=" + mods.Count);
        }

        // Every loaded assembly ModTek loaded from <game>\Mods, except ModTek's own libs and UCU's harness/own mods.
        static List<Assembly> ModAssemblies()
        {
            var list = new List<Assembly>();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) if (IsModAssembly(a)) list.Add(a);
            return list;
        }

        static bool IsModAssembly(Assembly a)
        {
            if (a == null || a.IsDynamic) return false;
            string location;
            try { location = a.Location; } catch { return false; }
            if (string.IsNullOrEmpty(location)) return false;
            string full;
            try { full = System.IO.Path.GetFullPath(location); } catch { return false; }
            int i = full.IndexOf("\\Mods\\", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            string rel = full.Substring(i + 6);
            if (rel.StartsWith("ModTek\\", StringComparison.OrdinalIgnoreCase)) return false;
            if (rel.StartsWith(".port\\", StringComparison.OrdinalIgnoreCase)) return false;   // UCU staged assemblies (Mods/.port/Newtonsoft.Json.dll armed an unmodded co-op in U)
            string name;
            try { name = a.GetName().Name; } catch { return false; }
            if (Array.IndexOf(UcuAssemblies, name) >= 0) return false;
            // The game's, Unity's and the BCL's assemblies also appear under Mods/.modtek/AssembliesShimmed; they are not mods.
            if (name == "Assembly-CSharp" || name == "Assembly-CSharp-firstpass" || name == "mscorlib" || name == "netstandard") return false;
            if (name == "System" || name.StartsWith("System.", StringComparison.Ordinal)) return false;
            if (name == "Unity" || name.StartsWith("Unity.", StringComparison.Ordinal) || name.StartsWith("UnityEngine", StringComparison.Ordinal)) return false;
            if (name.StartsWith("DOTween", StringComparison.Ordinal)) return false;
            if (name == "Mono" || name.StartsWith("Mono.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)) return false;
            if (name.StartsWith("BattleTech.", StringComparison.Ordinal) || name.StartsWith("BattleTechPort", StringComparison.Ordinal)) return false;
            if (name.StartsWith("ModTek", StringComparison.Ordinal) || name == "0Harmony" || name.StartsWith("0Harmony.", StringComparison.Ordinal)
                || name.StartsWith("HarmonyX", StringComparison.Ordinal) || name.StartsWith("Ionic", StringComparison.Ordinal)) return false;
            // Anything else shipped in BattleTech_Data\Managed is the game's/Unity's/BCL's, except the third-party libraries a mod
            // ships in its own folder (kept: Newtonsoft.Json, YamlDotNet, MessagePack, EPPlus, ManagedBass).
            if (ManagedNames().ContainsKey(name) && !KeepManaged.ContainsKey(name)) return false;
            return true;
        }

        static Dictionary<string, bool> managedNames;
        static readonly Dictionary<string, bool> KeepManaged = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        { { "Newtonsoft.Json", true }, { "YamlDotNet", true }, { "MessagePack", true }, { "EPPlus", true }, { "ManagedBass", true } };

        static Dictionary<string, bool> ManagedNames()
        {
            if (managedNames != null) return managedNames;
            var set = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BattleTech_Data", "Managed");
                foreach (string f in System.IO.Directory.GetFiles(dir, "*.dll")) set[System.IO.Path.GetFileNameWithoutExtension(f)] = true;
            }
            catch { }
            return managedNames = set;
        }

        // The assemblies under Mods that R2 leaves alone: the game's, Unity's, the BCL's, ModTek's and UCU's own (logged once at arm).
        static List<string> ExcludedNames(List<string> kept)
        {
            var excluded = new List<string>();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a == null || a.IsDynamic) continue;
                string location;
                try { location = a.Location; } catch { continue; }
                if (string.IsNullOrEmpty(location)) continue;
                string full;
                try { full = System.IO.Path.GetFullPath(location); } catch { continue; }
                if (full.IndexOf("\\Mods\\", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string name;
                try { name = a.GetName().Name; } catch { continue; }
                if (kept.Contains(name) || excluded.Contains(name)) continue;
                excluded.Add(name);
            }
            excluded.Sort(StringComparer.Ordinal);
            return excluded;
        }

        // ---- R1: one fixed list of shared game points, one owner per method. ----
        static void SharedPoints()
        {
            const string tag = Tag;
            Scope(Game("BattleTech.Contract", "BeginRequestResources", typeof(bool)), tag, "contract-begin",
                (i, x, t) => Get(i, "GUID") as string, (i, x) => Get(i, "GUID") as string);
            Scope(Game("BattleTech.EncounterLayerParent", "InitializeContract", ModAssemblyHook.GameType("MessageCenterMessage")), tag, "encounter-init", (i, x, t) => "init");
            Scope(Game("BattleTech.UnitSpawnPointGameLogic", "Spawn", typeof(bool)), tag, "spawn", (i, x, t) => Get(i, "GUID") as string);
            Scope(Declared("BattleTech.Framework.UnitSpawnPointOverride", "RequestUnit", 8), tag, "request-unit", (i, x, t) => Join(x));
            // FirstTimeInit is installed early (ArmEarly) because it is on the stack at AtFirstCombat; see Register.
            Scope(Game("BattleTech.AttackDirector+AttackSequence", "GenerateHitInfo", ModAssemblyHook.GameType("BattleTech.Weapon"), typeof(int), typeof(int), typeof(int), typeof(bool), typeof(float)),
                tag, "hit-info", (i, x, t) => Get(i, "id") + "|" + x[1] + "|" + x[2]);
            Scope(Game("BattleTech.AttackDirector+AttackSequence", "GenerateToHitInfo"), tag, "to-hit", (i, x, t) => Get(i, "id")?.ToString());   // id is an int: `as string` was null, so no frame opened (R-fix9: point|to-hit|frames=0)
            Scope(Game("BattleTech.AttackDirector", "OnAttackComplete", ModAssemblyHook.GameType("MessageCenterMessage")), tag, "attack-complete",
                (i, x, t) => "seq|" + Get(x[0], "sequenceId") + RoundPhase(t));
            Scope(Game("BattleTech.AttackStackSequence", "OnAttackComplete", ModAssemblyHook.GameType("MessageCenterMessage")), tag, "stack-attack-complete",
                (i, x, t) => Get(Get(i, "owningActor"), "GUID") + "|" + Get(x[0], "sequenceId"));
            Scope(Game("BattleTech.AttackStackSequence", "CompleteOrders"), tag, "attack-stack-complete", (i, x, t) => Get(Get(i, "owningActor"), "GUID") + RoundPhase(t));
            Scope(Game("BattleTech.ActorMovementSequence", "CompleteOrders"), tag, "move-complete", (i, x, t) => Get(Get(i, "owningActor"), "GUID") as string);
            Scope(Game("BattleTech.MechMeleeSequence", "CompleteOrders"), tag, "melee-complete", (i, x, t) => Get(Get(i, "owningActor"), "GUID") as string);
            Scope(Game("BattleTech.MechJumpSequence", "CompleteOrders"), tag, "jump-complete", (i, x, t) => Get(Get(i, "OwningMech"), "GUID") + RoundPhase(t));
            Scope(Game("BattleTech.AbstractActor", "OnActivationEnd", typeof(string), typeof(int)), tag, "activation-end", (i, x, t) => Get(i, "GUID") + RoundPhase(t));
            // Mech overrides OnActivationEnd and calls the base; Extended_CE's prefix on the override runs before that, so it needs its own scope. Same key.
            Scope(Game("BattleTech.Mech", "OnActivationEnd", typeof(string), typeof(int)), tag, "activation-end", (i, x, t) => Get(i, "GUID") + RoundPhase(t));
            Scope(Game("BattleTech.AbstractActor", "OnNewRound", typeof(int)), tag, "new-round", (i, x, t) => Get(i, "GUID") + "|r" + x[0]);
            Scope(Game("BattleTech.AbstractActor", "OnAttackComplete"), tag, "actor-attack-complete", (i, x, t) => Get(i, "GUID") + RoundPhase(t));
            Scope(Game("BattleTech.AbstractActor", "HandleDeath", typeof(string)), tag, "death", (i, x, t) => Get(i, "GUID") + "|" + x[0] + RoundPhase(t));
            Scope(Declared("BattleTech.Pilot", "InjurePilot", 6), tag, "injure", (i, x, t) => Get(Get(i, "ParentActor"), "GUID") + "|" + Get(i, "Injuries") + "|" + x[2] + RoundPhase(t));
            Scope(Game("BattleTech.TurnDirector", "IncrementActiveTurnActor"), tag, "turn",
                (i, x, t) => "r" + Get(t, "CurrentRound") + "|p" + Get(t, "CurrentPhase") + "|a" + Get(t, "ActiveTurnActorIndex"));
            Scope(Game("BattleTech.TurnDirector", "StartFirstRound"), tag, "first-round", (i, x, t) => "r" + Get(t, "CurrentRound"));
            Scope(Game("BattleTech.Team", "AddUnit", ModAssemblyHook.GameType("BattleTech.AbstractActor")), tag, "addunit", (i, x, t) => Get(x[0], "GUID") as string);
            Scope(Game("BattleTech.Mech", "InitStats"), tag, "mech-init", (i, x, t) => Get(i, "GUID") as string);
            Scope(Game("BattleTech.Vehicle", "InitStats"), tag, "vehicle-init", (i, x, t) => Get(i, "GUID") as string);
            Scope(Game("BattleTech.Pilot", "InitAbilities", typeof(bool), typeof(bool)), tag, "pilot-abilities", (i, x, t) => Get(i, "GUID") + "|" + x[0] + "|" + x[1]);
            Scope(Game("BattleTech.MechComponent", "DamageComponent", ModAssemblyHook.GameType("BattleTech.WeaponHitInfo"),
                ModAssemblyHook.GameType("BattleTech.ComponentDamageLevel"), typeof(bool)), tag, "damage-component",
                (i, x, t) => Component(i) + "|" + Get(i, "DamageLevel") + "|" + x[1] + "|" + x[2] + Hit(x[0]));
            Scope(Declared("BattleTech.Mech", "DamageLocation", 9), tag, "damage-location", (i, x, t) => Get(i, "GUID") + "|" + x[2] + "|" + x[6] + Hit(x[1]));
            Scope(Declared("BattleTech.Mech", "OnLocationDestroyed", 4), tag, "location-destroyed", (i, x, t) => Get(i, "GUID") + "|" + x[0] + Hit(x[2]));
            Scope(Declared("BattleTech.Mech", "OnMoveComplete", 6), tag, "move-complete-mech", (i, x, t) => Get(i, "GUID") + RoundPhase(t));
            Scope(Declared("BattleTech.Mech", "OnSprintComplete", 5), tag, "sprint-complete", (i, x, t) => Get(i, "GUID") + RoundPhase(t));
            // Extended_CE (BEX:T) only, as at HEAD: another mod's postfix on this method (AttackImprovementMod's FixZombieBuilding) fails to rebuild under a second patch
            if (Array.Exists(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "Extended_CE"))
                Scope(Declared("BattleTech.Building", "DamageBuilding", 4), tag, "damage-building", (i, x, t) => Get(i, "GUID") + "|" + x[1] + Hit(x[0]));
            Scope(Game("BattleTech.Mech", "ResolveWeaponDamage", ModAssemblyHook.GameType("BattleTech.WeaponHitInfo"),
                ModAssemblyHook.GameType("BattleTech.Weapon"), ModAssemblyHook.GameType("BattleTech.MeleeAttackType")), tag, "resolve-damage",
                (i, x, t) => Get(i, "GUID") + "|" + Component(x[1]) + Hit(x[0]));
            foreach (string name in new[] { "MechStartupInvocation", "MechStandInvocation" })
                Scope(Game("BattleTech." + name, "Invoke", ModAssemblyHook.GameType("BattleTech.CombatGameState")), tag, name,
                    (i, x, t) => Get(i, "MechGUID") + RoundPhase(t));
            Scope(Game("BattleTech.Ability", "Activate", ModAssemblyHook.GameType("BattleTech.AbstractActor"), ModAssemblyHook.GameType("BattleTech.ICombatant")),
                tag, "ability", (i, x, t) => Get(Get(i, "Def"), "Id") + "|" + Get(x[0], "GUID") + "|" + Get(x[1], "GUID") + RoundPhase(t));
            foreach (string name in new[] { "ApplyEffectsToMech", "ApplyEffectsToTurret", "ApplyEffectsToVehicle", "ApplyEffectsToBuilding" })
                Scope(Declared("BattleTech.PoorlyMaintainedEffect", name, 1), tag, "poorly-maintained",
                    (i, x, t) => name + "|" + (Get(x[0], "GUID") as string ?? "?"));
        }

        // ---- R3: one declaration table for mod-internal entry points no shared point reaches. ----
        sealed class Decl { public string Mod, Type, Method, Name; public int Params; public KeyOf Key; }
        static readonly Decl[] Decls =
        {
            new Decl { Mod = "CBTBehaviorsEnhanced", Type = "CBTBehaviorsEnhanced.Helper.AttackHelper", Method = "CreateImaginaryAttack", Params = 7, Name = "cbtbe-imaginary-attack",
                Key = (i, x, t) => Get(x[0], "GUID") + "|" + Get(x[2], "GUID") + "|" + x[6] + "|" + x[5] + "|" + ((x[4] as ICollection)?.Count ?? -1) + RoundPhase(t) },
            new Decl { Mod = "CustomAmmoCategories", Type = "CustAmmoCategories.AdvWeaponHitInfoRec", Method = "ApplyVariance", Params = 1, Name = "cac-variance",
                // IRTweaks' per-hit key (its System.Random draws sit in this method): sequence, group, weapon, hit, AoE flag and key.
                Key = (i, x, t) => "hit|" + Get(Get(i, "parent"), "attackSequenceId") + "|" + Get(Get(i, "parent"), "groupIdx") + "|" + Get(Get(i, "parent"), "weaponIdx")
                    + "|" + Get(i, "hitIndex") + "|" + Get(i, "isAOE") + "|" + Get(i, "AOEKey") + RoundPhase(t) },
            new Decl { Mod = "CustomAmmoCategories", Type = "CustAmmoCategories.JammingEnabler", Method = "jammAMS", Params = 0, Name = "cac-jam",
                Key = (i, x, t) => "jam|" + (current != null ? current.Key : RoundPhase(t)) },
            new Decl { Mod = "CustomAmmoCategories", Type = "CustAmmoCategories.ResolveDamageHelper", Method = "ResolveJamming", Params = 2, Name = "cac-jam-weapon",
                Key = (i, x, t) => "jamw|" + Hit(x[1]) },
            new Decl { Mod = "CustomAmmoCategories", Type = "WeaponRealizer.JammingEnabler", Method = "AttemptToAddJam", Params = 2, Name = "cac-unjam",
                Key = (i, x, t) => "unjam|WeaponRealizer.JammingEnabler.AttemptToAddJam|" + Get(x[0], "GUID") + "|" + Get(x[1], "uid") + RoundPhase(t) },
            new Decl { Mod = "CustomAmmoCategories", Type = "WeaponRealizer.JammingEnabler", Method = "AttemptToRemoveJam", Params = 2, Name = "cac-unjam",
                Key = (i, x, t) => "unjam|WeaponRealizer.JammingEnabler.AttemptToRemoveJam|" + Get(x[0], "GUID") + "|" + Get(x[1], "uid") + RoundPhase(t) },
            new Decl { Mod = "CustomAmmoCategories", Type = "CustAmmoCategories.CustomAmmoCategories", Method = "AttemptToRemoveJam", Params = 2, Name = "cac-unjam",
                Key = (i, x, t) => "unjam|CustomAmmoCategories.AttemptToRemoveJam|" + Get(x[0], "GUID") + "|" + Get(x[1], "uid") + RoundPhase(t) },
            new Decl { Mod = "CustomAmmoCategories", Type = "CustAmmoCategories.DynamicMapHelper", Method = "calculateAbortPos", Params = -1, Name = "cac-mine-abort",
                Key = (i, x, t) => "mine|abort|" + Get(x[0], "GUID") + RoundPhase(t) + "|" + LastWaypoint(x[2]) },
            new Decl { Mod = "CustomAmmoCategories", Type = "CustAmmoCategories.DynamicMapHelper", Method = "calculateJumpDamage", Params = 2, Name = "cac-mine-jump",
                Key = (i, x, t) => "mine|jump|" + Get(x[0], "GUID") + RoundPhase(t) + "|" + XZ(x[1]) },
            new Decl { Mod = "CustomAmmoCategories", Type = "CustAmmoCategories.DynamicMapHelper", Method = "FireTick", Params = 0, Name = "cac-fire",
                Key = (i, x, t) => "fire|tick|" + RoundPhase(t) },
            new Decl { Mod = "CustomAmmoCategories", Type = "CustAmmoCategories.MapTerrainHexCell", Method = "TryBurnCellSync", Params = 4, Name = "cac-fire-burn",
                Key = (i, x, t) => "fire|burn|" + XZ(Get(i, "center")) + "|" + Get(Get(x[0], "parent"), "GUID") + RoundPhase(t) },
            new Decl { Mod = "CustomAmmoCategories", Type = "CustAmmoCategories.MineField", Method = ".ctor", Params = 4, Name = "cac-mine-lay",
                Key = (i, x, t) => "mine|lay|" + XZ(Get(x[0], "center")) + RoundPhase(t) },
            new Decl { Mod = "CustomAmmoCategories", Type = "CustAmmoCategories.DeferredEffect", Method = "addEffect", Params = 3, Name = "cac-defer",
                Key = (i, x, t) => "defer|" + Get(x[0], "GUID") + "|" + Get(Get(x[1], "Description"), "Id") + RoundPhase(t) },
            new Decl { Mod = "MissionControl", Type = "MissionControl.Logic.SpawnLanceAtEdgeOfBoundary", Method = "Run", Params = 1, Name = "mc-node", Key = NodeKey },
            new Decl { Mod = "MissionControl", Type = "MissionControl.Logic.SpawnLanceAnywhere", Method = "Run", Params = 1, Name = "mc-node", Key = NodeKey },
            new Decl { Mod = "MissionControl", Type = "MissionControl.Logic.SpawnLanceMembersAroundTarget", Method = "Run", Params = 1, Name = "mc-node", Key = NodeKey },
            new Decl { Mod = "MissionControl", Type = "MissionControl.Logic.SpawnObjectAnywhere", Method = "Run", Params = 1, Name = "mc-node", Key = NodeKey },
            new Decl { Mod = "MissionControl", Type = "MissionControl.Logic.AddLanceToAllyTeam", Method = "Run", Params = 1, Name = "mc-node", Key = NodeKey },
            new Decl { Mod = "MissionControl", Type = "MissionControl.Logic.AddLanceToTargetTeam", Method = "Run", Params = 1, Name = "mc-node", Key = NodeKey },
            new Decl { Mod = "MissionControl", Type = "MissionControl.Logic.AddLanceToPlayer2Team", Method = "Run", Params = 1, Name = "mc-node", Key = NodeKey },
            // MissionControl result and activator picks (the sweep's rows at HEAD): the tag-region results, the three "at random" results and the chunk activator.
            new Decl { Mod = "MissionControl", Type = "MissionControl.Result.TagUnitsInRegionResult", Method = "Trigger", Params = 2, Name = "mc-tag-region",
                Key = (i, x, t) => Get(i, "RegionGuid") + "|" + Get(i, "Type") + "|" + x[1] },
            new Decl { Mod = "MissionControl", Type = "MissionControl.Result.SetUnitsInRegionToBeTaggedObjectiveTargetsResult", Method = "Trigger", Params = 2, Name = "mc-tag-region",
                Key = (i, x, t) => Get(i, "RegionGuid") + "|" + Get(i, "Type") + "|" + x[1] },
            new Decl { Mod = "MissionControl", Type = "MissionControl.Result.SetStatusAtRandomResult", Method = "Trigger", Params = 2, Name = "mc-status-at-random",
                Key = (i, x, t) => Join(Get(i, "EncounterGuids")) + "|" + Get(i, "Status") + "|" + x[1] },
            new Decl { Mod = "MissionControl", Type = "MissionControl.Result.TriggerResultAtRandomResult", Method = "Trigger", Params = 2, Name = "mc-result-at-random",
                Key = (i, x, t) => Join(Get(i, "Results")) + "|" + x[1] },
            // Update runs every frame: a scope opens only on the frame the activator can pick (chunk set, not activated yet), keyed by the activator.
            new Decl { Mod = "MissionControl", Type = "MissionControl.LogicComponents.Activators.SetChunkStateAtRandomActivator", Method = "Update", Params = 0, Name = "mc-chunk-at-random",
                Key = (i, x, t) => Get(i, "chunk") == null || Get(i, "HasActivated") is bool done && done ? null : Get(i, "encounterObjectGuid") + "|" + Join(Get(i, "ChunkGuids")) },
            new Decl { Mod = "MissionControl", Type = "MissionControl.LogicComponents.Activators.SetChunkStateAtRandomActivator", Method = "Execute", Params = 0, Name = "mc-chunk-at-random-exec",
                Key = (i, x, t) => Get(i, "encounterObjectGuid") + "|" + Join(Get(i, "ChunkGuids")) },
            new Decl { Mod = "StrategicOperations", Type = "StrategicOperations.Framework.AI_Utils", Method = "GenerateAIStrategicAbilities", Params = 1, Name = "so-ai",
                Key = (i, x, t) => { string g = Get(x[0], "GUID") as string ?? "?"; int n; lock (declarationRuns) { declarationRuns.TryGetValue("so-ai|" + g, out n); declarationRuns["so-ai|" + g] = n + 1; } return g + "|" + n + RoundPhase(t); } },
            new Decl { Mod = "StrategicOperations", Type = "StrategicOperations.Framework.Classes+StrategicMovementSequence", Method = "CompleteOrders", Params = 0, Name = "so-move",
                Key = (i, x, t) => Get(Get(i, "owningActor"), "GUID") + RoundPhase(t) },
            new Decl { Mod = "CustomActivatableEquipment", Type = "CustomActivatableEquipment.ActivatableComponent", Method = "activateComponent", Params = 3, Name = "cae-activate",
                Key = (i, x, t) => Component(x[0]) + "|" + x[1] + "|" + x[2] + RoundPhase(t) },
            new Decl { Mod = "CustomActivatableEquipment", Type = "CustomActivatableEquipment.AoEComponentExplodeHelper", Method = "AoEExplodeComponent", Params = 1, Name = "cae-aoe",
                Key = (i, x, t) => Component(x[0]) + RoundPhase(t) },
        };

        static string NodeKey(object i, object[] x, object turn)
        {
            string type = i.GetType().Name, key = "";
            foreach (string field in NodeKeyFields) if (Get(i, field) is string s && s.Length > 0) { key = s; break; }
            string id = type + "|" + key;
            int n;
            lock (declarationRuns) { declarationRuns.TryGetValue(id, out n); declarationRuns[id] = n + 1; }
            return id + "|" + n;
        }

        static void Declarations(List<Assembly> mods)
        {
            var byName = new Dictionary<string, Assembly>();
            foreach (Assembly a in mods) byName[a.GetName().Name] = a;
            int installed = 0;
            foreach (Decl d in Decls)
            {
                if (!byName.TryGetValue(d.Mod, out Assembly a)) continue;   // that mod is not loaded: its row costs nothing
                MethodBase m = Resolve(a, d.Type, d.Method, d.Params);
                if (m == null) { log(Tag + "|row-missing|R3 " + d.Mod + " " + d.Type + "." + d.Method + " not found"); continue; }
                if (sites.ContainsKey(m)) continue;
                Scope(m, Tag, d.Name, d.Key);
                installed++;
            }
            log(Tag + "|installed|R3 declarations: " + installed + " of " + Decls.Length + " rows for " + mods.Count + " mod assemblies");
        }

        static MethodBase Resolve(Assembly a, string type, string method, int count)
        {
            Type t;
            try { t = a.GetType(type, false); } catch { return null; }
            if (t == null) return null;
            MethodBase match = null;
            if (method == ".ctor")
            {
                foreach (ConstructorInfo c in t.GetConstructors(All))
                    if (count < 0 || c.GetParameters().Length == count) { if (match != null) return null; match = c; }
                return match;
            }
            foreach (MethodInfo m in t.GetMethods(All))
            {
                if (m.Name != method) continue;
                if (count >= 0 && m.GetParameters().Length != count) continue;
                if (match != null) return null;
                match = m;
            }
            return match;
        }

        // ---- R2: generic redirect in every loaded non-UCU mod assembly. ----
        // One pass per method (R-fix11 ran three: every method of every assembly was decoded three times, 50-75 s at first combat):
        // the IL is decoded once, the three kinds of site are counted together, and a method with any site is patched once with the one
        // RedirectTranspiler. Rows and per-assembly counts are the three old passes' (draw redirect, Guid.NewGuid redirect, new Random()
        // redirect). Returns the draw-call count; guidMethods + newMethods is what the old GuidSites + NewRandoms returned.
        static int Redirect(Assembly a, out int guidMethods, out int newMethods)
        {
            int drawMethods = 0, drawCalls = 0, guidCalls = 0, newCalls = 0;
            guidMethods = 0; newMethods = 0;
            foreach (MethodBase m in Methods(a, null))
            {
                if (!MayHaveSite(m)) continue;   // exact pre-filter: no call/callvirt/newobj of a target in the IL, nothing to decode
                int d = 0, g = 0, n = 0;
                bool keyed = KeyedGuidSite(m);   // ComponentGuidCoopDeterminism / the SO support relink mint these and verify their own sites
                try
                {
                    var found = new List<CodeInstruction>(PatchProcessor.GetOriginalInstructions(m));
                    foreach (CodeInstruction ins in found)
                    {
                        if (ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt)
                        {
                            if (DrawKind(ins.operand as MethodInfo) != 0) d++;
                            else if (!keyed && ins.operand as MethodInfo == guidNewGuid) g++;
                        }
                        else if (ins.opcode == OpCodes.Newobj && ins.operand as ConstructorInfo == randomCtor) n++;
                    }
                }
                catch { d = g = n = 0; }
                if (d + g + n == 0) continue;
                if (g > 0 || n > 0) guidTags[Name(m)] = Tag;
                try
                {
                    harmony.Patch(m, transpiler: Mine(nameof(RedirectTranspiler), Priority.Normal));
                    if (d > 0) { drawMethods++; drawCalls += d; }
                    if (g > 0) { guidMethods++; guidCalls += g; }
                    if (n > 0) { newMethods++; newCalls += n; }
                }
                catch (Exception e)
                {
                    if (d > 0) log(Tag + "|patch-failed|draw " + Name(m) + "|" + e.Message);
                    if (g > 0) log(Tag + "|patch-failed|guid " + Name(m) + "|" + e.Message);
                    if (n > 0) log(Tag + "|patch-failed|new-random " + Name(m) + "|" + e.Message);
                }
            }
            string name = a.GetName().Name;
            if (drawCalls > 0) log(Tag + "|installed|draw redirect " + name + "|" + drawCalls + " calls in " + drawMethods + " methods");
            if (guidCalls > 0) log(Tag + "|installed|Guid.NewGuid redirect " + name + "|" + guidCalls + " calls in " + guidMethods + " methods");
            if (newCalls > 0) log(Tag + "|installed|new System.Random() redirect " + name + "|" + newCalls + " calls in " + newMethods + " methods");
            return drawCalls;
        }

        // Exact pre-filter for the full decode: false only when the method's IL provably holds no call/callvirt of a System.Random draw or
        // Guid.NewGuid and no newobj of Random(). The IL is the runtime's (GetILAsByteArray), the tokens are resolved the way the decoder
        // resolves them (Module.ResolveMethod), each distinct token once per module; any parse or resolve failure answers true (decode it).
        static OpCode[] oneByte, twoByte;
        static readonly Dictionary<Module, Dictionary<int, bool>> tokenIsSite = new Dictionary<Module, Dictionary<int, bool>>();

        static void BuildOpCodes()
        {
            oneByte = new OpCode[256]; twoByte = new OpCode[256];
            foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!(f.GetValue(null) is OpCode oc)) continue;
                ushort v = (ushort)oc.Value;
                if (oc.Size == 1) oneByte[v & 0xFF] = oc; else twoByte[v & 0xFF] = oc;
            }
        }

        static int OperandSize(OpCode oc)
        {
            switch (oc.OperandType)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8: case OperandType.InlineR: return 8;
                default: return 4;   // InlineSwitch is handled by the caller (4 + 4 * count)
            }
        }

        static bool MayHaveSite(MethodBase m)
        {
            try
            {
                byte[] il = m.GetMethodBody()?.GetILAsByteArray();
                if (il == null) return true;
                if (oneByte == null) BuildOpCodes();
                Module module = m.Module;
                Type[] typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericArguments() : null;
                int i = 0;
                while (i < il.Length)
                {
                    OpCode oc;
                    if (il[i] == 0xFE) { if (i + 1 >= il.Length) return true; oc = twoByte[il[i + 1]]; i += 2; }
                    else { oc = oneByte[il[i]]; i += 1; }
                    if (oc.Size == 0) return true;   // an opcode the table lacks: do not guess
                    if (oc == OpCodes.Switch)
                    {
                        if (i + 4 > il.Length) return true;
                        i += 4 + 4 * BitConverter.ToInt32(il, i);
                        continue;
                    }
                    if ((oc == OpCodes.Call || oc == OpCodes.Callvirt || oc == OpCodes.Newobj) && i + 4 <= il.Length
                        && TokenIsSite(module, BitConverter.ToInt32(il, i), typeArgs)) return true;
                    i += OperandSize(oc);
                }
                return false;
            }
            catch { return true; }
        }

        static bool TokenIsSite(Module module, int token, Type[] typeArgs)
        {
            lock (tokenIsSite)
            {
                if (!tokenIsSite.TryGetValue(module, out Dictionary<int, bool> map)) tokenIsSite[module] = map = new Dictionary<int, bool>();
                if (map.TryGetValue(token, out bool known)) return known;
                bool site;
                try
                {
                    MethodBase r = module.ResolveMethod(token, typeArgs, null);
                    site = r is MethodInfo mi ? DrawKind(mi) != 0 || mi == guidNewGuid : r is ConstructorInfo ci && ci == randomCtor;
                }
                catch { site = true; }
                return map[token] = site;
            }
        }

        // Keyed identity GUID sites keep their own transpilers (ComponentGuidCoopDeterminism, the SO support relink); generic Guid.NewGuid
        // must not replace them with a call-order counter. Listed by name so this engine does not depend on those classes' internals.
        static readonly string[] KeyedSites =
        {
            "CustAmmoCategories.CustomAmmoCategories::getCACGUID",
            "CustAmmoCategories.CustomAmmoCategories::createAmmoBoxGUID",
            "CustAmmoCategories.CustomAmmoCategories::getAmmoBoxGUID",
            "CustAmmoCategories.WeaponRepresentation_Init::Postfix",
            "CustomComponents.CustomCombatTagsHelper::getCCGUID",
            "StrategicOperations.Framework.Utils::CreateOrUpdateAISupportTeam",
        };

        static bool KeyedGuidSite(MethodBase m)
            => m?.DeclaringType != null && Array.IndexOf(KeyedSites, m.DeclaringType.FullName + "::" + m.Name) >= 0;

        static int DrawKind(MethodInfo m)
        {
            if (m == randomNext) return 1;
            if (m == randomNextMax) return 2;
            if (m == randomNextRange) return 3;
            if (m == randomNextDouble) return 4;
            if (m == randomNextBytes) return 5;
            return 0;
        }

        // One transpiler for the three rewrites; a keyed Guid site keeps its own Guid.NewGuid (KeyedGuidSite), as the old Guid pass skipped it.
        static IEnumerable<CodeInstruction> RedirectTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            string asm = __originalMethod.DeclaringType?.Assembly.GetName().Name ?? "?";
            string method = Name(__originalMethod);
            string siteBase = asm + "|" + method;
            bool keyed = KeyedGuidSite(__originalMethod);
            int site = 0;
            foreach (CodeInstruction ins in instructions)
            {
                string text = null; MethodInfo shim = null;
                if (ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt)
                {
                    int kind = DrawKind(ins.operand as MethodInfo);
                    if (kind != 0) { text = siteBase + "#" + site++; shim = kind == 1 ? drawShim : kind == 2 ? drawMaxShim : kind == 3 ? drawRangeShim : kind == 4 ? drawDoubleShim : drawBytesShim; }
                    else if (!keyed && ins.operand as MethodInfo == guidNewGuid) { text = method; shim = newGuidFor; }
                }
                else if (ins.opcode == OpCodes.Newobj && ins.operand as ConstructorInfo == randomCtor) { text = method; shim = newRandomFor; }
                if (shim == null) { yield return ins; continue; }
                var push = new CodeInstruction(OpCodes.Ldstr, text);
                push.labels.AddRange(ins.labels);
                push.blocks.AddRange(ins.blocks);
                yield return push;
                yield return new CodeInstruction(OpCodes.Call, shim);
            }
        }

        // Inside a frame, the open frame's per-site Random; outside, the receiver's own draw (a Timer thread or a UI-only path).
        static int DrawNext(Random r, string site) { Frame f = current; return f == null ? r.Next() : SiteRandom(f, site).Next(); }
        static int DrawNextMax(Random r, int max, string site) { Frame f = current; return f == null ? r.Next(max) : SiteRandom(f, site).Next(max); }
        static int DrawNextRange(Random r, int min, int max, string site) { Frame f = current; return f == null ? r.Next(min, max) : SiteRandom(f, site).Next(min, max); }
        static double DrawNextDouble(Random r, string site) { Frame f = current; return f == null ? r.NextDouble() : SiteRandom(f, site).NextDouble(); }
        static void DrawNextBytes(Random r, byte[] buffer, string site) { Frame f = current; if (f == null) r.NextBytes(buffer); else SiteRandom(f, site).NextBytes(buffer); }

        static Random SiteRandom(Frame f, string site)
        {
            // site = "<assembly>|<type>::<method>#<call index>": count the draw for the point and the mod assembly.
            string asm = site;
            int bar = site.IndexOf('|');
            if (bar > 0) asm = site.Substring(0, bar);
            lock (drawCounts)
            {
                if (f.Name != null) { drawCounts.TryGetValue(f.Name, out int n); drawCounts[f.Name] = n + 1; }
                drawAssemblies.TryGetValue(asm, out int a); drawAssemblies[asm] = a + 1;
            }
            if (f.Randoms == null) f.Randoms = new Dictionary<string, Random>();
            if (!f.Randoms.TryGetValue(site, out Random r)) f.Randoms[site] = r = new Random(f.Seed ^ (int)(uint)Fnv(site));
            return r;
        }

        // ---- scopes, keys, rows ----
        static MethodBase Game(string type, string method, params Type[] args)
        {
            Type t = ModAssemblyHook.GameType(type);
            if (t == null || Array.IndexOf(args, null) >= 0) return null;
            return t.GetMethod(method, All, null, args, null);
        }

        // A declared method found by name and parameter count (-1 = any), for signatures with many game types.
        static MethodBase Declared(string type, string method, int parameters)
        {
            Type t = ModAssemblyHook.GameType(type);
            if (t == null) return null;
            MethodBase match = null;
            foreach (MethodInfo m in t.GetMethods(All))
            {
                if (m.Name != method || (parameters >= 0 && m.GetParameters().Length != parameters)) continue;
                if (match != null) return null;
                match = m;
            }
            return match;
        }

        static void Scope(MethodBase m, string tag, string name, KeyOf key, Func<object, object[], string> salt = null)
        {
            if (randomState == null || initState == null || battleTechGame == null) return;
            if (m == null) { log(Tag + "|point-missing|" + name + " not found"); return; }
            if (sites.ContainsKey(m)) { log(Tag + "|scope|" + name + "|shared with " + sites[m].Tag + ":" + sites[m].Name); return; }
            sites[m] = new Site { Tag = tag, Name = name, Key = key, Salt = salt };
            string prefix = m.IsStatic ? nameof(StaticPrefix) : nameof(InstancePrefix);
            try { harmony.Patch(m, prefix: Mine(prefix, Priority.First), finalizer: Mine(nameof(Finalizer), Priority.Last)); }
            catch (Exception e) { sites.Remove(m); log(Tag + "|site-failed|scope " + name + " on " + m.DeclaringType.FullName + "." + m.Name + "|" + e.Message); return; }
            log(Tag + "|installed|scope " + name + " on " + m.DeclaringType.FullName + "." + m.Name);
        }

        static HarmonyMethod Mine(string name, int priority)
            => new HarmonyMethod(typeof(CoopRngSweepDeterminism).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };

        static void InstancePrefix(MethodBase __originalMethod, object __instance, object[] __args, ref object __state) => Open(__originalMethod, __instance, __args, ref __state);
        static void StaticPrefix(MethodBase __originalMethod, object[] __args, ref object __state) => Open(__originalMethod, null, __args, ref __state);

        static void Open(MethodBase original, object instance, object[] args, ref object state)
        {
            state = null;
            if (!sites.TryGetValue(original, out Site site)) return;
            try
            {
                if (!IsCoopCombatNow()) return;
                string contract = CoopContract() ?? contractSalt;
                if (contract == null && site.Salt != null) { try { contract = site.Salt(instance, args); } catch { } }
                if (contract == null) return;
                contractSalt = contract;
                string key = site.Key(instance, args, Get(Combat(), "TurnDirector"));
                if (key == null) { lock (frameCounts) { nullKeys.TryGetValue(site.Name, out int nk); nullKeys[site.Name] = nk + 1; } return; }   // counted: a key that is always null opens no frame (to-hit, R-fix9)
                ulong h = Fnv(contract + "|rngsweep|" + site.Name + "|" + key);
                var frame = new Frame { Outer = current, Name = site.Name, Key = site.Name + "|" + key, Seed = (int)(uint)(h ^ (h >> 32)), UnityState = randomState.GetValue(null, null) };
                lock (frameCounts) { frameCounts.TryGetValue(site.Name, out int fc); frameCounts[site.Name] = fc + 1; }
                initState.Invoke(null, new object[] { frame.Seed });
                current = frame;
                state = frame;
                Row(site.Tag, site.Name, "seed|" + site.Name + "|" + key + "|" + frame.Seed);
            }
            catch (Exception e) { log(Tag + "|prefix-failed|" + site.Name + "|" + e.Message); }   // a saved state is still put back
        }

        static void Finalizer(object __state)
        {
            if (!(__state is Frame f)) return;
            try { randomState.SetValue(null, f.UnityState, null); }
            catch (Exception e) { log(Tag + "|restore-failed|" + e.Message); }
            current = f.Outer;
        }

        // At mission end: one row per R1/R3 point (frames opened, draws R2 served inside) and one per mod assembly. This is the
        // coverage half of the proof (R-fix6 could only show armed/installed rows).
        static void LogSummary()
        {
            try
            {
                var names = new List<string>();
                foreach (Site s in sites.Values) if (!names.Contains(s.Name)) names.Add(s.Name);
                names.Sort(StringComparer.Ordinal);
                int frames = 0, draws = 0;
                lock (frameCounts) lock (drawCounts)
                {
                    foreach (string name in names)
                    {
                        frameCounts.TryGetValue(name, out int f); drawCounts.TryGetValue(name, out int d); nullKeys.TryGetValue(name, out int nk);
                        frames += f; draws += d;
                        log(Tag + "|summary|point|" + name + "|frames=" + f + "|draws=" + d + "|nullkeys=" + nk);
                    }
                }
                log(Tag + "|summary|points|count=" + names.Count + "|frames=" + frames + "|draws=" + draws);
                var asms = new List<string>(drawAssemblies.Keys);
                asms.Sort(StringComparer.Ordinal);
                lock (drawAssemblies)
                    foreach (string a in asms) log(Tag + "|summary|assembly|" + a + "|draws=" + drawAssemblies[a]);
            }
            catch (Exception e) { log(Tag + "|summary-failed|" + e.Message); }
        }


        static bool IsCoopCombatNow()
        {
            try
            {
                object net = Get(battleTechGame?.GetValue(null, null), "NetworkGameInstance");
                return Get(net, "IsCoopCombat") is bool coop && coop;
            }
            catch { return false; }
        }

        static string CoopContract()
        {
            object combat = Combat();
            return Get(Get(combat, "ActiveContract"), "GUID") as string;
        }

        static object Combat() => battleTechGame == null ? null : Get(battleTechGame.GetValue(null, null), "Combat");

        static string RoundPhase(object turn) => "|r" + Get(turn, "CurrentRound") + "|p" + Get(turn, "CurrentPhase");

        static string Join(object[] args)
        {
            if (args == null) return "-";
            var sb = new System.Text.StringBuilder();
            foreach (object o in args) sb.Append(o is string s ? s : o?.GetType().Name).Append(',');
            return sb.ToString();
        }

        static string Join(object list)
        {
            if (!(list is IEnumerable e)) return "-";
            var sb = new System.Text.StringBuilder();
            foreach (object o in e) sb.Append(o is string s ? s : o?.GetType().Name).Append(',');
            return sb.ToString();
        }

        static string Component(object c) => Get(Get(c, "parent"), "GUID") + "|" + Get(c, "uid") + "|" + Get(c, "defId");

        static string Hit(object hitInfo) => "|h" + Get(hitInfo, "attackSequenceId") + "." + Get(hitInfo, "attackGroupIndex") + "." + Get(hitInfo, "attackWeaponIndex") + "." + Get(hitInfo, "attackerId");

        static string LastWaypoint(object waypoints)
        {
            if (!(waypoints is IEnumerable e)) return "-";
            object last = null;
            foreach (object o in e) last = o;
            return Get(Get(last, "Position"), "x") + "," + Get(Get(last, "Position"), "z");
        }

        static string XZ(object v) => Get(v, "x") + "," + Get(v, "z");

        static void Row(string tag, string site, string text)
        {
            rowCounts.TryGetValue(tag + site, out int n);
            if (n >= 40) return;
            rowCounts[tag + site] = n + 1;
            log(tag + "|" + text);
        }

        // In place of new System.Random(): seeded from FNV(contract | scope key | site | n) inside a scope, FNV(contract | site | n) outside one.
        static Random NewRandomFor(string site)
        {
            string contract = CoopContract() ?? contractSalt;
            if (contract == null) return new Random();
            Frame f = current;
            string key, counter = "new-random|" + site;
            int n;
            if (f != null)
            {
                if (f.Guids == null) f.Guids = new Dictionary<string, int>();
                f.Guids.TryGetValue(counter, out n); f.Guids[counter] = ++n;
                key = contract + "|" + f.Key + "|" + counter + "|" + n;
            }
            else
            {
                if (guidContract != contract) { guidContract = contract; guidCounts.Clear(); }
                guidCounts.TryGetValue(counter, out n); guidCounts[counter] = ++n;
                key = contract + "|" + counter + "|" + n;
            }
            ulong h = Fnv(key);
            int seed = (int)(uint)(h ^ (h >> 32));
            guidTags.TryGetValue(site, out string tag);
            Row(tag ?? Tag, counter, "new-random|" + site + "|" + n + "|" + (f != null ? f.Key : "-") + "|" + seed);
            return new Random(seed);
        }

        // In place of Guid.NewGuid() in every mod assembly's methods (keyed-identity sites keep their own redirect).
        static Guid NewGuidFor(string site)
        {
            string contract = CoopContract() ?? contractSalt;
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
            Row(tag ?? Tag, site, "guid|" + site + "|" + n + "|" + (f != null ? f.Key : "-"));
            return new Guid(bytes);
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
