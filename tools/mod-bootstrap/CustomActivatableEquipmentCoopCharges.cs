// CustomActivatableEquipment charge reads in co-op (logic-cae-charges-1: guest-only "CAEChargesCount" on a Coolant Pod, MECHS resyncs on 2.3.0-4B and HEAD).
// ActivatableComponent.isOutOfCharges and getChargesCount add the component stat CAEChargesCount, with the def's ChargesCount, when it is missing. The combat
// HUD calls them for the unit the local player has selected, so a peer got the stat by looking at a unit and the other peers did not, and the MECHS compare
// reads every component stat. This adapter, in a co-op combat only, lets the two reads return the same value without adding the stat. The stat then appears
// only when a charge is spent (setChargesCount, reached through the toggle every peer replays). No unit is listed, so units present at start, late spawns and
// units rebuilt by a resync or a reload are covered alike. Single player keeps the mod's own add. Installed at the first combat, after CAE's Init.
// Each read's one AddStatistic<int>(string, int) call is replaced; anything else than one such call per method, its result dropped, installs nothing.
// Rows: cae-charges-coop|installed|..., cae-charges-coop|withheld|<unit>|<component uid>|<def>|<value> (first 40).
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomActivatableEquipmentCoopCharges
    {
        const string Tag = "cae-charges-coop";
        static Action<string> log;
        static MethodInfo addStatistic, readAdd;
        static int rows;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("CustomActivatableEquipment", a => ModAssemblyHook.AtFirstCombat(() => Apply(a), logLine, Tag), logLine, Tag);
        }

        static void Apply(Assembly cae)
        {
            if (!CoopDecisionChannel.Init(log, Tag)) return;
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type component = ModAssemblyHook.GameType("BattleTech.MechComponent");
            Type activatable = cae.GetType("CustomActivatableEquipment.ActivatableComponent", false);
            MethodInfo isOut = activatable?.GetMethod("isOutOfCharges", st, null, new[] { component }, null);
            MethodInfo count = activatable?.GetMethod("getChargesCount", st, null, new[] { component }, null);
            readAdd = typeof(CustomActivatableEquipmentCoopCharges).GetMethod(nameof(ReadAdd), st);
            foreach (MethodInfo m in ModAssemblyHook.GameType("BattleTech.StatCollection")?.GetMethods(BindingFlags.Instance | BindingFlags.Public) ?? new MethodInfo[0])
                if (m.Name == "AddStatistic" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2) addStatistic = m.MakeGenericMethod(typeof(int));
            if (isOut == null || count == null || addStatistic == null)
            {
                log(Tag + "|skipped|shape changed ActivatableComponent.isOutOfCharges=" + (isOut != null) + " getChargesCount=" + (count != null) + " StatCollection.AddStatistic<T>(string,T)=" + (addStatistic != null));
                return;
            }
            var harmony = new Harmony("battletech.port.cae-charges-coop");
            try
            {
                foreach (MethodInfo m in new[] { isOut, count })
                    harmony.Patch(m, transpiler: new HarmonyMethod(typeof(CustomActivatableEquipmentCoopCharges).GetMethod(nameof(Transpiler), st)));
                log(Tag + "|installed|isOutOfCharges, getChargesCount: a co-op read adds no CAEChargesCount");
            }
            catch (Exception e)
            {
                harmony.UnpatchSelf();   // never one of the two
                Exception inner = e;
                while (inner.InnerException != null) inner = inner.InnerException;
                log(Tag + "|skipped|" + inner.Message);
            }
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);
            var sites = new List<int>();
            for (int i = 0; i < code.Count; i++)
                if ((code[i].opcode == OpCodes.Call || code[i].opcode == OpCodes.Callvirt) && addStatistic.Equals(code[i].operand as MethodInfo)) sites.Add(i);
            if (sites.Count != 1 || sites[0] + 1 >= code.Count || code[sites[0] + 1].opcode != OpCodes.Pop)
                throw new InvalidOperationException("shape changed " + original.Name + " AddStatistic<int> calls=" + sites.Count);
            int at = sites[0];
            code[at].opcode = OpCodes.Call;
            code[at].operand = readAdd;
            var push = new CodeInstruction(OpCodes.Ldarg_0);   // the component
            push.labels.AddRange(code[at].labels);
            code[at].labels.Clear();
            code.Insert(at, push);
            return code;
        }

        // In place of stats.AddStatistic<int>(name, value) in the two reads.
        static object ReadAdd(object stats, string name, int value, object component)
        {
            if (!CoopDecisionChannel.CoopCombat()) return addStatistic.Invoke(stats, new object[] { name, value });
            if (++rows <= 40)
                log(Tag + "|withheld|" + CoopDecisionChannel.Get(CoopDecisionChannel.Get(component, "parent"), "GUID") + "|" + CoopDecisionChannel.Get(component, "uid")
                    + "|" + CoopDecisionChannel.Get(component, "defId") + "|" + value);
            return null;
        }
    }
}
