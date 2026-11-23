// Design-mask sticky effects after a combat load (mod support, gh issue 24, CBTBE-3b: Lao Hu held one more DesignMaskRough
// on the co-op guest than on the host after a resync load; .hermes/reports/issue24-designmask-read-1/report.md).
// A save holds every effect, the design-mask stickies included, but not AbstractActor.occupiedDesignMask, so a loaded actor
// starts with none. On a load AbstractActor.Init calls ReapplyDesignMasks (CustomAmmoCategories defers that call to the
// actor's first ResetPathing, still inside the load), and SetOccupiedDesignMask, finding occupiedDesignMask null, applies the
// mask's sticky effect again on top of the hydrated one. Retail's EffectManager.CreateEffect meets its stack limit there and
// only refreshes the saved effect. CustomStatisticEffects replaces CreateEffect for statistic effects and always adds the
// effect (its stack limit only decides whether the effect is applied), so every load leaves one more copy than a combat that
// was never loaded. A co-op host never reloads, so its guest then disagrees on the unit's effects after each resync.
// This adapter, only when an assembly named CustomStatisticEffects is loaded, while a combat is loading from a save with combat
// saves on (CombatSavesRuntimeGate.SaveDataEnabled: the marker or a co-op combat), runs a prefix on ReapplyDesignMasks after
// every other prefix. When the game's body is going to run (CustomAmmoCategories' deferral not active) and the actor holds no
// mask yet, it sets occupiedDesignMask to the mask the body is about to compute from the same cell. SetOccupiedDesignMask then
// sees no change, as on the live peer that put the unit there, so no sticky effect is created; the hydrated one stands. No
// effect, statistic or cell list is written. A new mission (IsLoadingFromSave false), a live move, and a combat without the
// mod are untouched.
using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class DesignMaskStickyLoadCompat
    {
        const string Tag = "designmask-load";
        static Action<string> log;
        static PropertyInfo combatOf, isLoadingFromSave, mapMetaData, occupied;
        static MethodInfo cellList, priorityMask, setOccupied;
        static FieldInfo relatedTerrainCell;
        static int rows;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("CustomStatisticEffects", Apply, logLine, Tag);
        }

        static void Apply(Assembly mod)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor");
            Type combat = ModAssemblyHook.GameType("BattleTech.CombatGameState");
            Type meta = ModAssemblyHook.GameType("BattleTech.MapMetaData");
            Type cell = ModAssemblyHook.GameType("BattleTech.MapEncounterLayerDataCell");
            Type terrainCell = ModAssemblyHook.GameType("BattleTech.MapTerrainDataCell");
            MethodInfo reapply = actor?.GetMethod("ReapplyDesignMasks", all, null, Type.EmptyTypes, null);
            combatOf = actor?.GetProperty("Combat", all);
            occupied = actor?.GetProperty("occupiedDesignMask", all);
            setOccupied = occupied?.GetSetMethod(true);
            cellList = actor?.GetMethod("getOccupiedEncounterCellList", all, null, Type.EmptyTypes, null);
            isLoadingFromSave = combat?.GetProperty("IsLoadingFromSave", all);
            mapMetaData = combat?.GetProperty("MapMetaData", all);
            priorityMask = terrainCell == null ? null : meta?.GetMethod("GetPriorityDesignMask", all, null, new[] { terrainCell }, null);
            relatedTerrainCell = cell?.GetField("relatedTerrainCell", all);
            if (reapply == null || combatOf == null || setOccupied == null || cellList == null || isLoadingFromSave == null
                || mapMetaData == null || priorityMask == null || relatedTerrainCell == null)
            {
                log(Tag + "|skipped|shape changed ReapplyDesignMasks=" + (reapply != null) + " occupiedDesignMask=" + (setOccupied != null)
                    + " getOccupiedEncounterCellList=" + (cellList != null) + " GetPriorityDesignMask=" + (priorityMask != null));
                return;
            }
            var prefix = new HarmonyMethod(typeof(DesignMaskStickyLoadCompat).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last };
            new Harmony("battletech.port.designmask-load").Patch(reapply, prefix: prefix);
            log(Tag + "|patched|" + mod.GetName().Version + "|AbstractActor.ReapplyDesignMasks on a load: the hydrated sticky effect is not applied again");
        }

        // Runs after CustomAmmoCategories' prefix, which sets __runOriginal = false while it defers the call from Init.
        static void Prefix(object __instance, ref bool __runOriginal)
        {
            if (!__runOriginal || __instance == null) return;
            try
            {
                object combat = combatOf.GetValue(__instance, null);
                if (combat == null || !(bool)isLoadingFromSave.GetValue(combat, null) || !CombatSavesRuntimeGate.SaveDataEnabled()) return;
                if (occupied.GetValue(__instance, null) != null) return;
                IList cells = cellList.Invoke(__instance, null) as IList;
                if (cells == null || cells.Count == 0) return;
                // The game's own lookup (ReapplyDesignMasks: GetPriorityDesignMask(cells[0].relatedTerrainCell)), patches included.
                object mask = priorityMask.Invoke(mapMetaData.GetValue(combat, null), new[] { relatedTerrainCell.GetValue(cells[0]) });
                if (mask == null) return;
                setOccupied.Invoke(__instance, new[] { mask });
                if (++rows <= 40 || rows % 25 == 0) log(Tag + "|kept|" + GuidOf(__instance) + "|" + IdOf(mask) + "|" + rows);
            }
            catch (Exception ex) { log(Tag + "|prefix-failed|" + GuidOf(__instance) + "|" + (ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex).Message); }
        }

        static string GuidOf(object actor)
        {
            try { return actor?.GetType().GetProperty("GUID")?.GetValue(actor, null) as string ?? "?"; } catch { return "?"; }
        }

        static string IdOf(object mask)
        {
            try { return mask.GetType().GetProperty("Id")?.GetValue(mask, null) as string ?? "?"; } catch { return "?"; }
        }
    }
}
