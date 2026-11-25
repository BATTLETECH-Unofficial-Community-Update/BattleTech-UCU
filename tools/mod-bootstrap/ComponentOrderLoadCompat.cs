// Component order after a co-op combat-save load (gh issue 24, W-L7 compares 106 and 319; .hermes/reports/issue24-critorder-read-1.md).
// A fresh combat builds AbstractActor.allComponents in Mech.Init from MechDef inventory order, types mixed, each component's uid its
// inventory index (Mech.cs:1059-1091). A combat-save load rebuilds it in AbstractActor.Hydrate and Mech.Hydrate grouped by type: weapons,
// ammo, jump jets, misc (AbstractActor.cs:3869-3886, Mech.cs:3340-3345; Vehicle.cs:1073-1080 likewise). The co-op guest reloads from the
// host's snapshot on every resync and the host does not, so the same list index names a different component on each peer. Crits pick a
// slot by index into that list (CAC AdvancedCriticalProcessor.cs:101-150, :402, :428; retail Mech.GetComponentInSlot), so one crit roll
// destroyed SRM ammo (the Assassin died) on the host and a jump jet on the guest. UCU's MECHS compare ignores order (MechStatsReporter),
// so the compares stayed equal until the outcomes split.
// This adapter, in a co-op campaign session (NetworkGameInstance.IsCampaignHost or IsCampaignClient, or a co-op combat running), puts
// allComponents back in Init order in a Priority.Last postfix on Mech, Vehicle and Turret.Hydrate, before InitFromSave and InitAbilities
// read it. The key is the unit's Def.Inventory, which Init walks: each entry takes the loaded component holding that ref, else the first
// unused one with the same def id and mount. The saved uid is not usable: it is empty on most components when Hydrate ends (CBTBE-2,
// 2026-11-22: 6 of 7 units). A plain MechComponent no entry holds was added after Init by a mod (Extended_CE's Gear_BEX parts) and goes
// after the inventory in its loaded order; any other component that matches no entry leaves the list as loaded, with one row. Both peers of a
// co-op combat loaded from a save then hold the same order as a combat never loaded. Single player is untouched (retail reorders on load).
// Only game methods are patched, by LoadSteps' phase-1 hook (installed at the first combat-save load). shortcut: the eventual home is HBS's own Hydrate (a transplant patch at
// the post-4B fold).
using System;
using System.Collections;
using System.Reflection;

namespace BattleTechPort
{
    public static class ComponentOrderLoadCompat
    {
        const string Tag = "component-order-load";
        static Action<string> log;
        static PropertyInfo battleTechGame;
        static FieldInfo allComponents;
        static bool prepared, usable;
        static int rows;

        // LoadSteps phase 1 (after Mech, Vehicle and Turret.Hydrate, Priority.Last): the registry installs the postfix at the first
        // combat-save load, before any actor hydrates, and calls this once per hydrated actor.
        public static void AfterHydrate(object actor, Action<string> logLine)
        {
            log = logLine;
            if (!prepared) { prepared = true; usable = Prepare(); }
            if (usable) Postfix(actor);
        }

        static bool Prepare()
        {
            const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", stat);
            allComponents = ModAssemblyHook.GameType("BattleTech.AbstractActor")?.GetField("allComponents", inst);
            if (battleTechGame == null || allComponents == null)
            {
                log(Tag + "|skipped|shape changed BattleTechGame=" + (battleTechGame != null) + " AbstractActor.allComponents=" + (allComponents != null));
                return false;
            }
            log(Tag + "|installed|allComponents put back in Init order after a co-op combat-save load (LoadSteps phase 1)");
            return true;
        }

        static bool CoopSession()
        {
            if (AppDomain.CurrentDomain.GetData("BattleTech.UCU.CoopCombat.v1") as string == "on") return true;
            object net = Get(battleTechGame.GetValue(null, null), "NetworkGameInstance");
            return Get(net, "IsCampaignHost") is bool h && h || Get(net, "IsCampaignClient") is bool c && c;
        }

        static void Postfix(object __instance)
        {
            try
            {
                if (!CoopSession()) return;
                var list = allComponents.GetValue(__instance) as IList;
                if (list == null || list.Count < 2) return;
                int n = list.Count;
                object def = Get(__instance, "MechDef") ?? Get(__instance, "VehicleDef") ?? Get(__instance, "TurretDef");
                var inventory = Get(def, "Inventory") as IList;
                if (inventory == null) { Skip(__instance, "no def inventory at Hydrate"); return; }
                // Init walks Def.Inventory in order and makes one component per entry; walk it again and take, for each entry, the loaded
                // component holding that same ref, else the first unused one with the same def id and mount. Within one def id the loaded
                // order is the saved per-type order, which is the host's Init order.
                var used = new bool[n];
                var at = new object[n];
                int k = 0, byValue = 0;
                foreach (object entry in inventory)
                {
                    int pick = -1;
                    for (int j = 0; j < n && pick < 0; j++) if (!used[j] && ReferenceEquals(Ref(list[j]), entry)) pick = j;
                    if (pick < 0)
                        for (int j = 0; j < n && pick < 0; j++) if (!used[j] && SameEntry(Ref(list[j]), entry)) pick = j;
                    if (pick < 0) continue;
                    if (!ReferenceEquals(Ref(list[pick]), entry)) byValue++;
                    used[pick] = true;
                    at[k++] = list[pick];
                }
                // A component no inventory entry holds was added after Init by a mod (Extended_CE's Gear_BEX parts: BEXT-SPEED-fix layer G, every BEX:T
                // unit, 4 to 20 of them). Init order has it after the inventory, in the order it was added, which the loaded misc list keeps, so it
                // goes after the inventory in its loaded order. A weapon, ammo box or jump jet that matches no entry still leaves the list as loaded.
                int added = 0;
                for (int j = 0; j < n; j++)
                {
                    if (used[j]) continue;
                    string kind = list[j]?.GetType().Name;
                    if (kind != "MechComponent") { Skip(__instance, (n - k) + " of " + n + " components match no inventory entry, one a " + kind); return; }
                    at[k++] = list[j]; added++;
                }
                bool moved = false;
                for (int i = 0; i < n; i++) if (!ReferenceEquals(list[i], at[i])) { moved = true; break; }
                if (!moved) return;
                for (int i = 0; i < n; i++) list[i] = at[i];
                if (++rows <= 120) log(Tag + "|restored|" + Get(__instance, "GUID") + "|" + n + " components, " + byValue + " matched by def id and mount, " + added + " added after Init kept after the inventory");
            }
            catch (Exception e) { log(Tag + "|postfix-failed|" + Get(__instance, "GUID") + "|" + e.Message); }
        }

        static object Ref(object component)
            => Get(component, "mechComponentRef") ?? Get(component, "vehicleComponentRef") ?? Get(component, "turretComponentRef") ?? Get(component, "baseComponentRef");

        static bool SameEntry(object a, object b)
            => a != null && b != null && Equals(Get(a, "ComponentDefID") as string, Get(b, "ComponentDefID") as string)
                && Equals(Get(a, "MountedLocation")?.ToString(), Get(b, "MountedLocation")?.ToString());

        static void Skip(object actor, string why)
        {
            if (++rows <= 120) log(Tag + "|left-as-loaded|" + Get(actor, "GUID") + "|" + why);
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
