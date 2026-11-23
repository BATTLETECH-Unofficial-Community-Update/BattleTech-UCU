// CustomAmmoCategories' brace-evaluation weapon modes on the co-op host (gh issue 24, fault J; .hermes/reports/issue24-faultJ-read-2.md).
// CustomAmmoCategories prefixes the AI behaviour tree's BraceNode.Tick (CustAmmoCategories.BraceNode_Tick.Prefix): to find a weapon that can fire indirectly it
// calls ApplyAmmoMode with every firing method of every weapon, keeps the first capable mode of each weapon it selects for its brace attack, and never puts the
// others back, so a weapon it did not select is left in the last mode it tried (the enemy Viper's Clan AMS left Off). CustomAmmoCategories' other evaluators
// (AIMaxWeaponRangeCache, BlockWeaponsHelpers) restore the current mode. The behaviour tree runs only on the peer that thinks for the AI (the co-op host), so the
// leftover mode existed there only, the CMP-9 MECHS compare differed ("CAC-WeaponMode" Off on the host, On on the guest) and the guest was resynced.
// This adapter, only when an assembly named CustomAmmoCategories is loaded and only in a co-op combat, records each weapon's mode and ammo before BraceNode.Tick
// (ahead of CustomAmmoCategories' prefix) and afterwards puts back, with CustomAmmoCategories' own ApplyAmmoMode, every weapon whose mode or ammo changed and that
// is not in the attack order the node returned. Weapons in that order keep the mode the brace attack fires with. Single player is untouched.
// shortcut: the brace attack's own chosen modes are not sent to the other peer; that needs a CAC mode sync (decision D-20261121-3).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomAmmoCategoriesCoopBraceModes
    {
        const string OwnId = "battletech.port.cac-coop-brace-modes";
        static Action<string> log;
        static MethodInfo getCurrent, applyPair;
        static PropertyInfo modeIdProp, ammoIdProp;
        static FieldInfo unitField, orderWeaponsField;
        static readonly List<KeyValuePair<object, object>> saved = new List<KeyValuePair<object, object>>();
        static int logged;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("CustomAmmoCategories", Apply, logLine, "cac-coop-brace-modes");
        }

        static void Apply(Assembly cac)
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type helper = cac.GetType("CleverGirlAIDamagePrediction.CleverGirlHelper", false);
            Type pair = cac.GetType("CustAmmoCategories.AmmoModePair", false);
            getCurrent = helper?.GetMethod("getCurrentAmmoMode", BindingFlags.Static | BindingFlags.Public);
            applyPair = helper?.GetMethod("ApplyAmmoMode", BindingFlags.Static | BindingFlags.Public);
            modeIdProp = pair?.GetProperty("modeId", inst);
            ammoIdProp = pair?.GetProperty("ammoId", inst);
            Type brace = ModAssemblyHook.GameType("BraceNode");
            MethodInfo tick = brace?.GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            unitField = ModAssemblyHook.GameType("BehaviorNode")?.GetField("unit", inst);
            orderWeaponsField = ModAssemblyHook.GameType("AttackOrderInfo")?.GetField("Weapons", inst);
            if (getCurrent == null || applyPair == null || modeIdProp == null || ammoIdProp == null || tick == null || unitField == null || orderWeaponsField == null)
            {
                log("cac-coop-brace-modes|skipped|shape changed getCurrentAmmoMode=" + (getCurrent != null) + " ApplyAmmoMode=" + (applyPair != null)
                    + " AmmoModePair=" + (modeIdProp != null && ammoIdProp != null) + " BraceNode.Tick=" + (tick != null) + " unit=" + (unitField != null)
                    + " AttackOrderInfo.Weapons=" + (orderWeaponsField != null));
                return;
            }
            Harmony h = new Harmony(OwnId);
            h.Patch(tick, prefix: new HarmonyMethod(typeof(CustomAmmoCategoriesCoopBraceModes).GetMethod(nameof(TickPrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(CustomAmmoCategoriesCoopBraceModes).GetMethod(nameof(TickPostfix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
            log("cac-coop-brace-modes|installed|BraceNode.Tick weapon modes restored in co-op combat except the brace attack's");
        }

        static void TickPrefix(object __instance)
        {
            saved.Clear();
            try
            {
                object unit = unitField.GetValue(__instance);
                if (!InCoop(unit)) return;
                foreach (object weapon in Weapons(unit))
                    saved.Add(new KeyValuePair<object, object>(weapon, getCurrent.Invoke(null, new[] { weapon })));
            }
            catch (Exception ex) { saved.Clear(); log("cac-coop-brace-modes|prefix failed|" + ex.Message); }
        }

        static void TickPostfix(object __result)
        {
            if (saved.Count == 0) return;
            try
            {
                IList chosen = __result == null ? null : orderWeaponsField.DeclaringType.IsInstanceOfType(Get(__result, "orderInfo")) ? orderWeaponsField.GetValue(Get(__result, "orderInfo")) as IList : null;
                int restored = 0;
                foreach (KeyValuePair<object, object> entry in saved)
                {
                    if (chosen != null && chosen.Contains(entry.Key)) continue;
                    object now = getCurrent.Invoke(null, new[] { entry.Key });
                    if (Same(now, entry.Value)) continue;
                    applyPair.Invoke(null, new[] { entry.Key, entry.Value });
                    restored++;
                }
                if (restored > 0 && ++logged <= 12) log("cac-coop-brace-modes|restored|" + restored + " weapon(s); attack order weapons " + (chosen?.Count ?? 0));
            }
            catch (Exception ex) { log("cac-coop-brace-modes|postfix failed|" + ex.Message); }
            finally { saved.Clear(); }
        }

        static bool Same(object a, object b)
            => a != null && b != null && (string)modeIdProp.GetValue(a, null) == (string)modeIdProp.GetValue(b, null) && (string)ammoIdProp.GetValue(a, null) == (string)ammoIdProp.GetValue(b, null);

        static IEnumerable Weapons(object unit) => Get(unit, "Weapons") as IEnumerable ?? new object[0];

        static bool InCoop(object unit)
        {
            object net = Get(Get(unit, "Combat"), "NetworkGameInstance");
            if (net == null)
            {
                PropertyInfo game = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                net = Get(game?.GetValue(null, null), "NetworkGameInstance");
            }
            return net != null && Get(net, "IsCoopCombat") is bool coop && coop;
        }

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
