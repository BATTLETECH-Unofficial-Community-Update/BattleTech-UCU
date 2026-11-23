// AttackImprovementMod called-shot clustering on CustomUnits targets in co-op (gh issue 24, W-L7 volley seq 68647775;
// .hermes/reports/issue24-pronevolley-read-1.md).
// With CalledShotUseClustering on (BTA), AIM's PrefixMechCalledShot, a prefix on the game's generic HitLocation.GetHitLocation, replaces a
// called shot's hit table with GetHitTableCluster(CurrentHitDirection, calledLocation). CurrentHitDirection is a private static that AIM's
// RecordHitDirection postfix on HitLocation.GetMechHitTable writes on every call, from any caller. CustomUnits' CustomMech.GetHitLocation
// (and the TrooperSquad and FakeVehicleMech overrides) builds its own table without calling GetMechHitTable, so for a CU target the
// direction is whatever the peer last asked a table for: the host's AI think leaves its own value, the guest's is another or None. In W-L7
// an AI called shot on the prone Blackjack used a FromFront cluster table on the host (LeftArm) and the prone table on the guest (LeftTorso)
// for the same roll 0.4228203 (host CAC_AIM_log:40 against guest :46), and the six clustered hits split.
// This adapter, only when AttackImprovementMod and CustomUnits are loaded and only in a co-op combat, sets CurrentHitDirection to this
// attack's own direction (ToProne for a prone target, else CombatGameState.HitLocation.GetAttackDirection(attackPosition, target)), as
// AIM records it for a vanilla mech's GetHitLocation and as CU picks its own table, in a prefix on those three GetHitLocation methods, and
// puts the peer's previous value back in a finalizer. Both peers then build the same cluster table. Installed at the first combat
// (ModAssemblyHook.AtFirstCombat), after the mods' Init (fault M). Single player is untouched.
using System;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class AttackImprovementModCoopCalledShot
    {
        const string OwnId = "battletech.port.aim-calledshot-coop";
        const string Tag = "aim-calledshot-coop";
        static Action<string> log;
        static PropertyInfo battleTechGame;
        static FieldInfo direction;
        static MethodInfo attackDirection;
        static object toProne;
        static int rows;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            // Hooked on CustomAmmoCategories, which ships and loads AIM, as CustomAmmoCategoriesCoopImpact is; AIM is loaded by the first combat.
            ModAssemblyHook.When("CustomAmmoCategories", cac => ModAssemblyHook.AtFirstCombat(Apply, logLine, Tag), logLine, Tag);
        }

        static void Apply()
        {
            const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", stat);
            direction = ModAssemblyHook.GameType("Sheepy.BattleTechMod.AttackImprovementMod.HitLocation")?.GetField("CurrentHitDirection", stat);
            Type dirType = ModAssemblyHook.GameType("BattleTech.AttackDirection");
            Type actor = ModAssemblyHook.GameType("BattleTech.AbstractActor");
            Type vector3 = ModAssemblyHook.GameType("UnityEngine.Vector3");
            Type icombatant = ModAssemblyHook.GameType("BattleTech.ICombatant");
            attackDirection = ModAssemblyHook.GameType("BattleTech.HitLocation")?.GetMethod("GetAttackDirection", inst, null, new[] { vector3, icombatant }, null);
            toProne = dirType == null ? null : Enum.Parse(dirType, "ToProne");
            if (battleTechGame == null || direction == null || direction.FieldType != dirType || attackDirection == null || actor == null || vector3 == null)
            {
                log(Tag + "|skipped|shape changed BattleTechGame=" + (battleTechGame != null) + " AIM HitLocation.CurrentHitDirection=" + (direction != null)
                    + " HitLocation.GetAttackDirection(Vector3,ICombatant)=" + (attackDirection != null));
                return;
            }
            var harmony = new Harmony(OwnId);
            var prefix = new HarmonyMethod(typeof(AttackImprovementModCoopCalledShot).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
            var finalizer = new HarmonyMethod(typeof(AttackImprovementModCoopCalledShot).GetMethod(nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last };
            string patched = "";
            foreach (string name in new[] { "CustomUnits.CustomMech", "CustomUnits.TrooperSquad", "CustomUnits.FakeVehicleMech" })
            {
                MethodInfo m = ModAssemblyHook.GameType(name)?.GetMethod("GetHitLocation", inst, null, new[] { actor, vector3, typeof(float), typeof(int), typeof(float) }, null);
                if (m == null) { log(Tag + "|skipped-method|" + name + ".GetHitLocation not found"); continue; }
                harmony.Patch(m, prefix: prefix, finalizer: finalizer);
                patched += " " + name;
            }
            log(Tag + "|installed|CurrentHitDirection = the attack's direction in co-op combat around" + patched + " GetHitLocation");
        }

        // __args: attacker, attackPosition, hitLocationRoll, calledShotLocation, bonusMultiplier. __state holds the peer's previous direction.
        static void Prefix(object __instance, object[] __args, ref object __state)
        {
            __state = null;
            try
            {
                object combat = Get(battleTechGame.GetValue(null, null), "Combat");
                object net = Get(combat, "NetworkGameInstance");
                if (net == null || !(Get(net, "IsCoopCombat") is bool coop) || !coop) return;
                object dir = Get(__instance, "IsProne") is bool prone && prone ? toProne
                    : attackDirection.Invoke(Get(combat, "HitLocation"), new[] { __args[1], __instance });
                __state = direction.GetValue(null);
                direction.SetValue(null, dir);
                if ((int)__args[3] != 0 && ++rows <= 80)
                    log(Tag + "|set|" + Get(__instance, "GUID") + "|called=" + __args[3] + "|" + dir + "|was=" + __state);
            }
            catch (Exception e) { log(Tag + "|prefix-failed|" + (e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e).Message); }
        }

        static void Finalizer(object __state)
        {
            if (__state == null) return;
            try { direction.SetValue(null, __state); }
            catch (Exception e) { log(Tag + "|restore-failed|" + e.Message); }
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
