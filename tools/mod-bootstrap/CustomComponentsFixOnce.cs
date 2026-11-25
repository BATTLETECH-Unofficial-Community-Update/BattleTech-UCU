// CustomComponents default-fixer idempotence guard (gh issue 24; the maintainer, 2026-11-19: "Build the fix for 24, the guard.").
// CustomComponents' SimGameState_RespondToDefsLoadComplete_Patch.Prefix runs MechDefProcessing.Process over EVERY mech def of the DataManager each time a
// SimGameState is created (a new career, a loaded save). The processors (CustomComponents' DefaultFixer, MechEngineer's AutoFixer) rewrite each def's
// Inventory, and a second pass over defs that were already fixed reorders almost all of them (3465 of 3469 in pair-rp-modded14..16: the same components in
// another order). Component uids are positions in that order, and slot-indexed draws and uid-addressed invocations follow it, so two co-op peers that ran
// a different number of passes built the same mech with a different component behind the same uid.
// What it does: the first time a MechDef object goes through MechDefProcessing.Process it is remembered; later calls are given only the defs that have not
// been processed yet, so a def keeps the order of its first pass for the life of the process. A def that is a new object (reloaded from JSON) has not been
// processed and is processed as usual. When nothing is left to process the call is skipped.
// Active only when an assembly named CustomComponents is loaded; it patches only CustomComponents.MechDefProcessing.Process and acts only on the list that
// method receives. Without the mod nothing is patched and nothing is logged.
// Second half (co-op guest): SimGameState_Rehydrate_Patch.Postfix runs Process over the player's mech defs of every snapshot the guest applies. Those are NEW
// MechDef objects whose inventories arrive already fixed by the host, and DefaultFixer.FixMech is not idempotent on its own output (pair-rp-fixonce1: the host's
// order 061a... became d915... on the guest), so a guest that re-fixes them ends with another order than the host and component uids (positions in the
// inventory, Mech.Init) point at other components. While this process is a campaign client (BattleTech.Network.CampaignPermissions.IsClient), the order a def
// had before the call is put back after it when the call left the same components (component id and mounted location, as a multiset); a call that changed what
// the def holds keeps the fixer's result. Single-player and the host are untouched.
// Single-player effect: the second and later career load in one game process no longer reorders mech defs a second time (a def keeps its first-pass order).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomComponentsFixOnce
    {
        const string OwnId = "battletech.port.customcomponents-fixonce";
        static Action<string> log;
        static readonly ConditionalWeakTable<object, object> processed = new ConditionalWeakTable<object, object>();
        static int calls;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("CustomComponents", Apply, logLine, "customcomponents-fixonce");
        }

        static void Apply(Assembly customComponents)
        {
            Type processing = customComponents.GetType("CustomComponents.MechDefProcessing", false);
            MethodInfo process = null;
            if (processing != null)
                foreach (MethodInfo m in processing.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    ParameterInfo[] p = m.GetParameters();
                    if (m.Name == "Process" && p.Length == 1 && p[0].ParameterType.IsGenericType
                        && p[0].ParameterType.GetGenericTypeDefinition() == typeof(List<>) && p[0].ParameterType.GetGenericArguments()[0].FullName == "BattleTech.MechDef")
                    { process = m; break; }
                }
            if (process == null) { log("customcomponents-fixonce|skipped|shape changed MechDefProcessing=" + (processing != null) + " Process(List<MechDef>)=false"); return; }
            var harmony = new Harmony(OwnId);
            const BindingFlags own = BindingFlags.Static | BindingFlags.NonPublic;
            harmony.Patch(process,
                prefix: new HarmonyMethod(typeof(CustomComponentsFixOnce).GetMethod(nameof(Prefix), own)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(CustomComponentsFixOnce).GetMethod(nameof(Postfix), own)) { priority = Priority.Last });
            // The order restore applies only to the Process call CustomComponents makes from its SimGameState.Rehydrate postfix (the player's mech defs, which arrive
            // already fixed in the host's snapshot). The all-defs call made when the guest's SimGameState is created is a first pass like the host's and must reorder
            // exactly as the host's did (pair-rp-fixonce1 rerun: restoring there left 25 defs, enemy Jenners among them, in raw order on the guest only).
            Type rehydrate = customComponents.GetType("CustomComponents.Patches.SimGameState_Rehydrate_Patch", false);
            MethodInfo rehydratePostfix = rehydrate?.GetMethod("Postfix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (rehydratePostfix != null)
                harmony.Patch(rehydratePostfix,
                    prefix: new HarmonyMethod(typeof(CustomComponentsFixOnce).GetMethod(nameof(RehydrateEnter), own)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(CustomComponentsFixOnce).GetMethod(nameof(RehydrateLeave), own)) { priority = Priority.Last });
            else log("customcomponents-fixonce|rehydrate postfix not found, the client order restore stays off");
            log("customcomponents-fixonce|installed|" + processing.FullName + "::Process");
        }

        sealed class RefEq : IEqualityComparer<object> { public new bool Equals(object a, object b) { return ReferenceEquals(a, b); } public int GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); } }

        sealed class State
        {
            public List<object> Defs;
            public Dictionary<object, string[]> Before;   // client only: component id|location per inventory slot, in order
        }

        static bool inRehydrate;
        static void RehydrateEnter() { inRehydrate = true; }
        static Exception RehydrateLeave(Exception __exception) { inRehydrate = false; return __exception; }

        static PropertyInfo isClient;
        static bool resolvedClient;

        static bool IsGuestClient()
        {
            if (!resolvedClient)
            {
                resolvedClient = true;
                isClient = ModAssemblyHook.GameType("BattleTech.Network.CampaignPermissions")?.GetProperty("IsClient", BindingFlags.Public | BindingFlags.Static);
            }
            try { return isClient != null && (bool)isClient.GetValue(null, null); }
            catch { return false; }
        }

        static string[] Slots(object def)
        {
            var inv = def.GetType().GetProperty("Inventory", BindingFlags.Public | BindingFlags.Instance)?.GetValue(def, null) as IEnumerable;
            if (inv == null) return null;
            var slots = new List<string>();
            foreach (object c in inv)
            {
                Type t = c.GetType();
                slots.Add(t.GetProperty("ComponentDefID")?.GetValue(c, null) + "|" + t.GetProperty("MountedLocation")?.GetValue(c, null));
            }
            return slots.ToArray();
        }

        // Puts the pre-call order back; returns a short outcome word for the log.
        static string RestoreOrder(object def, string[] before)
        {
            PropertyInfo invProp = def.GetType().GetProperty("Inventory", BindingFlags.Public | BindingFlags.Instance);
            var after = invProp?.GetValue(def, null) as Array;
            if (after == null || before == null) return "no-inventory";
            if (after.Length != before.Length) return "content-changed";
            var queues = new Dictionary<string, Queue<object>>();
            foreach (object c in after)
            {
                Type t = c.GetType();
                string key = t.GetProperty("ComponentDefID")?.GetValue(c, null) + "|" + t.GetProperty("MountedLocation")?.GetValue(c, null);
                if (!queues.TryGetValue(key, out var q)) queues[key] = q = new Queue<object>();
                q.Enqueue(c);
            }
            var ordered = Array.CreateInstance(after.GetType().GetElementType(), after.Length);
            bool same = true;
            for (int i = 0; i < before.Length; i++)
            {
                if (!queues.TryGetValue(before[i], out var q) || q.Count == 0) return "content-changed";
                object c = q.Dequeue();
                ordered.SetValue(c, i);
                if (!ReferenceEquals(c, after.GetValue(i))) same = false;
            }
            if (same) return "unchanged";
            def.GetType().GetMethod("SetInventory", BindingFlags.Public | BindingFlags.Instance)?.Invoke(def, new object[] { ordered });
            return "restored";
        }

        static bool Prefix(object __instance, object[] __args, out State __state)
        {
            __state = null;
            try
            {
                bool client = IsGuestClient() && inRehydrate;
                ListProcessors(__instance);
                var all = __args[0] as IList;
                if (all == null) return true;
                var fresh = (IList)Activator.CreateInstance(all.GetType());
                var freshDefs = new List<object>();
                foreach (object def in all)
                {
                    if (def == null || processed.TryGetValue(def, out _)) continue;
                    fresh.Add(def);
                    freshDefs.Add(def);
                }
                int call = ++calls;
                log("customcomponents-fixonce|call=" + call + "|client=" + client + "|defs=" + all.Count + "|already-processed=" + (all.Count - fresh.Count) + "|to-process=" + fresh.Count);
                if (fresh.Count == all.Count) { __state = Capture(freshDefs, client); return true; }   // nothing seen before: the original runs on the caller's own list
                if (fresh.Count == 0) return false;
                __args[0] = fresh;
                __state = Capture(freshDefs, client);
                return true;
            }
            catch (Exception e) { log("customcomponents-fixonce|prefix failed, original runs|" + e.Message); __state = null; return true; }
        }

        static State Capture(List<object> defs, bool client)
        {
            var st = new State { Defs = defs };
            if (!client) return st;
            st.Before = new Dictionary<object, string[]>(new RefEq());
            foreach (object d in defs) { string[] slots = Slots(d); if (slots != null) st.Before[d] = slots; }
            return st;
        }

        // Which processors a skipped call also skips (everything MechDefProcessing runs, CustomComponents' AutoFixer delegates included), logged when the
        // set changes, so the record of a run shows what the guard stood in front of.
        static string lastProcessors;

        static void ListProcessors(object instance)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                var field = instance?.GetType().GetField("_processors", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field?.GetValue(instance) is IEnumerable list)
                    foreach (object p in list)
                    {
                        sb.Append(sb.Length > 0 ? "," : "").Append(p?.GetType().FullName);
                        if (p != null && p.GetType().FullName == "CustomComponents.AutoFixer")
                        {
                            var fixers = p.GetType().GetField("fixers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(p) as IEnumerable;
                            if (fixers != null)
                            {
                                sb.Append("[");
                                bool first = true;
                                foreach (Delegate d in fixers) { sb.Append(first ? "" : ";").Append(d.Method.DeclaringType?.FullName).Append("::").Append(d.Method.Name); first = false; }
                                sb.Append("]");
                            }
                        }
                    }
                string now = sb.ToString();
                if (now != lastProcessors) { lastProcessors = now; log("customcomponents-fixonce|processors|" + now); }
            }
            catch (Exception e) { log("customcomponents-fixonce|processor list failed|" + e.Message); }
        }

        static void Postfix(State __state)
        {
            if (__state == null) return;
            foreach (object def in __state.Defs) if (!processed.TryGetValue(def, out _)) processed.Add(def, null);
            if (__state.Before == null) return;
            try
            {
                var counts = new Dictionary<string, int>();
                foreach (var kv in __state.Before)
                {
                    string outcome = RestoreOrder(kv.Key, kv.Value);
                    counts[outcome] = counts.TryGetValue(outcome, out int n) ? n + 1 : 1;
                }
                var sb = new System.Text.StringBuilder();
                foreach (var kv in counts) sb.Append(sb.Length > 0 ? "," : "").Append(kv.Key).Append('=').Append(kv.Value);
                log("customcomponents-fixonce|client-order|" + sb);
            }
            catch (Exception e) { log("customcomponents-fixonce|client-order failed|" + e.Message); }
        }
    }
}
