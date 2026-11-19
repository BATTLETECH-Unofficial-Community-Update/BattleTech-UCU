// Start-dialogue adapter for combat saves (mod support).
// SoldiersPiratesAssassinsMercs (SPAM) and EnvironmentalDesignMasks (EDM) each prefix TurnDirector.QueuePilotChatter,
// which runs when a battle begins, and there build a one-shot dialogue: a DialogueChunkGameLogic
// ("Chunk_Dialogue_SPAM_<faction>", "Chunk_Dialog_<mood>_startMission") under the encounter layer, with a
// DialogueGameLogic child, a GUID each, and TriggerDialogue. Neither runs AlwaysInit on them, and AlwaysInit is the only
// place an encounter object's MessageMemory gets its owner (the game runs it once, in EncounterLayerParent.InitializeContract).
// EncounterLayerData.BeforeSave walks every active encounter object and calls MessageMemory.PrepareForSave, which throws
// "Owner GUID is null or empty!" on these, so the whole combat save fails. A load would not rebuild them either (they
// exist only after QueuePilotChatter, which a loaded battle does not run again), so a save that held them would log
// "extra guids" and "Missing GUID" on every load.
// This leaves them out of the save: while EncounterLayerData.SaveGameSerialize runs, each such chunk's GameObject is
// inactive, so BeforeSave and the serializer do not see it; it is active again afterwards (finalizer, also on a throw).
// They have no OnEnable or OnDisable. (EDM's Prefix is private, SPAM's public: the shape check takes both.) A chunk counts only if its mod's assembly is loaded, its name has that mod's
// pattern, and its MessageMemory has no owner (so a chunk the game initialised, a map's own or MissionControl's, is
// never touched). Applied per mod when its assembly loads; skipped with one log line if a shape it relies on changed.
// The combat's ItemRegistry also lists their DialogueGameLogic GUIDs, and its PreSerialization writes every key into the
// save's debugItemsByGuidKeys; ItemRegistry.InitFromSave then logs "itemsByGuid expected count N after Rehydrate" and
// "itemsByGuid Missing KEY <guid> of type DialogueGameLogic" on the load. A postfix on
// ItemRegistry.PreSerialization drops the keys of objects inside such a chunk from those two debug lists, so the saved
// lists match what the load rebuilds. The registry itself is not changed.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BattleTechPort
{
    public static class StartDialogueSaveCompat
    {
        sealed class ModRule
        {
            public string Assembly, Tag, PatchType, Prefix, Suffix;
            public bool Active;
        }

        static readonly ModRule[] rules =
        {
            new ModRule { Assembly = "SoldiersPiratesAssassinsMercs", Tag = "spam-save",
                PatchType = "SoldiersPiratesAssassinsMercs.Patches.Dialogue+TurnDirector_QueuePilotChatter", Prefix = "Chunk_Dialogue_SPAM_", Suffix = "" },
            new ModRule { Assembly = "EnvironmentalDesignMasks", Tag = "edm-save",
                PatchType = "EnvironmentalDesignMasks.TurnDirector_QueuePilotChatter", Prefix = "Chunk_Dialog_", Suffix = "_startMission" },
        };

        static Action<string> log;
        static bool patched;
        static Type chunkType;
        static FieldInfo messageMemory, ownerId;
        static MethodInfo getComponentsInChildren, setActive, getComponentInParent;
        static FieldInfo regKeys, regTypes, regItems;
        static PropertyInfo gameObject, objectName;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            foreach (ModRule r in rules)
            {
                ModRule rule = r;
                ModAssemblyHook.When(rule.Assembly, a => Apply(rule, a), logLine, rule.Tag);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Apply(ModRule rule, Assembly assembly)
        {
            if (rule.Active) return;
            Type modPatch = assembly.GetType(rule.PatchType, false);
            if (modPatch == null || modPatch.GetMethod("Prefix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) == null)
            {
                log(rule.Tag + "|skipped|shape changed: " + rule.PatchType + ".Prefix not found");
                return;
            }
            if (!patched && !PatchSave(rule.Tag)) return;
            rule.Active = true;
            log(rule.Tag + "|patched|" + assembly.GetName().Version + "|its start dialogue chunk (" + rule.Prefix + "*" + rule.Suffix
                + ", no AlwaysInit) is left out of combat saves");
        }

        static bool PatchSave(string tag)
        {
            Type layer = ModAssemblyHook.GameType("BattleTech.EncounterLayerData");
            Type logic = ModAssemblyHook.GameType("BattleTech.EncounterObjectGameLogic");
            chunkType = ModAssemblyHook.GameType("BattleTech.Designed.DialogueChunkGameLogic");
            Type component = ModAssemblyHook.GameType("UnityEngine.Component");
            Type go = ModAssemblyHook.GameType("UnityEngine.GameObject");
            Type obj = ModAssemblyHook.GameType("UnityEngine.Object");
            MethodInfo save = layer?.GetMethod("SaveGameSerialize", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            messageMemory = logic?.GetField("messageMemory", BindingFlags.Instance | BindingFlags.NonPublic);
            ownerId = messageMemory?.FieldType.GetField("ownerID", BindingFlags.Instance | BindingFlags.NonPublic);
            getComponentsInChildren = component?.GetMethod("GetComponentsInChildren", new[] { typeof(Type), typeof(bool) });
            gameObject = component?.GetProperty("gameObject");
            setActive = go?.GetMethod("SetActive", new[] { typeof(bool) });
            objectName = obj?.GetProperty("name");
            if (save == null || chunkType == null || ownerId == null || getComponentsInChildren == null || gameObject == null || setActive == null || objectName == null)
            {
                log(tag + "|skipped|shape changed SaveGameSerialize=" + (save != null) + " DialogueChunkGameLogic=" + (chunkType != null)
                    + " MessageMemory.ownerID=" + (ownerId != null) + " Unity=" + (getComponentsInChildren != null && setActive != null));
                return false;
            }
            new Harmony("battletech.port.start-dialogue-save").Patch(save,
                prefix: new HarmonyMethod(typeof(StartDialogueSaveCompat).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)),
                finalizer: new HarmonyMethod(typeof(StartDialogueSaveCompat).GetMethod(nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic)));
            patched = true;
            PatchRegistry(tag);
            return true;
        }

        // Fault 22: the ItemRegistry debug key lists. Optional: if its shape changed, the save half still applies.
        static void PatchRegistry(string tag)
        {
            Type registry = chunkType.Assembly.GetType("ItemRegistry", false); // the game's, a global-namespace type
            Type component = ModAssemblyHook.GameType("UnityEngine.Component");
            MethodInfo pre = registry?.GetMethod("PreSerialization", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            regKeys = registry?.GetField("debugItemsByGuidKeys", BindingFlags.Instance | BindingFlags.NonPublic);
            regTypes = registry?.GetField("debugItemsByGuidValueTypes", BindingFlags.Instance | BindingFlags.NonPublic);
            regItems = registry?.GetField("itemsByGuid", BindingFlags.Instance | BindingFlags.NonPublic);
            getComponentInParent = component?.GetMethod("GetComponentInParent", new[] { typeof(Type), typeof(bool) });
            if (pre == null || regKeys == null || regTypes == null || regItems == null || getComponentInParent == null)
            {
                log(tag + "|skipped registry keys|shape changed ItemRegistry.PreSerialization=" + (pre != null) + " debug lists=" + (regKeys != null && regTypes != null)
                    + " itemsByGuid=" + (regItems != null) + " GetComponentInParent=" + (getComponentInParent != null));
                return;
            }
            new Harmony("battletech.port.start-dialogue-save").Patch(pre,
                postfix: new HarmonyMethod(typeof(StartDialogueSaveCompat).GetMethod(nameof(RegistryPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        // The chunk a left-out object sits in (itself or a parent), when that chunk is one this adapter leaves out.
        static ModRule LeftOutChunk(object component, out string chunkName)
        {
            chunkName = null;
            object chunk = getComponentInParent.Invoke(component, new object[] { chunkType, true });
            if (chunk == null) return null;
            chunkName = objectName.GetValue(chunk, null) as string ?? "";
            ModRule rule = RuleFor(chunkName);
            if (rule == null) return null;
            object memory = messageMemory.GetValue(chunk);
            return memory != null && !string.IsNullOrEmpty(ownerId.GetValue(memory) as string) ? null : rule;
        }

        static void RegistryPostfix(object __instance)
        {
            try
            {
                var keys = regKeys.GetValue(__instance) as List<string>;
                var types = regTypes.GetValue(__instance) as List<string>;
                var items = regItems.GetValue(__instance) as System.Collections.IDictionary;
                if (keys == null || items == null) return;
                Type component = getComponentInParent.DeclaringType;
                for (int i = keys.Count - 1; i >= 0; i--)
                {
                    object item = items.Contains(keys[i]) ? items[keys[i]] : null;
                    if (item == null || !component.IsInstanceOfType(item)) continue;
                    ModRule rule = LeftOutChunk(item, out string chunkName);
                    if (rule == null) continue;
                    log(rule.Tag + "|left out of the item registry keys|" + keys[i] + " " + item.GetType().Name + " in " + chunkName);
                    keys.RemoveAt(i);
                    if (types != null && i < types.Count) types.RemoveAt(i);
                }
            }
            catch (Exception ex) { log("start-dialogue-save|FAILED|registry keys " + ex); }
        }

        static ModRule RuleFor(string name)
        {
            foreach (ModRule r in rules)
                if (r.Active && name.StartsWith(r.Prefix, StringComparison.Ordinal) && name.EndsWith(r.Suffix, StringComparison.Ordinal)) return r;
            return null;
        }

        static void Prefix(object __instance, out List<object> __state)
        {
            __state = null;
            try
            {
                foreach (object chunk in (Array)getComponentsInChildren.Invoke(__instance, new object[] { chunkType, false }))
                {
                    string name = objectName.GetValue(chunk, null) as string ?? "";
                    ModRule rule = RuleFor(name);
                    if (rule == null) continue;
                    object memory = messageMemory.GetValue(chunk);
                    if (memory != null && !string.IsNullOrEmpty(ownerId.GetValue(memory) as string)) continue;
                    object go = gameObject.GetValue(chunk, null);
                    setActive.Invoke(go, new object[] { false });
                    (__state ?? (__state = new List<object>())).Add(go);
                    log(rule.Tag + "|left out of the save|" + name + " (start dialogue built at QueuePilotChatter, no AlwaysInit)");
                }
            }
            catch (Exception ex) { log("start-dialogue-save|FAILED|" + ex); }
        }

        static Exception Finalizer(Exception __exception, List<object> __state)
        {
            if (__state != null)
                foreach (object go in __state)
                {
                    try { setActive.Invoke(go, new object[] { true }); }
                    catch (Exception ex) { log("start-dialogue-save|FAILED|reactivate " + ex); }
                }
            return __exception;
        }
    }
}
