using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BattleTechPort
{
    // MechEngineer treats the shared definitions as stock only while their GUIDs are null.
    // Keep generated IDs available to HBS through both save serialization passes, then
    // restore the shared objects before the next definitions-load callback can see them.
    internal static class MechEngineerSaveCompat
    {
        const string Tag = "mechengineer-save";
        static PropertyInfo combat, dataManager, mechDefs, pairValue, guid;
        static MethodInfo setGuid;

        [ThreadStatic] static SaveScope active;

        sealed class SaveScope
        {
            internal readonly SaveScope Parent = active;
            internal readonly Dictionary<object, string> Shared = new Dictionary<object, string>(new ReferenceComparer());
            internal readonly List<Assignment> Assigned = new List<Assignment>();
        }

        struct Assignment
        {
            internal object Def;
            internal string Before;
            internal string Generated;
        }

        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }

        internal static void Register(Action<string> log)
        {
            ModAssemblyHook.When("MechEngineer", _ =>
            {
                Install();
                log(Tag + "|patched|restore shared MechDef GUIDs after combat save serialization");
            }, log, Tag);
        }

        static void Install()
        {
            Type game = ModAssemblyHook.GameType("BattleTech.GameInstance");
            Type structure = ModAssemblyHook.GameType("BattleTech.Save.SaveGameStructure.SaveGameStructure");
            Type container = ModAssemblyHook.GameType("BattleTech.Save.Test.SerializableReferenceContainer");
            Type mechDef = ModAssemblyHook.GameType("BattleTech.MechDef");
            Type saveReason = ModAssemblyHook.GameType("BattleTech.Save.SaveGameStructure.SaveReason");
            combat = game?.GetProperty("Combat");
            dataManager = game?.GetProperty("DataManager");
            mechDefs = dataManager?.PropertyType.GetProperty("MechDefs");
            pairValue = typeof(KeyValuePair<,>).MakeGenericType(typeof(string), mechDef).GetProperty("Value");
            guid = mechDef.GetProperty("GUID");
            setGuid = mechDef.GetMethod("SetGuid", new[] { typeof(string) });
            MethodInfo save = AccessTools.Method(structure, "Save", new[] { game, saveReason, typeof(string) });
            MethodInfo allocate = AccessTools.Method(container, "GetGUID");
            if (save == null || allocate == null || combat == null || mechDefs == null || guid == null || setGuid == null)
                throw new MissingMemberException("Combat save GUID tracking requires the HBS save and MechDef contracts.");

            var harmony = new Harmony("battletech.port." + Tag);
            var adapter = typeof(MechEngineerSaveCompat);
            harmony.Patch(allocate,
                prefix: new HarmonyMethod(adapter, nameof(BeforeGetGuid)) { priority = Priority.Last },
                postfix: new HarmonyMethod(adapter, nameof(AfterGetGuid)) { priority = Priority.First });
            harmony.Patch(save,
                prefix: new HarmonyMethod(adapter, nameof(BeforeSave)),
                finalizer: new HarmonyMethod(adapter, nameof(AfterSave)));
        }

        static void BeforeSave(object __0, ref SaveScope __state)
        {
            if (combat.GetValue(__0, null) == null) return;

            var scope = new SaveScope();
            var defs = (IEnumerable)mechDefs.GetValue(dataManager.GetValue(__0, null), null);
            foreach (object pair in defs)
            {
                object def = pairValue.GetValue(pair, null);
                if (def == null) continue;
                string original = (string)guid.GetValue(def, null);
                if (string.IsNullOrEmpty(original)) scope.Shared[def] = original;
            }
            __state = scope;
            active = scope;
        }

        static void BeforeGetGuid(object __0, bool __1, out Assignment __state)
        {
            __state = default;
            if (active == null || !__1 || __0 == null || !active.Shared.TryGetValue(__0, out string original)) return;
            if ((string)guid.GetValue(__0, null) == original)
                __state = new Assignment { Def = __0, Before = original };
        }

        static void AfterGetGuid(string __result, Assignment __state)
        {
            if (__state.Def == null || string.IsNullOrEmpty(__result)) return;
            __state.Generated = __result;
            active.Assigned.Add(__state);
        }

        static Exception AfterSave(Exception __exception, SaveScope __state)
        {
            if (__state == null) return __exception;

            active = __state.Parent;
            foreach (Assignment assignment in __state.Assigned)
            {
                try
                {
                    if ((string)guid.GetValue(assignment.Def, null) == assignment.Generated)
                        setGuid.Invoke(assignment.Def, new object[] { assignment.Before });
                }
                catch (Exception error)
                {
                    __exception = __exception == null ? error : new AggregateException(__exception, error);
                }
            }
            return __exception;
        }
    }
}
