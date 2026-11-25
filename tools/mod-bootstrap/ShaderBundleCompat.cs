using System;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace BattleTechPort
{
    public static class ShaderBundleCompat
    {
        const string AssemblyName = "Assembly-CSharp";
        const string TypeName = "BattleTech.Assetbundles.AssetBundleManager";
        static Action<string> log;
        static string playerRoot;
        static int logged;
        static bool applied;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            playerRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) Consider(assembly);
        }

        static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args) => Consider(args.LoadedAssembly);

        static void Consider(Assembly assembly)
        {
            if (applied || assembly == null) return;
            string name;
            try { name = assembly.GetName().Name; } catch { return; }
            if (name != AssemblyName) return;
            applied = true;
            try { Apply(assembly); }
            catch (Exception ex) { log("shaderbundle|FAILED|" + ex); }
            finally { AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad; }
        }

        static void Apply(Assembly assembly)
        {
            Type manager = assembly.GetType(TypeName, false);
            MethodInfo filepath = manager?.GetMethod("AssetBundleNameToFilepath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(string) }, null);
            MethodInfo fileUrl = manager?.GetMethod("AssetBundleNameToFileURL", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(string) }, null);
            if (filepath == null || fileUrl == null) throw new MissingMethodException(TypeName, "AssetBundleNameToFilepath/AssetBundleNameToFileURL(string)");
            var harmony = new Harmony("battletech.ucu.shaderbundle");
            harmony.Patch(filepath, postfix: new HarmonyMethod(typeof(ShaderBundleCompat).GetMethod(nameof(FilepathPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(fileUrl, postfix: new HarmonyMethod(typeof(ShaderBundleCompat).GetMethod(nameof(FileUrlPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            log("shaderbundle|patched|AssetBundleNameToFilepath+AssetBundleNameToFileURL");
        }

        static void FilepathPostfix(string __0, ref string __result)
        {
            __result = Redirect(__0, __result, false, playerRoot);
        }

        static void FileUrlPostfix(string __0, ref string __result)
        {
            __result = Redirect(__0, __result, true, playerRoot);
        }

        // The offline control harness invokes this same decision by reflection.
        internal static string Redirect(string assetBundleName, string result, bool isUrl, string root)
        {
            if (assetBundleName != "shaders" || string.IsNullOrEmpty(result) || string.IsNullOrEmpty(root)) return result;
            string retailPath = Path.Combine(root, "BattleTech_Data", "StreamingAssets", "data", "assetbundles", "shaders");
            string ucuPath = Path.Combine(root, "BattleTech_Data", "StreamingAssets", "ucu", "bundles", "shaders");
            string actualPath = LocalPath(result, isUrl);
            if (actualPath == null || !SamePath(actualPath, retailPath) || !File.Exists(ucuPath)) return result;
            if (Interlocked.Exchange(ref logged, 1) == 0) log?.Invoke("shaderbundle|redirected|shaders|UCU bundle");
            return isUrl ? "file://" + ucuPath : ucuPath;
        }

        static string LocalPath(string value, bool isUrl)
        {
            if (isUrl)
            {
                const string filePrefix = "file://";
                if (!value.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase)) return null;
                value = value.Substring(filePrefix.Length);
            }
            try { return Path.GetFullPath(value); }
            catch { return null; }
        }

        static bool SamePath(string left, string right)
        {
            try { return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }
}
