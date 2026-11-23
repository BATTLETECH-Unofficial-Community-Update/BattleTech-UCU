// CustomUnits adapter for combat saves (mod support). CustomUnits replaces every mech in a battle with its
// own classes (CustomMech, TrooperSquad, QuadMech, FakeVehicleMech). They have no save contract of their own; UCU's
// serializer saves them with Mech's and loads them through Serializer.CreateModSubclassInstance, which runs Mech() on an
// object made without a constructor, so the subclass's field initializers and constructor body don't run. Retail's load
// then runs Mech.InitFromSave (MechDef gets its DataManager and chassis back) and Mech.Init, which calls InitStats when
// loading from a save; CustomMech.InitStats builds custLosData there and TrooperSquad.InitStats sets squadStatsInited.
// This supplies only what the missing constructor would have set before that:
//  - after CreateModSubclassInstance: TrooperSquad.location_index = -1 (its field initializer);
//  - after Mech.InitFromSave: TrooperSquad.info = VehicleCustomInfoHelper.GetCustomInfo(MechDef) (its constructor body),
//    which TrooperSquad.InitStats reads.
// One more gap is in the load order itself. Mech.InitFromSave runs AbstractActor.InitFromSave first, whose
// BehaviorTree.InitFromSave evaluates expected armour through the hit table, and gives the MechDef its DataManager and
// chassis only afterwards. Retail's Mech doesn't need the chassis there; CustomMech.GetHitTable reads
// MechDef.Chassis.GetLocationDef and throws, which ends EncounterLayerParent.InitFromSavePassTwo. So before
// Mech.InitFromSave, on a CustomUnits mech whose MechDef has no chassis yet, the MechDef gets the DataManager and
// RefreshChassis: the first two steps of MechDef.InitFromSave, which the game then runs in full as before.
// CustomUnits' postfix on EncounterLayerParent.InitializeContract (CombatGameState_InitializeContract, the player-controlled
// convoy) reads combat.EncounterLayerData, which a load from save sets only in InitFromSavePassTwo, so on a load it throws
// at its first line and does nothing. What it does on a new game (the convoy lance moves to the player's team) happened
// before the save, and the save holds the result; running it again on a load would add the lance to the player's list a
// second time. So on a load with no encounter layer yet it is skipped, with a log line: the same outcome without the error.
// CustomUnits' postfix on MechDef.RefreshChassis gives the MechDef a new melee MechComponentRef every time it runs (its
// MeleeWeaponOverride is never null: it defaults to Weapon_MeleeAttack). A load runs RefreshChassis again inside
// MechDef.InitFromSave, so the MechDef ends up with a fresh melee ref, and RefreshInventory gives that fresh ref its def.
// The mech's saved melee Weapon still holds the saved ref, which nothing refreshes; its def stays null (Def is not
// saved), and the weapon throws from its game representation onwards and at every new round.
// In a new game Mech.Init builds the melee Weapon from the MechDef's ref after the refresh, so the two are the same object,
// and the save stores them as one. So after Mech.InitFromSave, on a CustomUnits mech whose saved melee Weapon no longer
// holds its MechDef's melee ref, the MechDef gets the saved ref back, with the DataManager and RefreshComponentDef that
// MechDef.RefreshInventory gives its melee ref: the battle keeps the melee weapon it was saved with.
// Applied when an assembly named CustomUnits loads; without it nothing is patched and nothing is logged.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomUnitsSaveCompat
    {
        const string AssemblyName = "CustomUnits";
        static Action<string> log;
        static bool applied;
        static Type trooperSquad, customInfoHelper;
        static PropertyInfo mechDef;
        static FieldInfo locationIndex, squadInfo;
        static MethodInfo getCustomInfo, refreshChassis;
        static FieldInfo defDataManager;
        static PropertyInfo chassis, isLoadingFromSave, encounterLayerData;
        static FieldInfo messageCombat;
        static FieldInfo meleeWeapon, meleeWeaponRef;
        static PropertyInfo componentRef, refDataManager, refDef, refDefId;
        static MethodInfo refreshComponentDef;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies()) Consider(loaded);
        }

        static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args) => Consider(args.LoadedAssembly);

        static void Consider(Assembly assembly)
        {
            if (applied || assembly == null) return;
            string name;
            try { name = assembly.GetName().Name; } catch { return; }
            if (name != AssemblyName) return;
            applied = true;
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            try { Apply(assembly); }
            catch (Exception ex) { log("customunits-save|FAILED|" + ex); }
        }

        static Type GameType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = a.GetType(name, false); } catch { continue; }
                if (t != null) return t;
            }
            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Apply(Assembly assembly)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            trooperSquad = assembly.GetType("CustomUnits.TrooperSquad", false);
            customInfoHelper = assembly.GetType("CustomUnits.VehicleCustomInfoHelper", false);
            Type serializer = GameType("BattleTech.Serialization.Serializer");
            Type mech = GameType("BattleTech.Mech");
            MethodInfo create = serializer?.GetMethod("CreateModSubclassInstance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); // private since patch 140 (8b863bb59)
            MethodInfo initFromSave = mech?.GetMethod("InitFromSave", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            mechDef = mech?.GetProperty("MechDef", all);
            if (trooperSquad != null)
            {
                locationIndex = trooperSquad.GetField("location_index", all);
                squadInfo = trooperSquad.GetField("info", all);
            }
            if (customInfoHelper != null && mechDef != null)
                getCustomInfo = customInfoHelper.GetMethod("GetCustomInfo", BindingFlags.Static | BindingFlags.Public, null, new[] { mechDef.PropertyType }, null);
            Type mechDefType = mechDef?.PropertyType;
            defDataManager = mechDefType == null ? null : AccessTools.Field(mechDefType, "dataManager");
            refreshChassis = mechDefType == null ? null : AccessTools.Method(mechDefType, "RefreshChassis", Type.EmptyTypes);
            chassis = mechDefType == null ? null : AccessTools.Property(mechDefType, "Chassis");
            if (trooperSquad == null || create == null || initFromSave == null || locationIndex == null || squadInfo == null || getCustomInfo == null)
            {
                log("customunits-save|skipped|shape changed TrooperSquad=" + (trooperSquad != null) + " CreateModSubclassInstance=" + (create != null)
                    + " Mech.InitFromSave=" + (initFromSave != null) + " location_index=" + (locationIndex != null) + " info=" + (squadInfo != null)
                    + " GetCustomInfo=" + (getCustomInfo != null));
                return;
            }
            if (defDataManager == null || refreshChassis == null || chassis == null)
            {
                log("customunits-save|skipped|shape changed MechDef.dataManager=" + (defDataManager != null) + " RefreshChassis=" + (refreshChassis != null)
                    + " Chassis=" + (chassis != null));
                return;
            }
            meleeWeapon = mech.GetField("MeleeWeapon", all);
            meleeWeaponRef = AccessTools.Field(mechDefType, "meleeWeaponRef");
            Type component = GameType("BattleTech.MechComponent");
            Type baseRef = GameType("BattleTech.BaseComponentRef");
            componentRef = component == null ? null : AccessTools.Property(component, "mechComponentRef");
            refDataManager = baseRef == null ? null : AccessTools.Property(baseRef, "DataManager");
            refDef = baseRef == null ? null : AccessTools.Property(baseRef, "Def");
            refDefId = baseRef == null ? null : AccessTools.Property(baseRef, "ComponentDefID");
            refreshComponentDef = baseRef == null ? null : AccessTools.Method(baseRef, "RefreshComponentDef", Type.EmptyTypes);
            if (meleeWeapon == null || meleeWeaponRef == null || componentRef == null || refDataManager == null || refDef == null || refDefId == null
                || refreshComponentDef == null)
            {
                log("customunits-save|melee|left|shape changed MeleeWeapon=" + (meleeWeapon != null) + " meleeWeaponRef=" + (meleeWeaponRef != null)
                    + " mechComponentRef=" + (componentRef != null) + " RefreshComponentDef=" + (refreshComponentDef != null));
                meleeWeapon = null;
            }
            var harmony = new Harmony("battletech.port.customunits-save");
            MethodInfo convoy = assembly.GetType("CustomUnits.CombatGameState_InitializeContract", false)?.GetMethod("Postfix", BindingFlags.Static | BindingFlags.Public);
            Type combatType = GameType("BattleTech.CombatGameState");
            messageCombat = GameType("BattleTech.InitializeContractMessage")?.GetField("combat", all);
            isLoadingFromSave = combatType?.GetProperty("IsLoadingFromSave", all);
            encounterLayerData = combatType?.GetProperty("EncounterLayerData", all);
            MethodInfo combatInit = combatType == null ? null : AccessTools.Method(combatType, "_Init");
            if (convoy != null && messageCombat != null && isLoadingFromSave != null && encounterLayerData != null && combatInit != null)
            {
                // CustomUnits' own method is patched at the first CombatGameState._Init, after CustomUnits' Init has run:
                // patching a mod's method compiles it, which can run its static constructors before the mod is set up
                convoyMethod = convoy;
                harmony.Patch(combatInit, postfix: new HarmonyMethod(typeof(CustomUnitsSaveCompat).GetMethod(nameof(ArmConvoy), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            else
                log("customunits-save|convoy|left|shape changed Postfix=" + (convoy != null) + " combat=" + (messageCombat != null));
            harmony.Patch(create, postfix: new HarmonyMethod(typeof(CustomUnitsSaveCompat).GetMethod(nameof(CreatePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(initFromSave,
                prefix: new HarmonyMethod(typeof(CustomUnitsSaveCompat).GetMethod(nameof(InitFromSavePrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(CustomUnitsSaveCompat).GetMethod(nameof(InitFromSavePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            log("customunits-save|patched|" + assembly.GetName().Version + "|TrooperSquad location_index after create; chassis before, saved melee ref and TrooperSquad info after Mech.InitFromSave");
        }

        static void CreatePostfix(object __result)
        {
            try
            {
                if (__result != null && trooperSquad.IsInstanceOfType(__result) && CombatSavesRuntimeGate.SaveDataEnabled())
                    locationIndex.SetValue(__result, -1);
            }
            catch (Exception ex) { log("customunits-save|create|FAILED|" + ex); }
        }

        static MethodInfo convoyMethod;
        static bool convoyArmed;

        static void ArmConvoy()
        {
            if (convoyArmed) return;
            convoyArmed = true;
            try
            {
                new Harmony("battletech.port.customunits-save").Patch(convoyMethod,
                    prefix: new HarmonyMethod(typeof(CustomUnitsSaveCompat).GetMethod(nameof(ConvoyPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                log("customunits-save|convoy|patched at the first combat");
            }
            catch (Exception ex) { log("customunits-save|convoy|FAILED|" + ex); }
        }

        static bool ConvoyPrefix(object message)
        {
            try
            {
                object combat = message == null || !messageCombat.DeclaringType.IsInstanceOfType(message) ? null : messageCombat.GetValue(message);
                if (combat == null || !(bool)isLoadingFromSave.GetValue(combat, null) || encounterLayerData.GetValue(combat, null) != null || !CombatSavesRuntimeGate.SaveDataEnabled()) return true;
                log("customunits-save|convoy|skipped on load|CombatGameState_InitializeContract.Postfix: applied before the save, the save holds its result");
                return false;
            }
            catch (Exception ex) { log("customunits-save|convoy|FAILED|" + ex); return true; }
        }

        static void KeepSavedMeleeRef(object mech)
        {
            if (meleeWeapon == null) return;
            try
            {
                object weapon = meleeWeapon.GetValue(mech);
                object def = mechDef.GetValue(mech, null);
                object saved = weapon == null ? null : componentRef.GetValue(weapon, null);
                object fresh = def == null ? null : meleeWeaponRef.GetValue(def);
                if (saved == null || ReferenceEquals(saved, fresh)) return;
                meleeWeaponRef.SetValue(def, saved);
                refDataManager.SetValue(saved, defDataManager.GetValue(def), null);
                refreshComponentDef.Invoke(saved, null);
                string savedId = refDefId.GetValue(saved, null) as string;
                string freshId = fresh == null ? null : refDefId.GetValue(fresh, null) as string;
                log("customunits-save|melee|" + mech.GetType().Name + "|saved ref kept " + savedId + (savedId == freshId ? "" : " (RefreshChassis made " + freshId + ")")
                    + " def=" + (refDef.GetValue(saved, null) != null));
            }
            catch (Exception ex) { log("customunits-save|melee|FAILED|" + mech.GetType().Name + "|" + ex); }
        }

        static void InitFromSavePrefix(object __instance, object dataManager)
        {
            if (__instance == null || dataManager == null || __instance.GetType().Assembly.GetName().Name != AssemblyName || !CombatSavesRuntimeGate.SaveDataEnabled()) return;
            try
            {
                object def = mechDef.GetValue(__instance, null);
                if (def == null || chassis.GetValue(def, null) != null) return;
                defDataManager.SetValue(def, dataManager);
                refreshChassis.Invoke(def, null);
                log("customunits-save|chassis|" + __instance.GetType().Name + "|" + (chassis.GetValue(def, null) != null));
            }
            catch (Exception ex) { log("customunits-save|chassis|FAILED|" + __instance.GetType().Name + "|" + ex); }
        }

        static void InitFromSavePostfix(object __instance)
        {
            if (__instance == null || __instance.GetType().Assembly.GetName().Name != AssemblyName || !CombatSavesRuntimeGate.SaveDataEnabled()) return;
            KeepSavedMeleeRef(__instance);
            if (!trooperSquad.IsInstanceOfType(__instance)) return;
            try
            {
                if (squadInfo.GetValue(__instance) != null) return;
                object def = mechDef.GetValue(__instance, null);
                object info = def == null ? null : getCustomInfo.Invoke(null, new[] { def });
                squadInfo.SetValue(__instance, info);
                log("customunits-save|info|" + __instance.GetType().Name + "|" + (info != null));
            }
            catch (Exception ex) { log("customunits-save|info|FAILED|" + __instance.GetType().Name + "|" + ex); }
        }
    }
}
