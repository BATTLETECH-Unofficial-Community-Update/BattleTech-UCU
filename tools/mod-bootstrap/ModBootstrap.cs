// Doorstop target for UCU's Unity 6 player: makes stock ModTek v4 run on Mono 6.13, then starts it.
// ModTek's own files stay untouched.  doorstop_config.ini's target_assembly names this DLL; everything else in it stays
// as ModTek ships it.  Decided 2026-11-04.
//
// Four differences from the Mono that shipped with the 2018 game:
//  1. ModTek/lib/netstandard.dll (2.0) comes first on doorstop's search path and lacks what UnityEngine needs from 2.1.
//     The first assembly of a name to load wins, so the player's own facade is loaded before anything asks for it.
//     This DLL must therefore reference mscorlib only.
//  2. Assembly.LoadFrom(string, bool) gained a StackCrawlMark parameter: ModTek's Harmony patch fails to find its target.
//  3. Assembly.get_Location moved to RuntimeAssembly: ModTek's patch would apply and never run.
//  4. Assembly.LoadFile no longer goes through LoadFrom, so ModTek's shim missed everything its preloader loads.
// 2 to 4 are retargeted where ModTek applies them, through Harmony.PatchAll(Type).
//  5. Mono loads a framework facade it can't find beside the referencing assembly (System.Threading.Tasks 4.0.0.0, say) from
//     <lib>/Facades. The Unity 6 player ships no Facades folder and copies into Managed only the facades its own scripts
//     use, so a mod DLL built against a netstandard 1.x contract dies with TypeLoadException. CustomPrewarm's bundled
//     System.Threading.Tasks.Extensions does, and its msgpack cache is never written (GitHub issue #4). ResolveFacade
//     answers those names with netstandard, which forwards their types, after every other resolver has had its turn.
//
// One difference from Unity 2018: UnityEngine.Input moved from CoreModule to InputLegacyModule with no forwarder, so mods
// that reference it die with TypeLoadException. Nothing has loaded CoreModule when doorstop calls us, and the first
// assembly of a name to load wins, so a forwarded copy is written under Mods/.port and loaded here. The player's own
// CoreModule stays byte-stock; only modded runs use the copy.
// The same copy carries a second fix. Unity 2018 created a block-compressed texture (DXT1, DXT5, BC4/5/7) of any size;
// Unity 6 refuses one whose width or height is not a multiple of 4. IRBTModUtils' sprite loader replaces the game's own
// (which UCU already fixed, transplant patch 005) and makes a 2x2 DXT5 texture before LoadImage, so every PNG
// portrait it loads fails and a save with such a pilot in the task timeline stops the ship UI (GitHub issue #3). The copy
// turns those requests into RGBA32, the format LoadImage leaves a PNG in anyway; other sizes and formats are untouched.
// Newtonsoft is 13 where retail had 10, and ModTek calls one of its internals: the same route adds retail's overload back.
// CustomPrewarmCompat.cs works around a CustomPrewarm bug that fails on retail too (GitHub issue #5); see that file.
// BTDebugCompat.cs gives BTDebug's gizmo materials a shader when Shader.Find cannot see a retail bundle shader; see that file.
// PR308Guard.cs keeps MonoMod.Core before 1.3.5 (ModTek 4.4.x) from writing out-of-reach detours under Proton; see that file.
// CustomUnitSpawnCompat.cs lets CustomUnitSpawn's Random.Range patches bind to Unity 6's renamed parameters; see that file.
// ImguiTooltipCompat.cs clears GUI.tooltip as each IMGUI behaviour begins an event, as retail's engine did (issues 21, 31).
// ImguiTextColorCompat.cs draws IMGUI text colours as retail did, not linearised twice (issue 21: CombatLog's grip).
// Combat-save adapters and deferrals are gated by their owning assemblies and run only on the save/load paths documented in their files.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Doorstop
{
    public static class Entrypoint
    {
        // The installer turns mod support on by default, so this runs on vanilla installs too. With no ModTek in Mods\ it does
        // nothing but leave a one-line log, and touches no other assembly: the game starts as it would without Doorstop.
        public static void Start()
        {
            string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!File.Exists(Path.Combine(root, Path.Combine("Mods", Path.Combine("ModTek", Path.Combine("lib", "ModTek.Preloader.dll"))))))
            {
                File.WriteAllText(Path.Combine(root, "BattleTechPortBootstrap.log"), "no ModTek in Mods\\ModTek: nothing to do" + Environment.NewLine);
                return;
            }
            BattleTechPort.ModBootstrap.Run();
        }
    }
}

namespace BattleTechPort
{
    public static class ModBootstrap
    {
        const string LoadPatches = "ModTek.Preloader.Harmony12X.ShimInjectorPatches+AssemblyLoadPatches";
        const string LocationPatch = "ModTek.Preloader.Harmony12X.ShimInjectorPatches+AssemblyLocationPatch";
        static string logPath;
        static int retargeted;
        static MethodInfo loadFromPrefix;
        static Assembly netstandard;
        [ThreadStatic] static string resolving; // the facade this thread is asking the later handlers for

