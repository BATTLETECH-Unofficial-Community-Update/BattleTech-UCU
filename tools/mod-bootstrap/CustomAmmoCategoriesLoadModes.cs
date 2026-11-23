// CustomAmmoCategories weapon modes and ammo after a combat load (mod support, gh issue 24 fault V2;
// .hermes/reports/issue24-faultV-read-1.md Q2). CustomAmmoCategories keeps a weapon's modes in a WeaponExtendedInfo that it
// registers only from its Weapon constructor postfixes. A load deserializes weapons without that constructor (Mech.Init's load
// branch), so nothing is registered when AbstractActor.Init calls AssignAmmoToWeapons: CustomActivatableEquipment's prefix there
// adds a weapon addon's modes (Artemis IV) through WeaponStatCacheHelper.info(), which builds an unregistered instance on a miss,
// and so does CustomAmmoCategories' own postfix (isBoxesAssigned). The instance that is registered later (mode(), exDef(), ammo())
// sets the "CAC-WeaponMode" stat to the weapon's base mode in its constructor and has no addon modes. So after every load a weapon
// is in its base mode: the co-op guest's Artemis LRMs came back BASE from each resync snapshot (A-on-h19, compares 102 and 202 on
// the same host hash), and an AMS switched Off came back On (H-fix-h3).
// This adapter, only when an assembly named CustomAmmoCategories is loaded, on a combat loading from a save while combat saves are
// on (CombatSavesRuntimeGate.SaveDataEnabled: the marker or a co-op combat), runs a prefix on AbstractActor.AssignAmmoToWeapons
// ahead of every other one that records each weapon's saved mode and ammo (CustomAmmoCategories' getCurrentAmmoMode) and registers
// its WeaponExtendedInfo (exDef), so the addon modes and isBoxesAssigned land on the instance CustomAmmoCategories keeps. A postfix
// after every other one then puts back, with CustomAmmoCategories' own ApplyAmmoMode, each saved mode and ammo the weapon no longer
// has, when the saved mode is one of its modes and not restricted. No statistic is written here. Single player without the marker
// and a new mission are untouched.
// Addon links (fault V2 gap 1 and 2, .hermes/reports/issue24-faultV-fix-1.md "Fix 2"): CustomActivatableEquipment adds an addon's
// modes (Artemis IV on LRMs, the Gyrojet ammo feed's "Ammo") only to a weapon whose BaseComponentRef.LocalGUID equals an
// attachment's TargetComponentGUID. Those two fields are ModTek-injected and are not in a combat save's component refs (V2a refs-2.bin
// holds them only inside MechDef JSON); a ref with a SimGameUID in CustomActivatableEquipment's own registry gets them back from
// it (a player's mechlab-built mech), a unit from a data mechdef (BTA writes "target_LRM_1" pairs into it) does not. So, with
// CustomActivatableEquipment loaded, the prefix first copies each empty LocalGUID / TargetComponentGUID of the unit's component
// refs from the same ref of its data def (DataManager by PilotableActorDef id; matched by ComponentDefID, location and order),
// through CustomActivatableEquipment's own setters without a registry write: the links a new mission takes from that def.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomAmmoCategoriesLoadModes
    {
        const string OwnId = "battletech.port.cac-load-modes";
        const string Tag = "cac-load-modes";
        static Action<string> log;
        static bool patched;
        static MethodInfo getCurrent, applyPair, exDef, info;
        static PropertyInfo modeIdProp, ammoIdProp, modesProp, restrictedProp, combatProp, loadingProp;
        static ConstructorInfo pairCtor;
        static int rows;
        static Assembly cae;                                      // CustomActivatableEquipment, once loaded
        static MethodInfo localGet, localSet, targetGet, targetSet; // its LanceMechEquipmentList_Init.LocalGUID / TargetComponentGUID
        static bool caeResolved;
        // Per call; AssignAmmoToWeapons does not nest. A stack would only matter if it did.
        static readonly List<KeyValuePair<object, object>> saved = new List<KeyValuePair<object, object>>();

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("CustomAmmoCategories", Apply, logLine, Tag);
            ModAssemblyHook.When("CustomActivatableEquipment", a => cae = a, logLine, Tag);   // resolved at the first load, after its Init
        }

        // CustomActivatableEquipment's extension accessors for the two injected fields; null when it is absent or its shape changed.
        static bool ResolveCae()
        {
            if (caeResolved) return localGet != null;
            if (cae == null) return false;
            caeResolved = true;
            Type owner = cae.GetType("CustomActivatableEquipment.LanceMechEquipmentList_Init", false);
            Type baseRef = ModAssemblyHook.GameType("BattleTech.BaseComponentRef");
            const BindingFlags pubStat = BindingFlags.Static | BindingFlags.Public;
            if (owner != null && baseRef != null)
            {
                localGet = owner.GetMethod("LocalGUID", pubStat, null, new[] { baseRef, typeof(bool) }, null);
                localSet = owner.GetMethod("LocalGUID", pubStat, null, new[] { baseRef, typeof(string), typeof(bool) }, null);
                targetGet = owner.GetMethod("TargetComponentGUID", pubStat, null, new[] { baseRef, typeof(bool) }, null);
                targetSet = owner.GetMethod("TargetComponentGUID", pubStat, null, new[] { baseRef, typeof(string), typeof(bool) }, null);
            }
            if (localGet == null || localSet == null || targetGet == null || targetSet == null)
            {
                localGet = null;
                log(Tag + "|links-skipped|shape changed CustomActivatableEquipment LanceMechEquipmentList_Init.LocalGUID/TargetComponentGUID");
                return false;
            }
            log(Tag + "|links-armed|CustomActivatableEquipment " + cae.GetName().Version);
            return true;
        }

        // Copies the addon links of a loaded unit's component refs from its data def. Returns the number of fields set.
        static int RestoreLinks(object actor)
        {
            if (!ResolveCae()) return 0;
            object def = Get(actor, "PilotableActorDef");
            string id = Get(Get(def, "Description"), "Id") as string;
            object dm = Get(Get(actor, "Combat"), "DataManager");
            if (def == null || id == null || dm == null) return 0;
            string tn = def.GetType().Name;
            object store = Get(dm, tn.Contains("Vehicle") ? "VehicleDefs" : tn.Contains("Turret") ? "TurretDefs" : "MechDefs");
            MethodInfo exists = StringMethod(store, "Exists"), getDef = StringMethod(store, "Get");   // IDataItemStore<string, T>
            if (exists == null || getDef == null || !(bool)exists.Invoke(store, new object[] { id })) return 0;
            var source = new Dictionary<string, object>();                     // key|ordinal -> data def ref
            var seen = new Dictionary<string, int>();
            foreach (object r in Get(getDef.Invoke(store, new object[] { id }), "Inventory") as IEnumerable ?? new object[0])
                if (r != null) source[Key(r, seen)] = r;
            seen.Clear();
            int set = 0;
            foreach (object component in Get(actor, "allComponents") as IEnumerable ?? new object[0])
            {
                object live = Get(component, "baseComponentRef");
                object from;
                if (live == null || !source.TryGetValue(Key(live, seen), out from)) continue;
                set += Copy(live, from, localGet, localSet) + Copy(live, from, targetGet, targetSet);
            }
            return set;
        }

        // A one-string-argument method of the store's class or of an interface it implements (an explicit implementation hides it on the class).
        static MethodInfo StringMethod(object store, string name)
        {
            if (store == null) return null;
            MethodInfo m = store.GetType().GetMethod(name, new[] { typeof(string) });
            if (m != null) return m;
            foreach (Type i in store.GetType().GetInterfaces())
                if ((m = i.GetMethod(name, new[] { typeof(string) })) != null) return m;
            return null;
        }

        static string Key(object componentRef, Dictionary<string, int> seen)
        {
            string k = Get(componentRef, "ComponentDefID") + "|" + Get(componentRef, "MountedLocation");
            int n;
            seen.TryGetValue(k, out n);
            seen[k] = n + 1;
            return k + "|" + n;
        }

        // Sets the live field from the data def's when the live one is empty (read with the registry, as CustomActivatableEquipment reads it).
        static int Copy(object live, object from, MethodInfo get, MethodInfo set)
        {
            if (!string.IsNullOrEmpty(get.Invoke(null, new[] { live, (object)true }) as string)) return 0;
            string value = get.Invoke(null, new[] { from, (object)false }) as string;
            if (string.IsNullOrEmpty(value)) return 0;
            set.Invoke(null, new[] { live, value, (object)false });
            return 1;
        }

        // Reflection and a patch on a game method only: no CustomAmmoCategories type is initialized before its Init.
        static void Apply(Assembly cac)
        {
            if (patched) return;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags pubStat = BindingFlags.Static | BindingFlags.Public;
            Type weaponType = ModAssemblyHook.GameType("BattleTech.Weapon");
            Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor");
            Type combat = ModAssemblyHook.GameType("BattleTech.CombatGameState");
            Type helper = cac.GetType("CleverGirlAIDamagePrediction.CleverGirlHelper", false);
            Type pair = cac.GetType("CustAmmoCategories.AmmoModePair", false);
            Type cache = cac.GetType("CustAmmoCategories.WeaponStatCacheHelper", false);
            Type ext = cac.GetType("CustAmmoCategories.WeaponExtendedInfo", false);
            getCurrent = helper?.GetMethod("getCurrentAmmoMode", pubStat);
            applyPair = helper?.GetMethod("ApplyAmmoMode", pubStat);
            modeIdProp = pair?.GetProperty("modeId", inst);
            ammoIdProp = pair?.GetProperty("ammoId", inst);
            pairCtor = pair?.GetConstructor(new[] { typeof(string), typeof(string) });   // AmmoModePair(string ammo, string mode)
            if (cache != null && weaponType != null)
                foreach (MethodInfo m in cache.GetMethods(pubStat))
                {
                    ParameterInfo[] p = m.GetParameters();
                    if (p.Length != 1 || p[0].ParameterType != weaponType) continue;
                    if (m.Name == "exDef") exDef = m;
                    else if (m.Name == "info") info = m;
                }
            modesProp = ext?.GetProperty("modes", inst);
            restrictedProp = ext?.GetProperty("restrictedModes", inst);
            combatProp = actor?.GetProperty("Combat", inst);
            loadingProp = combat?.GetProperty("IsLoadingFromSave", inst);
            MethodInfo assign = actor?.GetMethod("AssignAmmoToWeapons", inst | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            if (getCurrent == null || applyPair == null || modeIdProp == null || ammoIdProp == null || pairCtor == null || exDef == null || info == null
                || modesProp == null || restrictedProp == null || combatProp == null || loadingProp == null || assign == null)
            {
                log(Tag + "|skipped|shape changed getCurrentAmmoMode=" + (getCurrent != null) + " ApplyAmmoMode=" + (applyPair != null)
                    + " AmmoModePair=" + (modeIdProp != null && ammoIdProp != null && pairCtor != null) + " exDef=" + (exDef != null) + " info=" + (info != null)
                    + " modes=" + (modesProp != null) + " restrictedModes=" + (restrictedProp != null) + " Combat=" + (combatProp != null)
                    + " IsLoadingFromSave=" + (loadingProp != null) + " AssignAmmoToWeapons=" + (assign != null));
                return;
            }
            new Harmony(OwnId).Patch(assign,
                prefix: new HarmonyMethod(typeof(CustomAmmoCategoriesLoadModes).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(CustomAmmoCategoriesLoadModes).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
            patched = true;
            log(Tag + "|patched|" + cac.GetName().Version + "|AbstractActor.AssignAmmoToWeapons on a load: modes registered before, saved modes put back after");
        }

        static void Prefix(object __instance)
        {
            saved.Clear();
            try
            {
                if (!Loading(__instance)) return;
                int links = 0;
                try { links = RestoreLinks(__instance); }
                catch (Exception ex) { log(Tag + "|links-failed|" + Get(__instance, "GUID") + "|" + Unwrap(ex).Message); }
                if (links > 0 && ++rows <= 80) log(Tag + "|linked|" + Get(__instance, "GUID") + "|" + links);
                // Read every saved value first: registering a weapon's info resets its mode stat to the base mode.
                foreach (object weapon in Weapons(__instance)) saved.Add(new KeyValuePair<object, object>(weapon, getCurrent.Invoke(null, new[] { weapon })));
                foreach (KeyValuePair<object, object> entry in saved) exDef.Invoke(null, new[] { entry.Key });
            }
            catch (Exception ex) { log(Tag + "|prefix-failed|" + Get(__instance, "GUID") + "|" + Unwrap(ex).Message); }
        }

        static void Postfix(object __instance)
        {
            if (saved.Count == 0) return;
            try
            {
                int restored = 0;
                foreach (KeyValuePair<object, object> entry in saved)
                {
                    object weapon = entry.Key;
                    string mode = (string)modeIdProp.GetValue(entry.Value, null), ammo = (string)ammoIdProp.GetValue(entry.Value, null);
                    object now = getCurrent.Invoke(null, new[] { weapon });
                    string nowMode = (string)modeIdProp.GetValue(now, null), nowAmmo = (string)ammoIdProp.GetValue(now, null);
                    if (string.IsNullOrEmpty(ammo)) ammo = nowAmmo;                // no saved ammo stat: keep what the load chose
                    if (string.IsNullOrEmpty(mode) || (mode == nowMode && ammo == nowAmmo)) continue;
                    object weaponInfo = info.Invoke(null, new[] { weapon });
                    var modes = modesProp.GetValue(weaponInfo, null) as IDictionary;
                    var restricted = restrictedProp.GetValue(weaponInfo, null);
                    bool isRestricted = restricted != null && (bool)restricted.GetType().GetMethod("Contains").Invoke(restricted, new object[] { mode });
                    if (modes == null || !modes.Contains(mode) || isRestricted)
                    {
                        if (++rows <= 80) log(Tag + "|unknown-mode|" + Get(__instance, "GUID") + "|" + Get(weapon, "defId") + "|saved=" + mode + "|now=" + nowMode
                            + (isRestricted ? "|restricted" : ""));
                        continue;
                    }
                    applyPair.Invoke(null, new[] { weapon, pairCtor.Invoke(new object[] { ammo, mode }) });
                    restored++;
                }
                if (++rows <= 80) log(Tag + "|restored|" + Get(__instance, "GUID") + "|" + restored + "|weapons=" + saved.Count);
            }
            catch (Exception ex) { log(Tag + "|postfix-failed|" + Get(__instance, "GUID") + "|" + Unwrap(ex).Message); }
            finally { saved.Clear(); }
        }

        static bool Loading(object actor)
        {
            object combat = combatProp.GetValue(actor, null);
            return combat != null && (bool)loadingProp.GetValue(combat, null) && CombatSavesRuntimeGate.SaveDataEnabled();
        }

        static IEnumerable Weapons(object unit) => Get(unit, "Weapons") as IEnumerable ?? new object[0];

        static Exception Unwrap(Exception ex) => ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex;

        static object Get(object o, string name)
        {
            if (o == null) return null;
            const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, f | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
                FieldInfo fi = t.GetField(name, f | BindingFlags.DeclaredOnly);
                if (fi != null) return fi.GetValue(o);
            }
            return null;
        }
    }
}
