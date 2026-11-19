// CustomAmmoCategories adapter for combat saves (mod support). CustomAmmoCategories defers an actor's design masks: its
// prefix on AbstractActor.Init sets a thread flag, its prefix on ReapplyDesignMasks skips the game's call while the flag is
// set, and its prefix on ResetPathing makes that call later and clears the flag. In a new game that is harmless, because
// the actor's constructor (InitAbstractActor) already gave it an empty occupiedEncounterLayerCells list. On a load the game
// sets that list only in AbstractActor.Init, through the ReapplyDesignMasks call that CustomAmmoCategories skips, so
// CheckEnteredCellsForRegions(occupiedEncounterLayerCells) reads null and ends EncounterLayerParent.InitFromSavePassTwo at
// the first unit. So on a load, before AbstractActor.Init, an actor whose list is still null gets
// getOccupiedEncounterCellList(): the list the skipped ReapplyDesignMasks would have set there. The actor then enters its
// regions on the load as it does without the mod, and CustomAmmoCategories still applies the masks from ResetPathing.
// Applied when an assembly named CustomAmmoCategories loads; without it nothing is patched and nothing is logged.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomAmmoCategoriesSaveCompat
    {
        const string AssemblyName = "CustomAmmoCategories";
        static Action<string> log;
        static bool applied;
        static PropertyInfo combatOf, isLoadingFromSave, cells;
        static MethodInfo cellList;
        static int filled;

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
            catch (Exception ex) { log("cac-save|FAILED|" + ex); }
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
            Type deferral = assembly.GetType("CustAmmoCategoriesPatches.ActorMovementSequence_ReapplyDesignMasks", false);
            Type actor = GameType("BattleTech.AbstractActor");
            Type combat = GameType("BattleTech.CombatGameState");
            MethodInfo init = actor == null ? null : AccessTools.Method(actor, "Init", new[] { GameType("UnityEngine.Vector3"), typeof(float), typeof(bool) });
            combatOf = actor == null ? null : AccessTools.Property(actor, "Combat");
            cells = actor == null ? null : AccessTools.Property(actor, "occupiedEncounterLayerCells");
            cellList = actor == null ? null : AccessTools.Method(actor, "getOccupiedEncounterCellList", Type.EmptyTypes);
            isLoadingFromSave = combat?.GetProperty("IsLoadingFromSave", all);
            if (deferral == null || init == null || combatOf == null || cells == null || cells.GetSetMethod(true) == null || cellList == null || isLoadingFromSave == null)
            {
                log("cac-save|skipped|shape changed deferral=" + (deferral != null) + " Init=" + (init != null) + " Combat=" + (combatOf != null)
                    + " occupiedEncounterLayerCells=" + (cells != null) + " getOccupiedEncounterCellList=" + (cellList != null));
                return;
            }
            new Harmony("battletech.port.cac-save").Patch(init,
                prefix: new HarmonyMethod(typeof(CustomAmmoCategoriesSaveCompat).GetMethod(nameof(InitPrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
            log("cac-save|patched|" + assembly.GetName().Version + "|cell list before AbstractActor.Init on a load");
        }

        static void InitPrefix(object __instance)
        {
            if (__instance == null) return;
            try
            {
                object combat = combatOf.GetValue(__instance, null);
                if (combat == null || !(bool)isLoadingFromSave.GetValue(combat, null) || cells.GetValue(__instance, null) != null) return;
                object list = cellList.Invoke(__instance, null);
                cells.GetSetMethod(true).Invoke(__instance, new[] { list });
                if (filled++ < 3 || filled % 25 == 0) log("cac-save|cells|" + __instance.GetType().Name + "|" + filled);
            }
            catch (Exception ex) { log("cac-save|cells|FAILED|" + __instance.GetType().Name + "|" + ex); }
        }
    }
}