        // The names in Mono 6.13's lib/mono/4.5/Facades, less netstandard itself: what the Editor's Mono resolves on its own.
        const string FacadeNames =
            "Microsoft.Win32.Primitives Microsoft.Win32.Registry.AccessControl Microsoft.Win32.Registry System.AppContext System.Buffers " +
            "System.Collections.Concurrent System.Collections.NonGeneric System.Collections.Specialized System.Collections " +
            "System.ComponentModel.Annotations System.ComponentModel.EventBasedAsync System.ComponentModel.Primitives " +
            "System.ComponentModel.TypeConverter System.ComponentModel System.Console System.Data.Common System.Data.SqlClient " +
            "System.Diagnostics.Contracts System.Diagnostics.Debug System.Diagnostics.FileVersionInfo System.Diagnostics.Process " +
            "System.Diagnostics.StackTrace System.Diagnostics.TextWriterTraceListener System.Diagnostics.Tools System.Diagnostics.TraceEvent " +
            "System.Diagnostics.TraceSource System.Diagnostics.Tracing System.Drawing.Primitives System.Dynamic.Runtime " +
            "System.Globalization.Calendars System.Globalization.Extensions System.Globalization System.IO.Compression.ZipFile " +
            "System.IO.FileSystem.AccessControl System.IO.FileSystem.DriveInfo System.IO.FileSystem.Primitives System.IO.FileSystem.Watcher " +
            "System.IO.FileSystem System.IO.IsolatedStorage System.IO.MemoryMappedFiles System.IO.Pipes System.IO.UnmanagedMemoryStream " +
            "System.IO System.Linq.Expressions System.Linq.Parallel System.Linq.Queryable System.Linq System.Memory " +
            "System.Net.AuthenticationManager System.Net.Cache System.Net.Http.Rtc System.Net.HttpListener System.Net.Mail " +
            "System.Net.NameResolution System.Net.NetworkInformation System.Net.Ping System.Net.Primitives System.Net.Requests " +
            "System.Net.Security System.Net.ServicePoint System.Net.Sockets System.Net.Utilities System.Net.WebHeaderCollection " +
            "System.Net.WebSockets.Client System.Net.WebSockets System.ObjectModel System.Reflection.DispatchProxy " +
            "System.Reflection.Emit.ILGeneration System.Reflection.Emit.Lightweight System.Reflection.Emit System.Reflection.Extensions " +
            "System.Reflection.Primitives System.Reflection.TypeExtensions System.Reflection System.Resources.Reader " +
            "System.Resources.ReaderWriter System.Resources.ResourceManager System.Resources.Writer System.Runtime.CompilerServices.VisualC " +
            "System.Runtime.Extensions System.Runtime.Handles System.Runtime.InteropServices.RuntimeInformation " +
            "System.Runtime.InteropServices.WindowsRuntime System.Runtime.InteropServices System.Runtime.Loader System.Runtime.Numerics " +
            "System.Runtime.Serialization.Formatters System.Runtime.Serialization.Json System.Runtime.Serialization.Primitives " +
            "System.Runtime.Serialization.Xml System.Runtime System.Security.AccessControl System.Security.Claims " +
            "System.Security.Cryptography.Algorithms System.Security.Cryptography.Cng System.Security.Cryptography.Csp " +
            "System.Security.Cryptography.DeriveBytes System.Security.Cryptography.Encoding System.Security.Cryptography.Encryption.Aes " +
            "System.Security.Cryptography.Encryption.ECDiffieHellman System.Security.Cryptography.Encryption.ECDsa " +
            "System.Security.Cryptography.Encryption System.Security.Cryptography.Hashing.Algorithms System.Security.Cryptography.Hashing " +
            "System.Security.Cryptography.OpenSsl System.Security.Cryptography.Pkcs System.Security.Cryptography.Primitives " +
            "System.Security.Cryptography.ProtectedData System.Security.Cryptography.RSA System.Security.Cryptography.RandomNumberGenerator " +
            "System.Security.Cryptography.X509Certificates System.Security.Principal.Windows System.Security.Principal " +
            "System.Security.SecureString System.ServiceModel.Duplex System.ServiceModel.Http System.ServiceModel.NetTcp " +
            "System.ServiceModel.Primitives System.ServiceModel.Security System.ServiceProcess.ServiceController " +
            "System.Text.Encoding.CodePages System.Text.Encoding.Extensions System.Text.Encoding System.Text.RegularExpressions " +
            "System.Threading.AccessControl System.Threading.Overlapped System.Threading.Tasks.Extensions System.Threading.Tasks.Parallel " +
            "System.Threading.Tasks System.Threading.Thread System.Threading.ThreadPool System.Threading.Timer System.Threading " +
            "System.ValueTuple System.Xml.ReaderWriter System.Xml.XDocument System.Xml.XPath.XDocument System.Xml.XPath.XmlDocument " +
            "System.Xml.XPath System.Xml.XmlDocument System.Xml.XmlSerializer System.Xml.Xsl.Primitives";
        static string[] facades;

