// Runs an adapter once, when the mod assembly it adapts is loaded (or at once, when it already is). Shared by the
// per-mod save adapters (ExpandedInitiativeSaveCompat, ActorHydrateDeferral).
using System;
using System.Collections.Generic;
using System.Reflection;

namespace BattleTechPort
{
    public static class ModAssemblyHook
    {
        public static void When(string assemblyName, Action<Assembly> apply, Action<string> log, string tag)
        {
            bool done = false;
            AssemblyLoadEventHandler handler = null;
            Action<Assembly> consider = a =>
            {
                if (done || a == null) return;
                string name;
                try { name = a.GetName().Name; } catch { return; }
                if (name != assemblyName) return;
                done = true;
                AppDomain.CurrentDomain.AssemblyLoad -= handler;
                try { apply(a); }
                catch (Exception ex) { log(tag + "|FAILED|" + ex); }
            };
            handler = (s, e) => consider(e.LoadedAssembly);
            AppDomain.CurrentDomain.AssemblyLoad += handler;
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies()) consider(loaded);
        }

        // Runs install once, at the start of the first combat (CombatGameState.FirstTimeInit for a new mission, _InitFromSave for a
        // loaded one, a co-op rejoin or resync included), or at once when a combat has already started. For an adapter that patches a
        // mod's own methods: patching one when its assembly loads compiles it before ModTek has called the mod's Init, and Mono runs a
        // type initializer the method touches at that compile (gh issue 24 fault M: IRTweaks.ModState..cctor threw, IRTweaks' Init
        // failed and every mod that depends on it was skipped).
        public static void AtFirstCombat(Action install, Action<string> log, string tag)
        {
            lock (firstCombatPending)
            {
                if (firstCombatSeen) { Run(install, log, tag); return; }
                firstCombatPending.Add(new KeyValuePair<Action, string>(install, tag));
                if (firstCombatHooked) return;
                firstCombatHooked = true;
                firstCombatLog = log;
            }
            Type combat = GameType("BattleTech.CombatGameState");
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var prefix = new HarmonyLib.HarmonyMethod(typeof(ModAssemblyHook).GetMethod(nameof(FirstCombatPrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = HarmonyLib.Priority.First };
            var harmony = new HarmonyLib.Harmony("battletech.port.first-combat");
            foreach (string name in new[] { "FirstTimeInit", "_InitFromSave" })
            {
                MethodInfo m = combat?.GetMethod(name, all);
                if (m == null) { log("first-combat|skipped|CombatGameState." + name + " not found"); continue; }
                harmony.Patch(m, prefix: prefix);
            }
        }

        // Runs apply once, at the first mod assembly load after Assembly-CSharp's types exist (a callback registration only; the
        // handler does no work until a mod assembly loads). Used for a game-point scope that must exist before first combat
        // (CoopRngSweepDeterminism's FirstTimeInit, the timing HEAD FieldRepairs used); everything else runs at AtFirstCombat.
        public static void WhenAnyMod(Action<Assembly> apply, Action<string> log, string tag)
        {
            bool done = false;
            AssemblyLoadEventHandler handler = null;
            handler = (s, e) =>
            {
                if (done) return;
                if (GameType("BattleTech.CombatGameState") == null) return;
                Assembly a = e.LoadedAssembly;
                if (a == null) return;
                string location;
                try { location = a.Location; } catch { return; }
                if (string.IsNullOrEmpty(location) || location.IndexOf("\\Mods\\", StringComparison.OrdinalIgnoreCase) < 0) return;
                string full;
                try { full = System.IO.Path.GetFullPath(location); } catch { return; }
                if (full.IndexOf("\\Mods\\ModTek\\", StringComparison.OrdinalIgnoreCase) >= 0) return;
                done = true;
                AppDomain.CurrentDomain.AssemblyLoad -= handler;
                try { apply(a); }
                catch (Exception ex) { log(tag + "|FAILED|" + ex); }
            };
            AppDomain.CurrentDomain.AssemblyLoad += handler;
            if (GameType("BattleTech.CombatGameState") != null)
                foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string location;
                    try { location = loaded.Location; } catch { continue; }
                    if (string.IsNullOrEmpty(location) || location.IndexOf("\\Mods\\", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (location.IndexOf("\\Mods\\ModTek\\", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    done = true;
                    AppDomain.CurrentDomain.AssemblyLoad -= handler;
                    try { apply(loaded); } catch (Exception ex) { log(tag + "|FAILED|" + ex); }
                    break;
                }
        }

        static readonly List<KeyValuePair<Action, string>> firstCombatPending = new List<KeyValuePair<Action, string>>();
        static bool firstCombatSeen, firstCombatHooked;
        static Action<string> firstCombatLog;

        static void FirstCombatPrefix()
        {
            List<KeyValuePair<Action, string>> run;
            lock (firstCombatPending)
            {
                if (firstCombatSeen) return;
                firstCombatSeen = true;
                run = new List<KeyValuePair<Action, string>>(firstCombatPending);
                firstCombatPending.Clear();
            }
            foreach (KeyValuePair<Action, string> entry in run) Run(entry.Key, firstCombatLog, entry.Value);
        }

        static void Run(Action install, Action<string> log, string tag)
        {
            try { install(); }
            catch (Exception ex) { log(tag + "|FAILED|" + ex); }
        }

        public static Type GameType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = a.GetType(name, false); } catch { continue; }
                if (t != null) return t;
            }
            return null;
        }
    }
}
