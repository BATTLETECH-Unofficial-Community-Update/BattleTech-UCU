// MissionControl adapter for combat saves (mod support). MissionControl builds a hidden pathfinder
// Mech for its own path checks (GUID "<guid>.9999999999", or ".9999999998"; MissionControl itself recognises its
// pathfinders by those endings). It belongs to no team and is not a registered combatant, but building it creates real
// effects on it (MechEngineer's EngineCoreEffect_* and component PassiveEffect_*). A combat save writes every effect in
// EffectManager's two lists and not the Mech, so on load CombatGameState.FindCombatantByGUID finds no target and
// StatisticEffect.GetProperStatCollectionOnLoad throws a NullReferenceException: hydration fails and the save returns
// to the main menu, on retail as on UCU.
// While EffectManager.Dehydrate runs, and only when an effect's target is a MissionControl pathfinder that the combat
// doesn't know as a combatant, the save sees copies of the two lists without those effects; a finalizer puts the live
// lists back, so the battle in progress keeps every effect. Each effect left out is logged. Any other effect is saved as
// before. Applied only when an assembly named MissionControl loads; without it nothing is patched and nothing is logged.
using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class MissionControlSaveCompat
    {
        static Action<string> log;
        static FieldInfo effectsField, expiringField, targetField, targetIdField, effectIdField;
        static PropertyInfo combatProp, guidProp;
        static MethodInfo findCombatant;
        static Type actorType;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("MissionControl", Apply, logLine, "missioncontrol-save");
        }

        static void Apply(Assembly missionControl)
        {
            Assembly game = ModAssemblyHook.GameType("BattleTech.EffectManager")?.Assembly;
            if (game == null) { log("missioncontrol-save|skipped|Assembly-CSharp not loaded"); return; }
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type manager = game.GetType("BattleTech.EffectManager", false);
            Type effect = game.GetType("BattleTech.Effect", false);
            Type combat = game.GetType("BattleTech.CombatGameState", false);
            actorType = game.GetType("BattleTech.AbstractActor", false);
            MethodInfo dehydrate = manager?.GetMethod("Dehydrate", inst | BindingFlags.DeclaredOnly);
            effectsField = manager?.GetField("effects", inst);
            expiringField = manager?.GetField("expiringEffects", inst);
            combatProp = manager?.GetProperty("Combat", inst);
            targetField = effect?.GetField("target", inst);
            targetIdField = effect?.GetField("targetID", inst);
            effectIdField = effect?.GetField("id", inst);
            guidProp = actorType?.GetProperty("GUID", inst);
            findCombatant = combat?.GetMethod("FindCombatantByGUID", inst, null, new[] { typeof(string), typeof(bool) }, null);
            if (dehydrate == null || effectsField == null || expiringField == null || combatProp == null || targetField == null
                || guidProp == null || findCombatant == null)
            {
                log("missioncontrol-save|skipped|shape changed Dehydrate=" + (dehydrate != null) + " effects=" + (effectsField != null)
                    + " expiringEffects=" + (expiringField != null) + " target=" + (targetField != null) + " FindCombatantByGUID=" + (findCombatant != null));
                return;
            }
            new Harmony("battletech.port.missioncontrol-save").Patch(dehydrate,
                prefix: new HarmonyMethod(typeof(MissionControlSaveCompat).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)),
                finalizer: new HarmonyMethod(typeof(MissionControlSaveCompat).GetMethod(nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic)));
            log("missioncontrol-save|patched|" + missionControl.GetName().Version + "|EffectManager.Dehydrate leaves out effects on MissionControl pathfinders");
            ApplyContractOnLoad(missionControl, game);
            LoadEncounterRecord(missionControl);
        }

        // Part 3, the contract on a load. MissionControl sets its per-contract state in
        // MissionControl.SetContract, which only its Contract.BeginRequestResources prefix calls: a contract start. Part of
        // that state is CurrentContract and a backup of the contract's lance and objective lists, which
        // MissionControl.OnCombatDestroyed (a postfix on CombatGameState.OnCombatGameDestroyed) puts back when the battle
        // is cleared. A load never starts a contract, so after it CurrentContract is the previous battle's contract (or
        // null) and the backup is empty: the next clear throws KeyNotFoundException in RestoreContractOverriddeData, which
        // leaves GameInstance.ClearCombat unfinished and CombatGameState.Update throwing every frame. So on a load, after
        // EncounterLayerParent.InitializeContract (MissionControl's own prefix on it has run), CurrentContract becomes the
        // loaded battle's contract and MissionControl's BackupContractOverriddeData backs up its lists as loaded. When the
        // battle is cleared, the contract keeps the lists it was loaded with. The rest of SetContract (encounter rules,
        // additional lances, map and contract type) is not run again: it chooses the encounter, which the save holds.
        static Type mcType, mcDataType;
        static PropertyInfo mcInstance, mcCurrentContract, mcDataInstance, activeContract, isLoadingFromSave;
        static MethodInfo mcBackup;
        static FieldInfo messageCombat;

        static void ApplyContractOnLoad(Assembly missionControl, Assembly game)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            mcType = missionControl.GetType("MissionControl.MissionControl", false);
            mcDataType = missionControl.GetType("MissionControl.DataManager", false);
            mcInstance = mcType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
            mcCurrentContract = mcType?.GetProperty("CurrentContract", all);
            mcDataInstance = mcDataType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
            mcBackup = mcDataType?.GetMethod("BackupContractOverriddeData", all, null, Type.EmptyTypes, null);
            Type combat = game.GetType("BattleTech.CombatGameState", false);
            activeContract = combat?.GetProperty("ActiveContract", all);
            isLoadingFromSave = combat?.GetProperty("IsLoadingFromSave", all);
            messageCombat = game.GetType("BattleTech.InitializeContractMessage", false)?.GetField("combat", all);
            Type layerParent = game.GetType("BattleTech.EncounterLayerParent", false);
            MethodInfo initializeContract = layerParent == null ? null : AccessTools.Method(layerParent, "InitializeContract");
            if (mcInstance == null || mcCurrentContract == null || mcCurrentContract.GetSetMethod(true) == null || mcDataInstance == null || mcBackup == null
                || activeContract == null || isLoadingFromSave == null || messageCombat == null || initializeContract == null)
            {
                log("missioncontrol-save|contract|left|shape changed MissionControl.Instance=" + (mcInstance != null) + " CurrentContract=" + (mcCurrentContract != null)
                    + " BackupContractOverriddeData=" + (mcBackup != null) + " InitializeContract=" + (initializeContract != null));
                return;
            }
            new Harmony("battletech.port.missioncontrol-save").Patch(initializeContract,
                postfix: new HarmonyMethod(typeof(MissionControlSaveCompat).GetMethod(nameof(InitializeContractPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            log("missioncontrol-save|contract|patched|on a load, CurrentContract and its list backup after EncounterLayerParent.InitializeContract");
        }

        static void InitializeContractPostfix(object message)
        {
            try
            {
                object combat = message == null || !messageCombat.DeclaringType.IsInstanceOfType(message) ? null : messageCombat.GetValue(message);
                if (combat == null || !(bool)isLoadingFromSave.GetValue(combat, null)) return;
                object contract = activeContract.GetValue(combat, null);
                if (contract == null) { log("missioncontrol-save|contract|skipped on load|no ActiveContract"); return; }
                object mc = mcInstance.GetValue(null, null);
                object previous = mcCurrentContract.GetValue(mc, null);
                mcCurrentContract.SetValue(mc, contract, null);
                mcBackup.Invoke(mcDataInstance.GetValue(null, null), null);
                log("missioncontrol-save|contract|load|CurrentContract set to the loaded contract (" + (previous == null ? "was null" : ReferenceEquals(previous, contract) ? "already it" : "was another contract")
                    + "), its lists backed up");
            }
            catch (Exception ex) { log("missioncontrol-save|contract|FAILED|" + (ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex)); }
        }

        // Part 2, the encounter record, references the game, so it is its own assembly (MissionControlEncounter/), embedded
        // in this DLL as a resource and loaded from bytes here: this one must reference mscorlib only (ModBootstrap.cs, note 1).
        static void LoadEncounterRecord(Assembly missionControl)
        {
            const string name = "BattleTechPortBootstrap.MissionControl.dll";
            try
            {
                byte[] bytes;
                using (var s = typeof(MissionControlSaveCompat).Assembly.GetManifestResourceStream(name))
                {
                    if (s == null) { log("missioncontrol-encounter|skipped|" + name + " not embedded"); return; }
                    bytes = new byte[s.Length];
                    int read = 0;
                    while (read < bytes.Length) { int n = s.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
                }
                Type entry = Assembly.Load(bytes).GetType("BattleTechPort.MissionControlEncounter.Entry", true);
                entry.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { log, missionControl });
            }
            catch (Exception ex) { log("missioncontrol-encounter|FAILED|" + (ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex)); }
        }

        static bool Orphan(object combat, object e, out string why)
        {
            why = null;
            object target = targetField.GetValue(e);
            if (target == null || !actorType.IsInstanceOfType(target)) return false;
            string guid = (string)guidProp.GetValue(target, null);
            if (guid == null || !(guid.EndsWith(".9999999999") || guid.EndsWith(".9999999998"))) return false;
            object found = findCombatant.Invoke(combat, new object[] { guid, false });
            if (ReferenceEquals(found, target)) return false;
            why = (effectIdField?.GetValue(e) ?? "?") + " on " + target.GetType().Name + " " + guid + (found == null ? " (MissionControl pathfinder, not a combatant)" : " (MissionControl pathfinder GUID names another combatant)");
            return true;
        }

        static IList Filtered(object combat, IList list, ref int dropped)
        {
            if (list == null) return null;
            IList copy = null;
            for (int i = 0; i < list.Count; i++)
            {
                if (Orphan(combat, list[i], out string why))
                {
                    if (copy == null)
                    {
                        copy = (IList)Activator.CreateInstance(list.GetType());
                        for (int j = 0; j < i; j++) copy.Add(list[j]);
                    }
                    dropped++;
                    log("missioncontrol-save|left out of the save|" + why);
                }
                else if (copy != null) copy.Add(list[i]);
            }
            return copy ?? list;
        }

        static void Prefix(object __instance, out object[] __state)
        {
            __state = null;
            try
            {
                object combat = combatProp.GetValue(__instance, null);
                if (combat == null) return;
                var effects = (IList)effectsField.GetValue(__instance);
                var expiring = (IList)expiringField.GetValue(__instance);
                int dropped = 0;
                IList e2 = Filtered(combat, effects, ref dropped), x2 = Filtered(combat, expiring, ref dropped);
                if (dropped == 0) return;
                __state = new object[] { effects, expiring };
                effectsField.SetValue(__instance, e2);
                expiringField.SetValue(__instance, x2);
            }
            catch (Exception ex) { log("missioncontrol-save|prefix|FAILED|" + ex); }
        }

        static void Finalizer(object __instance, object[] __state)
        {
            if (__state == null) return;
            try
            {
                effectsField.SetValue(__instance, __state[0]);
                expiringField.SetValue(__instance, __state[1]);
            }
            catch (Exception ex) { log("missioncontrol-save|restore|FAILED|" + ex); }
        }
    }
}