        // Registered first, so it runs the handlers added after it itself and answers only when all of them return null: a
        // mod's own copy of System.ValueTuple or System.Memory, which ModTek finds, still wins over netstandard's forwarders.
        // A name netstandard doesn't forward a type for still fails, as a TypeLoadException on that type.
        static Assembly ResolveFacade(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name);
            if (name.Name == resolving) return null;
            if (facades == null) facades = FacadeNames.Split(' ');
            if (Array.IndexOf(facades, name.Name) < 0) return null;
            string outer = resolving;
            resolving = name.Name;
            try
            {
                var field = typeof(AppDomain).GetField("AssemblyResolve", BindingFlags.Instance | BindingFlags.NonPublic);
                var all = field == null ? null : field.GetValue(AppDomain.CurrentDomain) as ResolveEventHandler;
                if (all != null)
                {
                    bool after = false;
                    foreach (ResolveEventHandler handler in all.GetInvocationList())
                    {
                        if (!after) { after = handler.Method == ((ResolveEventHandler)ResolveFacade).Method; continue; }
                        Assembly found = handler(sender, args);
                        if (found != null) return found;
                    }
                }
            }
            finally { resolving = outer; }
            Log("facade|" + args.Name + "|netstandard");
            return netstandard;
        }

        public static void Run()
        {
            string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            logPath = Path.Combine(root, "BattleTechPortBootstrap.log");
            File.WriteAllText(logPath, "");
            try
            {
                string managed = Environment.GetEnvironmentVariable("DOORSTOP_MANAGED_FOLDER_DIR");
                if (string.IsNullOrEmpty(managed)) managed = Path.Combine(root, "BattleTech_Data", "Managed");
                netstandard = Assembly.LoadFile(Path.Combine(managed, "netstandard.dll"));
                Log("netstandard=" + netstandard.FullName);
                AppDomain.CurrentDomain.AssemblyResolve += ResolveFacade;
                CustomPrewarmCompat.Register(Log);
                // Co-op determinism adapters (gh issue 24). Each one patches only when the mod it adapts is loaded (ModAssemblyHook) and acts only in a co-op combat
                // or on that mod's own data; none reads an environment variable. One failing registration must not stop the others or the save adapters below.
                RegisterCoop("ComponentGuidCoopDeterminism", () => ComponentGuidCoopDeterminism.Register(Log));      // CustomAmmoCategories, CustomComponents
                RegisterCoop("CustomComponentsFixOnce", () => CustomComponentsFixOnce.Register(Log));                // CustomComponents
                RegisterCoop("CustomUnitsCoopMoveStat", () => CustomUnitsCoopMoveStat.Register(Log));                // CustomUnits (needs UCU patch 145)
                RegisterCoop("CustomUnitsCoopContract", () => CustomUnitsCoopContract.Register(Log));                // CustomUnits
                RegisterCoop("CustomUnitsCoopCompare", () => CustomUnitsCoopCompare.Register(Log));                  // CustomUnits
                RegisterCoop("CustomUnitsCoopFiringArc", () => CustomUnitsCoopFiringArc.Register(Log));              // CustomUnits (fake-vehicle CUFiringArc, parse-order race)
                RegisterCoop("MissionControlCoopDeterminism", () => MissionControlCoopDeterminism.Register(Log));    // MissionControl: drop-data sync only (its random rolls are CoopRngSweepDeterminism's)
                RegisterCoop("FullXotlTablesCoopDeterminism", () => FullXotlTablesCoopDeterminism.Register(Log));    // FullXotlTables (BEX:T unit tables): opposing unit picks seeded per spawner (G6)
                RegisterCoop("StrategicOperationsCoopDeterminism", () => StrategicOperationsCoopDeterminism.Register(Log));   // StrategicOperations
                RegisterCoop("CoopActivationDecisionGate", () => CoopActivationDecisionGate.Register(Log));          // any mod that prefixes OnTurnActorActivateComplete (needs UCU patch 169)
                RegisterCoop("CustomAmmoCategoriesCoopBraceModes", () => CustomAmmoCategoriesCoopBraceModes.Register(Log));   // CustomAmmoCategories
                RegisterCoop("CustomAmmoCategoriesCoopModeSync", () => CustomAmmoCategoriesCoopModeSync.Register(Log));   // CustomAmmoCategories (needs UCU patch 166)
                RegisterCoop("PracticeMakesPerfectCoopStratCom", () => PracticeMakesPerfectCoopStratCom.Register(Log));   // PracticeMakesPerfect
                RegisterCoop("ExtendedCEPathingCoopSync", () => ExtendedCEPathingCoopSync.Register(Log));            // Extended_CE (BEX:T run rules read the planning peer's path; needs UCU patch 166)
                if (CombatSavesRuntimeGate.IsEnabled())
                {
                    RegisterSaveAdapters(true);
                }
                else
                {
                    // Issue 24 fault B (UCU patch 161, Sam 2026-11-21): a co-op resync or rejoin snapshot needs the save adapters
                    // with the marker absent too. They are registered when the first co-op combat begins (NetworkGameInstance.
                    // IsCoopCombat), so a process with no co-op combat gets no adapter and no patch from them.
                    CombatSavesRuntimeGate.SetCoopActions(ArmCoopSaveAdapters, DisarmCoopSaveAdapters);
                    Log("save-adapters|deferred|marker absent: registered at the first co-op combat");
                }
#if B25_SAVE_SHIM_TEST
                if (SaveShimTestGate.IsEnabled(root, Environment.GetEnvironmentVariable("UCU_BTA_COMBAT_SAVES_TEST")))
                {
                    Log("save-shim-test|enabled|marker=Mods/.port/bta-combat-saves-test.on");
                    ModStateCensus.Register(Log, root);
                }
#endif
                BTDebugCompat.Register(Log);
                CustomUnitSpawnCompat.Register(Log);
                ShaderBundleCompat.Register(Log);
                ImguiTooltipCompat.Register(Log);
                ImguiTextColorCompat.Register(Log);
                UnityInOverloadLookupCompat.Register(Log);   // Unity 6 `in` twins of UnityEngine methods: a by-name AccessTools lookup gets retail's method (gh issue 42, BTX_ExpansionPack)
                AbilifierBarracksPanelCompat.Register(Log);   // Abilifier: Barracks pip clicks reach the panel on screen (gh issue 36)
                RegisterCoop("AttackImprovementModCoopCalledShot", () => AttackImprovementModCoopCalledShot.Register(Log));   // AttackImprovementMod + CustomUnits (W-L7 volley)
                RegisterCoop("PilotStatHistoryLoadCompat", () => PilotStatHistoryLoadCompat.Register(Log));   // any co-op combat-save load: pilot stat history (CBTBE-5 compare 217)
                RegisterCoop("LoadSteps", () => LoadSteps.Register(Log));   // the load-step registry: phase 1 after hydrate (component order), phase 2 after _Init on a load (effect caches), phase 3 guest snapshot apply (generic carries of SO ModState, CAE repair sets, CAC jam queues, PanicSystem tracker, actor fields) and the co-op census (L1, L2; needs UCU patch 166)
                RegisterCoop("CoopActorTransientStateSync", () => CoopActorTransientStateSync.Register(Log));   // any co-op snapshot load: the actor occupiedDesignMask (the other actor fields are LoadSteps carries; needs UCU patch 166)
                RegisterCoop("CustomAmmoCategoriesCoopArtillerySync", () => CustomAmmoCategoriesCoopArtillerySync.Register(Log));   // CustomAmmoCategories artillery mode, terrain points (CBTBE-8 compares 10, 223, 335; needs UCU patch 166)
                RegisterCoop("CoopRngSweepDeterminism", () => CoopRngSweepDeterminism.Register(Log));   // generic co-op RNG for every non-UCU mod (R1 shared points, R2 draw/Guid/new Random redirect, R3 declarations)
                RegisterCoop("CBTBehaviorsEnhancedCoopStartupDecision", () => CBTBehaviorsEnhancedCoopStartupDecision.Register(Log));   // CBTBehaviorsEnhanced: AI startup-check outcomes carried to the guest (D-20261122-6)
                RegisterCoop("CustomActivatableEquipmentCoopToggle", () => CustomActivatableEquipmentCoopToggle.Register(Log));   // CustomActivatableEquipment: HUD toggles sent at once (D-20261122-6; needs UCU patches 166, 176)
                RegisterCoop("CustomActivatableEquipmentCoopCharges", () => CustomActivatableEquipmentCoopCharges.Register(Log));   // CustomActivatableEquipment: a co-op charge read adds no stat (logic-cae-charges-1)
                RegisterCoop("StrategicOperationsCoopSwarmDecision", () => StrategicOperationsCoopSwarmDecision.Register(Log));   // StrategicOperations: AI dismount / deswarm, swarm melee location (D-20261122-6)
                RegisterCoop("StrategicOperationsCoopAiCommand", () => StrategicOperationsCoopAiCommand.Register(Log));   // StrategicOperations: AI command abilities (BADrop and siblings) activated on the host, replayed on guests (so-ai-badrop-coop)
                RegisterCoop("StrategicOperationsCoopDropPod", () => StrategicOperationsCoopDropPod.Register(Log));   // StrategicOperations: AI drop-pod landing and deploy evasion carried from the host, dropped unit held out of the MECHS compare until then (so-ai-badrop-coop)
                RegisterCoop("CacDynamicMapCoopSync", () => CacDynamicMapCoopSync.Register(Log));   // CustomAmmoCategories minefields, temp design masks (snapshot sweep; needs UCU patch 166)
                RegisterCoop("CacDeferredEffectCoopSync", () => CacDeferredEffectCoopSync.Register(Log));   // CustomAmmoCategories pending deferred effects (snapshot sweep; needs UCU patch 166)
                RegisterCoop("StrategicOperationsCoopCommandSync", () => StrategicOperationsCoopCommandSync.Register(Log));   // StrategicOperations command abilities; host drops strafes in flight, deferred spawns and refunds their use and cost; ahead of so-coop so its rollback is in what so-coop sends (gap review; needs UCU patch 166)
                RegisterCoop("MonsterMashupCoopLinkSync", () => MonsterMashupCoopLinkSync.Register(Log));   // MonsterMashup linked actors, support spawns, weapon arcs, CustomUnits links (gap review; needs UCU patch 166)
                RegisterCoop("CustomAmmoCategoriesCoopAmsGeometry", () => CustomAmmoCategoriesCoopAmsGeometry.Register(Log));   // CustomAmmoCategories AMS trajectory points from the host (CBTBE-9 compare 163; needs UCU patch 014)
                RewriteCoreModule(managed, Path.Combine(root, "Mods", ".port"));
                RestoreJsonEvaluate(managed, Path.Combine(root, "Mods", ".port"));
                Start(Path.Combine(root, "Mods", "ModTek", "lib", "ModTek.Preloader.dll"));
                Log("done|retargeted=" + retargeted);
            }
            catch (Exception ex)
            {
                Log("FAILED|" + ex);
                throw;
            }
        }

