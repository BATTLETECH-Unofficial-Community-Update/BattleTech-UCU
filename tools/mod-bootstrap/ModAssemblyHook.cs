// Runs an adapter once, when the mod assembly it adapts is loaded (or at once, when it already is). Shared by the
// per-mod save adapters (ExpandedInitiativeSaveCompat, ActorHydrateDeferral).
using System;
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
