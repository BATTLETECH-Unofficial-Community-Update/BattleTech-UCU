// MissionControl co-op drop-data sync (gh issue 24). Its random rolls (the encounter rule, additional lances, spawn points, Guid.NewGuid) are
// now seeded by CoopRngSweepDeterminism (R1 contract-begin / encounter-init / spawn points, R2 Guid redirect, R3 node rows); this file keeps the
// one part of the old MissionControlCoopDeterminism that is not a random draw (post4b-housekeeping family R, logic-post4b-rng-1).
// MissionControl learns the player's drop (difficulty, skull rating, tonnage) only from the lance screen's UI, which only the host clicks, so a
// guest's values stay 0 and its additional-lance logic runs on other data. Prefix on Contract.BeginRequestResources (Priority.First, so before
// MissionControl's own prefix), in a co-op combat only, patched when the MissionControl assembly loads as before.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class MissionControlCoopDeterminism
    {
        static Action<string> log;
        static Assembly mcAssembly;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("MissionControl", Apply, logLine, "missioncontrol-coop");
        }

        static void Apply(Assembly missionControl)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            mcAssembly = missionControl;
            MethodInfo begin = ModAssemblyHook.GameType("BattleTech.Contract")?.GetMethod("BeginRequestResources", all);
            if (begin == null) { log("missioncontrol-coop|skipped|shape changed BeginRequestResources=False"); return; }
            new Harmony("battletech.port.missioncontrol-coop").Patch(begin,
                prefix: new HarmonyMethod(typeof(MissionControlCoopDeterminism).GetMethod(nameof(BeginPrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
            log("missioncontrol-coop|patched|" + missionControl.GetName().Version + "|drop-data sync on BeginRequestResources (random rolls: rngsweep-coop)");
        }

        static void BeginPrefix(object __instance)
        {
            try
            {
                if (!IsCoopCombat(__instance)) return;
                try { SyncDropData(__instance); }
                catch (Exception ex)
                {
                    Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    string trace = inner.StackTrace ?? "";
                    int eol = trace.IndexOf((char)10);
                    string top = (eol < 0 ? trace : trace.Substring(0, eol)).Trim();
                    log("missioncontrol-coop|drop-failed|" + inner.GetType().Name + ": " + inner.Message + " @ " + top);
                }
            }
            catch (Exception ex) { log("missioncontrol-coop|begin-prefix-failed|" + ex.Message); }
        }

        // MissionControl learns the player's drop (difficulty, skull rating, tonnage) only from the lance screen's UI, in a postfix on
        // LanceConfiguratorPanel.ContinueConfirmClicked, which only the host clicks: a guest's values stay 0 and its additional-lance
        // logic runs on other data.  Both peers compute them here from the contract's own lance with the game's function behind that
        // widget (SimGameBattleSimulator.GetLanceTonnageRating) and MissionControl's own tonnage sum (chassis tonnage).
        // Host: MissionControl's UI values are logged beside the computed ones and left alone; they must be equal.
        // Guest: set when the lance is filled, else logged and left alone.
        static void SyncDropData(object contract)
        {
            object net = Get(Get(contract, "BattleTechGame"), "NetworkGameInstance");
            bool host = Get(net, "IsCampaignHost") is bool h && h;
            Type mcType = mcAssembly.GetType("MissionControl.MissionControl", false);
            object mc = mcType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null, null);
            if (mc == null) { log("missioncontrol-coop|drop|role=" + (host ? "host" : "guest") + "|no MissionControl.Instance"); return; }
            object lances = Get(contract, "Lances");
            var units = lances?.GetType().GetMethod("GetLanceUnits", new[] { typeof(string) })?.Invoke(lances, new object[] { "" }) as Array;
            int total = units?.Length ?? 0, withMech = 0, hydrated = 0, unresolved = 0, fromCompany = 0, fromDataManager = 0;
            var diag = new System.Text.StringBuilder();
            object sim = Get(Get(contract, "BattleTechGame"), "Simulation");
            Type mechDefType = ModAssemblyHook.GameType("BattleTech.MechDef");
            object mechs = Activator.CreateInstance(typeof(List<>).MakeGenericType(mechDefType));
            double chassisTonnage = 0;
            if (units != null)
                foreach (object u in units)
                {
                    // A guest's lance arrives as JSON and the contract's multiplayer path hydrates each unit just after this prefix. Read-only
                    // here: hydrate a throwaway copy (SpawnableUnit's copy constructor, then FromJSON on the copy, which builds new
                    // MechDef/PilotDef objects from the JSON) and leave the contract's own units untouched for the game to hydrate.
                    object src = u;
                    if (Get(u, "IsUnitializedJson") is bool raw && raw)
                    {
                        src = u.GetType().GetConstructor(new[] { u.GetType() })?.Invoke(new[] { u });
                        src?.GetType().GetMethod("FromJSON", Type.EmptyTypes)?.Invoke(src, null);
                        hydrated++;
                    }
                    object mech = src == null ? null : Get(src, "Unit");
                    if (mech == null) continue;
                    if (!ReferenceEquals(src, u) || Get(mech, "Chassis") == null)   // a copy, or a unit the game already deserialized from JSON without resolving its chassis
                    {
                        // The JSON copy's MechDef holds ids, not the resolved chassis and components the lance screen's computation reads
                        // (GetLanceTonnageRating threw a NullReferenceException on it). The guest's company is the host's snapshot, so the same
                        // mech sits in its ActiveMechs under the same GUID: use that resolved object, read-only.
                        string guid = Get(mech, "GUID") as string, defId = Get(u, "UnitId") as string;
                        object resolved = FindActiveMech(sim, guid);
                        string how = "company";
                        if (resolved == null || Get(resolved, "Chassis") == null)
                        {
                            // fallback: the data manager's own MechDef for the unit id (stock definition, chassis resolved when loaded)
                            resolved = FindDataManagerMech(contract, defId);
                            how = "dataManager";
                        }
                        bool usable = resolved != null && Get(resolved, "Chassis") != null;
                        diag.Append(" [guid=" + guid + " id=" + defId + " via=" + (usable ? how : "NONE") + "]");
                        if (!usable) { unresolved++; continue; }
                        mech = resolved;
                        if (how == "company") fromCompany++; else fromDataManager++;
                    }
                    withMech++;
                    mechs.GetType().GetMethod("Add").Invoke(mechs, new[] { mech });
                    chassisTonnage += Convert.ToDouble(Get(Get(mech, "Chassis"), "Tonnage"));
                }
            MethodInfo rating = ModAssemblyHook.GameType("BattleTech.SimGameBattleSimulator")?.GetMethod("GetLanceTonnageRating", BindingFlags.Static | BindingFlags.Public);
            int difficulty = -1;
            if (rating != null && sim != null && withMech > 0)
            {
                var args = new object[] { sim, mechs, 0f };
                difficulty = (int)rating.Invoke(null, args);
            }
            double skull = difficulty * 0.5;
            string uiNow = "uiDifficulty=" + Get(mc, "PlayerLanceDropDifficultyValue") + " uiSkull=" + Get(mc, "PlayerLanceDropSkullRating") + " uiTonnage=" + Get(mc, "PlayerLanceDropTonnage");
            string computed = "computedDifficulty=" + difficulty + " computedSkull=" + skull + " computedTonnage=" + chassisTonnage;
            string filled = "lanceUnits=" + total + " withMechDef=" + withMech + " hydratedHere=" + hydrated + " resolvedFromCompany=" + fromCompany + "/" + total + " resolvedFromDataManager=" + fromDataManager + " unresolved=" + unresolved + diag;
            string role = host ? "host" : "guest";
            if (host)
            {
                bool same = withMech > 0 && Convert.ToDouble(Get(mc, "PlayerLanceDropDifficultyValue")) == difficulty
                    && Math.Abs(Convert.ToDouble(Get(mc, "PlayerLanceDropTonnage")) - chassisTonnage) < 0.01;
                log("missioncontrol-coop|drop|role=host|" + filled + "|" + uiNow + "|" + computed + "|" + (same ? "EQUAL" : "DIFFERENT: the computation is not MissionControl's, not trusted"));
                return;
            }
            // All or nothing: a total over part of the lance would put a wrong difficulty on the guest.  The difficulty also reads what a company mech can change
            // (CalculateTonnage: assigned armor and the inventory on top of the chassis tonnage), so only mechs taken from the company are exact; the
            // DataManager route gives the stock definition and is logged as approximate, never written.
            if (withMech == 0 || difficulty < 0 || unresolved > 0 || withMech != total || fromDataManager > 0)
            {
                log("missioncontrol-coop|drop|role=guest|FAILED SYNC, nothing written|" + filled + "|" + uiNow + "|" + computed + (fromDataManager > 0 ? " (approximate: stock MechDef used for " + fromDataManager + " unit(s))" : ""));
                return;
            }
            SetProp(mc, "PlayerLanceDropDifficultyValue", difficulty);
            SetProp(mc, "PlayerLanceDropSkullRating", skull);
            SetProp(mc, "PlayerLanceDropTonnage", chassisTonnage);
            log("missioncontrol-coop|drop|role=guest|" + filled + "|" + uiNow + "|" + computed + "|set");
        }

        static object FindDataManagerMech(object contract, string defId)
        {
            if (string.IsNullOrEmpty(defId)) return null;
            object dm = Get(contract, "dataManager") ?? Get(Get(contract, "BattleTechGame"), "DataManager");
            object store = Get(dm, "MechDefs");
            MethodInfo get = store?.GetType().GetMethod("Get", new[] { typeof(string) });
            return get?.Invoke(store, new object[] { defId });
        }

        static object FindActiveMech(object sim, string guid)
        {
            if (sim == null || string.IsNullOrEmpty(guid)) return null;
            var active = Get(sim, "ActiveMechs") as System.Collections.IDictionary;
            if (active == null) return null;
            foreach (object m in active.Values)
                if (string.Equals(Get(m, "GUID") as string, guid, StringComparison.Ordinal)) return m;
            return null;
        }

        static void SetProp(object o, string name, double value)
        {
            PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            p.SetValue(o, Convert.ChangeType(value, p.PropertyType), null);
        }

        static bool IsCoopCombat(object contract)
        {
            object net = Get(Get(contract, "BattleTechGame"), "NetworkGameInstance");
            return net != null && Get(net, "IsCoopCombat") is bool b && b;
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