        // Kept apart from Run so that the Cecil reference resolves only after netstandard has loaded. A failure here costs
        // the 20 mods that use Input and the mods that load PNG sprites, not the other mods, so it is logged and the run goes on.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void RewriteCoreModule(string managed, string cacheDir)
        {
            LoadRewritten("forward", managed, cacheDir, "UnityEngine.CoreModule.dll",
                cached => WriteCoreModule(Path.Combine(managed, "UnityEngine.CoreModule.dll"), Path.Combine(managed, "UnityEngine.InputLegacyModule.dll"), cacheDir, cached));
        }

        // The game's Newtonsoft is 13 where retail's was 10, and ModTek's JSON merges call internals that changed: Evaluate and
        // ExecuteFilter took a bool where they now take settings, and FieldFilter.Name was a property. A failure here costs the
        // merges, not the run.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void RestoreJsonEvaluate(string managed, string cacheDir)
        {
            LoadRewritten("json", managed, cacheDir, "Newtonsoft.Json.dll",
                cached => WriteJsonEvaluate(Path.Combine(managed, "Newtonsoft.Json.dll"), cacheDir, cached));
        }

        // Writes a changed copy of a Managed DLL under Mods/.port, once per source file and bootstrap build, and loads it: the
        // first assembly of a name to load wins, and the player's own file stays byte-stock. The bootstrap's own module ID is
        // in the stamp so that an updated bootstrap rewrites a copy an older one left behind.
        static void LoadRewritten(string tag, string managed, string cacheDir, string dll, Func<string, bool> write)
        {
            try
            {
                string cached = Path.Combine(cacheDir, dll);
                string stampPath = cached + ".source";
                var source = new FileInfo(Path.Combine(managed, dll));
                string stamp = source.Length + "|" + source.LastWriteTimeUtc.Ticks + "|" + typeof(ModBootstrap).Module.ModuleVersionId;
                bool fresh = File.Exists(cached) && File.Exists(stampPath) && File.ReadAllText(stampPath) == stamp;
                if (!fresh)
                {
                    if (!write(cached))
                        return;
                    File.WriteAllText(stampPath, stamp);
                }
                Log(tag + "|" + (fresh ? "cached" : "written") + "|" + Assembly.LoadFile(cached).Location);
            }
            catch (Exception ex)
            {
                Log(tag + "|FAILED|" + ex);
            }
        }

        static bool WriteJsonEvaluate(string source, string cacheDir, string cached)
        {
            using (var module = ModuleDefinition.ReadModule(source))
            {
                TypeDefinition jpath = module.GetType("Newtonsoft.Json.Linq.JsonPath.JPath");
                TypeDefinition settings = module.GetType("Newtonsoft.Json.Linq.JsonSelectSettings");
                MethodDefinition evaluate = null;
                foreach (var method in jpath.Methods)
                {
                    if (method.Name != "Evaluate" || method.Parameters.Count != 4) continue;
                    if (method.Parameters[3].ParameterType.FullName == "System.Boolean") { Log("json|skip|bool overload exists"); return false; }
                    if (method.Parameters[3].ParameterType.FullName == settings.FullName) evaluate = method;
                }
                if (evaluate == null) { Log("json|skip|no Evaluate taking JsonSelectSettings"); return false; }
                MethodDefinition ctor = null, setError = null;
                foreach (var method in settings.Methods)
                {
                    if (method.IsConstructor && !method.IsStatic && method.Parameters.Count == 0) ctor = method;
                    if (method.Name == "set_ErrorWhenNoMatch") setError = method;
                }
                var overload = new MethodDefinition("Evaluate", evaluate.Attributes, evaluate.ReturnType);
                for (int i = 0; i < 3; i++)
                    overload.Parameters.Add(new ParameterDefinition(evaluate.Parameters[i].Name, Mono.Cecil.ParameterAttributes.None, evaluate.Parameters[i].ParameterType));
                overload.Parameters.Add(new ParameterDefinition("errorWhenNoMatch", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Boolean));
                var il = overload.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Newobj, ctor);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Callvirt, setError);
                il.Emit(OpCodes.Call, evaluate);
                il.Emit(OpCodes.Ret);
                jpath.Methods.Add(overload);

                TypeDefinition filter = module.GetType("Newtonsoft.Json.Linq.JsonPath.PathFilter");
                MethodDefinition execute = null;
                foreach (var method in filter.Methods)
                    if (method.Name == "ExecuteFilter" && method.Parameters.Count == 3) execute = method;
                var executeBool = new MethodDefinition("ExecuteFilter", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig, execute.ReturnType);
                for (int i = 0; i < 2; i++)
                    executeBool.Parameters.Add(new ParameterDefinition(execute.Parameters[i].Name, Mono.Cecil.ParameterAttributes.None, execute.Parameters[i].ParameterType));
                executeBool.Parameters.Add(new ParameterDefinition("errorWhenNoMatch", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Boolean));
                il = executeBool.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Newobj, ctor);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Callvirt, setError);
                il.Emit(OpCodes.Callvirt, execute);
                il.Emit(OpCodes.Ret);
                filter.Methods.Add(executeBool);

                // Name was a property in 10 and is a field in 13.
                TypeDefinition fieldFilter = module.GetType("Newtonsoft.Json.Linq.JsonPath.FieldFilter");
                FieldDefinition name = null;
                foreach (var field in fieldFilter.Fields)
                    if (field.Name == "Name") name = field;
                var getName = new MethodDefinition("get_Name", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName, module.TypeSystem.String);
                il = getName.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, name);
                il.Emit(OpCodes.Ret);
                fieldFilter.Methods.Add(getName);

                Directory.CreateDirectory(cacheDir);
                module.Write(cached + ".tmp");
            }
            File.Delete(cached);
            File.Move(cached + ".tmp", cached);
            return true;
        }

        static bool WriteCoreModule(string core, string legacy, string cacheDir, string cached)
        {
            using (var input = ModuleDefinition.ReadModule(legacy))
            using (var module = ModuleDefinition.ReadModule(core))
            {
                bool forwarded = ForwardInput(module, input);
                bool textures = AllowAnyCompressedSize(module);
                if (!forwarded && !textures) return false;
                Directory.CreateDirectory(cacheDir);
                module.Write(cached + ".tmp");
            }
            File.Delete(cached);
            File.Move(cached + ".tmp", cached);
            return true;
        }

        static bool ForwardInput(ModuleDefinition module, ModuleDefinition input)
        {
            foreach (var type in module.Types)
            {
                if (type.FullName == "UnityEngine.Input") { Log("forward|skip|CoreModule defines Input"); return false; }
            }
            foreach (var type in module.ExportedTypes)
            {
                if (type.FullName == "UnityEngine.Input") { Log("forward|skip|already forwarded"); return false; }
            }
            var target = new AssemblyNameReference(input.Assembly.Name.Name, input.Assembly.Name.Version);
            module.AssemblyReferences.Add(target);
            module.ExportedTypes.Add(new ExportedType("UnityEngine", "Input", module, target) { Attributes = (Mono.Cecil.TypeAttributes)0x00200000 });
            return true;
        }

        // TextureFormat values Unity 6 stores in 4x4 blocks: DXT1, DXT5, BC4, BC5, BC7, DXT1Crunched, DXT5Crunched. BC6H (24)
        // is left alone: it is HDR, and RGBA32 would clip it.
        static readonly int[] BlockFormats = { 10, 12, 26, 27, 25, 28, 29 };
        const string BlockSafeFormat = "PortBlockSafeFormat";

        // Every public Texture2D(int, int, TextureFormat, ...) constructor ends in one internal constructor, the only one that
        // calls Texture's base constructor itself. Its format argument is replaced on entry, before ValidateFormat and
        // Internal_Create see it, by PortBlockSafeFormat(width, height, format), which this adds to Texture2D.
        static bool AllowAnyCompressedSize(ModuleDefinition module)
        {
            TypeDefinition texture2D = module.GetType("UnityEngine.Texture2D");
            if (texture2D == null) { Log("textures|skip|no Texture2D"); return false; }
            foreach (var method in texture2D.Methods)
                if (method.Name == BlockSafeFormat) { Log("textures|skip|already rewritten"); return false; }

            MethodDefinition ctor = null;
            int found = 0;
            foreach (var method in texture2D.Methods)
            {
                if (!method.IsConstructor || method.IsStatic || !method.HasBody || method.Parameters.Count < 3) continue;
                if (method.Parameters[0].ParameterType.FullName != "System.Int32" || method.Parameters[1].ParameterType.FullName != "System.Int32") continue;
                if (method.Parameters[2].ParameterType.FullName != "UnityEngine.TextureFormat") continue;
                foreach (var instruction in method.Body.Instructions)
                {
                    var callee = instruction.Operand as MethodReference;
                    if (instruction.OpCode == OpCodes.Call && callee != null && callee.Name == ".ctor" && callee.DeclaringType.FullName == "UnityEngine.Texture")
                    {
                        ctor = method;
                        found++;
                        break;
                    }
                }
            }
            if (found != 1) { Log("textures|skip|internal TextureFormat constructors=" + found); return false; }

            TypeReference format = ctor.Parameters[2].ParameterType;
            var helper = new MethodDefinition(BlockSafeFormat,
                Mono.Cecil.MethodAttributes.Private | Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.HideBySig, format);
            helper.Parameters.Add(new ParameterDefinition("width", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
            helper.Parameters.Add(new ParameterDefinition("height", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
            helper.Parameters.Add(new ParameterDefinition("format", Mono.Cecil.ParameterAttributes.None, format));
            var il = helper.Body.GetILProcessor();
            Instruction keep = il.Create(OpCodes.Ldarg_2);
            Instruction demote = il.Create(OpCodes.Ldc_I4_4); // RGBA32
            // if (((width | height) & 3) == 0) return format;
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Ldc_I4_3);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Brfalse, keep);
            foreach (int blockFormat in BlockFormats)
            {
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldc_I4, blockFormat);
                il.Emit(OpCodes.Beq, demote);
            }
            il.Append(keep);
            il.Emit(OpCodes.Ret);
            il.Append(demote);
            il.Emit(OpCodes.Ret);
            texture2D.Methods.Add(helper);

            // format = PortBlockSafeFormat(width, height, format); at the top of the constructor.
            var body = ctor.Body.GetILProcessor();
            Instruction first = ctor.Body.Instructions[0];
            body.InsertBefore(first, body.Create(OpCodes.Ldarg, ctor.Parameters[0]));
            body.InsertBefore(first, body.Create(OpCodes.Ldarg, ctor.Parameters[1]));
            body.InsertBefore(first, body.Create(OpCodes.Ldarg, ctor.Parameters[2]));
            body.InsertBefore(first, body.Create(OpCodes.Call, helper));
            body.InsertBefore(first, body.Create(OpCodes.Starg, ctor.Parameters[2]));
            Log("textures|rewritten|" + ctor.FullName);
            return true;
        }

        // Kept apart from Run so that the Harmony reference resolves only after netstandard has loaded.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Start(string preloaderPath)
        {
            PR308Guard.Install(Log); // before the first Harmony.Patch, the bootstrap's own and then ModTek's
            var harmony = new Harmony("battletech.port.modbootstrap");
            harmony.Patch(typeof(Harmony).GetMethod("PatchAll", new[] { typeof(Type) }),
                prefix: new HarmonyMethod(typeof(ModBootstrap).GetMethod("PatchAllPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
            try
            {
                Assembly preloader = Assembly.LoadFrom(preloaderPath);
                Type entry = preloader.GetType("Doorstop.Entrypoint", true);
                MethodInfo start = entry.GetMethod("Start", BindingFlags.Static | BindingFlags.Public);
                if (start == null) throw new MissingMethodException("Doorstop.Entrypoint", "Start");
                Log("start|" + preloader.FullName);
                try
                {
                    start.Invoke(null, null);
                }
                catch (TargetInvocationException ex)
                {
                    throw new InvalidOperationException("ModTek preloader failed", ex.InnerException);
                }
            }
            finally
            {
                harmony.UnpatchSelf();
            }
        }

        static bool PatchAllPrefix(Harmony __instance, Type type)
        {
            if (type == null) return true;
            if (type.FullName == LoadPatches)
            {
                var stackMark = Type.GetType("System.Threading.StackCrawlMark", true).MakeByRefType();
                var target = typeof(Assembly).GetMethod("LoadFrom", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null,
                    new[] { typeof(string), typeof(bool), stackMark }, null);
                if (Retarget(__instance, type, "LoadFrom_Prefix", target)) return true;
                loadFromPrefix = type.GetMethod("LoadFrom_Prefix", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                var loadFile = typeof(Assembly).GetMethod("LoadFile", new[] { typeof(string), typeof(System.Security.Policy.Evidence) });
                __instance.Patch(loadFile, prefix: new HarmonyMethod(typeof(ModBootstrap).GetMethod("LoadFilePrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                retargeted++;
                Log("retarget|LoadFilePrefix|prefix|System.Reflection.Assembly.LoadFile");
                // Skipping ModTek's PatchAll skips these two as well; their targets kept their names.
                PatchAppDomain(__instance, type, "LoadAssembly_Prefix", "LoadAssembly");
                PatchAppDomain(__instance, type, "LoadAssemblyRaw_Prefix", "LoadAssemblyRaw");
                return false;
            }
            if (type.FullName == LocationPatch)
            {
                var target = Type.GetType("System.Reflection.RuntimeAssembly", true)
                    .GetProperty("Location", BindingFlags.Instance | BindingFlags.Public).GetGetMethod();
                return Retarget(__instance, type, "Location", target);
            }
            return true;
        }

        // ModTek shims what its preloader loads through LoadFrom_Prefix because the old LoadFile called LoadFrom. Here LoadFile
        // goes straight to an internal call, so without this the injected Assembly-CSharp keeps its Harmony 1.2 reference, which
        // ModTek's own 0Harmony can't satisfy, and UnityGameInstance.Awake dies.
        static void LoadFilePrefix(ref string path)
        {
            var args = new object[] { path };
            loadFromPrefix.Invoke(null, args);
            path = (string)args[0];
        }

        // Without these, assemblies loaded from bytes go unshimmed: a loss for those mods, not for the run, so it is logged and the run goes on.
        static void PatchAppDomain(Harmony harmony, Type patchClass, string patchName, string targetName)
        {
            try
            {
                MethodInfo patch = patchClass.GetMethod(patchName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo target = typeof(AppDomain).GetMethod(targetName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                harmony.Patch(target, prefix: new HarmonyMethod(patch));
                retargeted++;
                Log("retarget|" + patchClass.Name + "." + patchName + "|prefix|System.AppDomain." + targetName);
            }
            catch (Exception ex)
            {
                Log("retarget|FAILED|" + patchName + "|" + ex);
            }
        }

        // A runtime that still has the old member keeps ModTek's own path; a ModTek whose patch method moved fails here, loudly.
        static bool Retarget(Harmony harmony, Type patchClass, string patchName, MethodInfo target)
        {
            if (target == null)
            {
                Log("keep|" + patchClass.Name);
                return true;
            }
            MethodInfo patch = patchClass.GetMethod(patchName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (patch == null) throw new MissingMethodException(patchClass.FullName, patchName);
            bool postfix = false;
            foreach (object attribute in patch.GetCustomAttributes(false))
                if (attribute.GetType().Name == "HarmonyPostfix") postfix = true;
            var method = new HarmonyMethod(patch);
            harmony.Patch(target, prefix: postfix ? null : method, postfix: postfix ? method : null);
            retargeted++;
            Log("retarget|" + patchClass.Name + "." + patchName + "|" + (postfix ? "postfix" : "prefix") + "|" + target.DeclaringType.FullName + "." + target.Name);
            return false;
        }

        static bool saveAdaptersRegistered;

        // marker: the opt-in marker is present. Each adapter acts only while CombatSavesRuntimeGate.SaveDataEnabled() (the
        // marker, or a co-op combat running). IRTweaks changes its DisableCombatSaves setting only with the marker, and
        // MechEngineerSaveCompat (SaveGameStructure.Save, not on a co-op snapshot's path) is registered only with it.
        // ExtendedCESaveCompat switches BEX:T's save button on only with the marker; its unit-state carry and load dedupe are on a
        // co-op snapshot's path too (logic-bext-fixes finding S), so it is registered either way.
        static void RegisterSaveAdapters(bool marker)
        {
            saveAdaptersRegistered = true;
            IRTweaksSaveCompat.Register(Log);
            CustomUnitsSaveCompat.Register(Log);
            ActorHydrateDeferral.Register(Log);
            MissionControlSaveCompat.Register(Log);
            DesignMaskStickyLoadCompat.Register(Log);  // CustomStatisticEffects: design-mask sticky not applied again on a load (issue 24, CBTBE-3b)
            MechAffinityLoadDedupe.Register(Log);      // MechAffinity: pilot effects not added again on a load (logic-bext-fixes finding S)
            BEXTimelineLoadSkip.Register(Log);         // BEX Timeline: ammo/component conversions not rolled again on a load (logic-bext-fixes layer H)
            EncounterLoadDeferral.Register(Log);
            ExpandedInitiativeSaveCompat.Register(Log);
            CustomAmmoCategoriesSaveCompat.Register(Log);
            CustomAmmoCategoriesLoadModes.Register(Log);   // CustomAmmoCategories weapon modes on a load (issue 24 fault V2)
            CustomActivatableEquipmentAuraLoadCompat.Register(Log);   // CustomActivatableEquipment aura effects on a load (issue 24 fault W, C3)
            StartDialogueSaveCompat.Register(Log);
            if (marker) MechEngineerSaveCompat.Register(Log);
            ExtendedCESaveCompat.Register(Log, marker);   // BEX:T (Extended_CE): save button with the marker; state carry and load dedupe always
        }

        // CombatSavesRuntimeGate.NoteCoopCombat(true), marker absent: the first co-op combat registers the adapters; a later
        // one puts back the MissionControl SerialBox load patch that the end of the previous one took off.
        static void ArmCoopSaveAdapters()
        {
            try
            {
                if (saveAdaptersRegistered)
                {
                    MissionControlSaveCompat.SetBoxLoad(true);
                    return;
                }
                Log("save-adapters|armed|co-op combat, marker absent");
                RegisterSaveAdapters(false);
            }
            catch (Exception ex) { Log("save-adapters|arm|FAILED|" + ex); }
        }

        // NoteCoopCombat(false), marker absent: the MissionControl SerialBox load patch acts on every SerialBox cargo load (a
        // map's own triggers included) and can't step aside once installed, so it comes off when the co-op combat ends. Every
        // other adapter stays installed and idle while SaveDataEnabled() is false.
        static void DisarmCoopSaveAdapters()
        {
            try { MissionControlSaveCompat.SetBoxLoad(false); }
            catch (Exception ex) { Log("save-adapters|disarm|FAILED|" + ex); }
        }

        static void RegisterCoop(string name, Action register)

        {

            try { register(); }

            catch (Exception ex) { Log("coop-adapter|" + name + "|FAILED|" + ex); }

        }


        static void Log(string line)
        {
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
    }
}
