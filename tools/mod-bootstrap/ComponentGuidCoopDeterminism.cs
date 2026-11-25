// Component identity GUIDs for co-op (gh issue 24). CustomAmmoCategories gives every weapon (and ammo box) a "WeaponGUID" / "AmmoBoxGUID" stat
// and CustomComponents gives every component a "CCComponentGUID" stat, each a System.Guid.NewGuid() minted the first time the code asks for it.
// Retail's MECHS compare (MechStatsReporter) carries every component's StatCollection, so with either mod loaded each peer's reporter hash
// differed on every mech even when the units were identical (pair-rp-modded11: 160 WeaponGUID values differ, CCComponentGUID exists on 15
// components of one peer only; rotation, position, flags, pilot and mech stats equal).
// While a co-op combat runs and only when the owning mod assembly is loaded, the minting methods get a deterministic GUID: FNV over the
// site, the owning actor's GUID and the component's uid (uids are only an index within the mech, "0", "1", ..., so the actor GUID is part
// of the key; both are equal on both peers). And the identity stat is created right after each actor's Init on both peers, so the key sets
// (and the dictionary order the reporter hash is built in) match; stats created by value caches (CACUsedHEatSinks) are NOT touched here.
// Single-player, an unmodded co-op battle and a co-op battle without these mods are untouched; System.Guid.NewGuid is never patched.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace BattleTechPort
{
    public static class ComponentGuidCoopDeterminism
    {
        const string OwnId = "battletech.port.component-guid-coop";
        static Action<string> log;
        static MethodInfo guidNewGuid, newGuidFor, ccGetGuid, cacGetGuid, heatSinkCap;   // heatSinkCap: CAC UsedHeatSinksCap(Mech)
        static string cacStatName, ccStatName;   // CustomAmmoCategories.GUIDStatisticName ("WeaponGUID")
        static readonly Dictionary<MethodBase, int> replacedCalls = new Dictionary<MethodBase, int>();
        static readonly Dictionary<MethodBase, int> componentArg = new Dictionary<MethodBase, int>();
        static int minted, notCoop, eagerActors, noKey, rekeyed, heatLogged;
        static string cacHeatStatName;
        static readonly Dictionary<object, bool> lateKeyed = new Dictionary<object, bool>(new RefEq());   // components that got a random GUID because they had no key yet
        sealed class RefEq : IEqualityComparer<object> { public new bool Equals(object a, object b) { return ReferenceEquals(a, b); } public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); } }

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            guidNewGuid = typeof(Guid).GetMethod("NewGuid", Type.EmptyTypes);
            newGuidFor = typeof(ComponentGuidCoopDeterminism).GetMethod(nameof(NewGuidFor), BindingFlags.Static | BindingFlags.NonPublic);
            // (type, method, parameter type names, index of the component argument)
            ModAssemblyHook.When("CustomAmmoCategories", mod => PatchSites(mod, "CAC", new[]
            {
                new Site("CustAmmoCategories.CustomAmmoCategories", "getCACGUID", new[] { "Weapon" }, 0),
                new Site("CustAmmoCategories.CustomAmmoCategories", "createAmmoBoxGUID", new[] { "AmmunitionBox", "String" }, 0),
                new Site("CustAmmoCategories.CustomAmmoCategories", "getAmmoBoxGUID", new[] { "AmmunitionBox" }, 0),
                new Site("CustAmmoCategories.WeaponRepresentation_Init", "Postfix", new[] { "WeaponRepresentation", "Weapon", "Transform", "Boolean", "String", "Int32" }, 1),
            }), logLine, "component-guid-coop-cac");
            ModAssemblyHook.When("CustomComponents", mod => PatchSites(mod, "CC", new[]
            {
                new Site("CustomComponents.CustomCombatTagsHelper", "getCCGUID", new[] { "MechComponent" }, 0),
            }), logLine, "component-guid-coop-cc");
            ModAssemblyHook.When("Assembly-CSharp", PatchActorInit, logLine, "component-guid-coop-init");
        }

        struct Site
        {
            public string Type, Method; public string[] Params; public int Arg;
            public Site(string type, string method, string[] ps, int arg) { Type = type; Method = method; Params = ps; Arg = arg; }
        }

        static void PatchSites(Assembly mod, string tag, Site[] sites)
        {
            var harmony = new Harmony(OwnId + "-" + tag);
            var transpiler = new HarmonyMethod(typeof(ComponentGuidCoopDeterminism).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Normal };
            int ok = 0;
            foreach (Site s in sites)
            {
                MethodBase m = Resolve(mod, s);
                if (m == null) { log("component-guid-coop|" + tag + "|site-unresolved|" + s.Type + "::" + s.Method); continue; }
                if (!m.IsStatic) { log("component-guid-coop|" + tag + "|site-not-static|" + s.Type + "::" + s.Method); continue; }
                try
                {
                    componentArg[m] = s.Arg;
                    harmony.Patch(m, transpiler: transpiler);
                    int replaced = replacedCalls.TryGetValue(m, out int r) ? r : 0;
                    if (replaced > 0) ok++;
                    log("component-guid-coop|" + tag + "|site|" + s.Type + "::" + s.Method + "|NewGuid calls replaced=" + replaced + (replaced > 0 ? "|ok" : "|NOT REPLACED"));
                    if (s.Method == "getCCGUID")
                    {
                        ccGetGuid = (MethodInfo)m;
                        ccStatName = m.DeclaringType.GetField("CCComponentGUIDStatName", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as string;
                    }
                    if (s.Method == "getCACGUID")
                    {
                        cacGetGuid = (MethodInfo)m;
                        cacStatName = m.DeclaringType.GetField("GUIDStatisticName", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as string;
                    }
                }
                catch (Exception ex) { log("component-guid-coop|" + tag + "|site-failed|" + s.Type + "::" + s.Method + "|" + ex.Message); }
            }
            if (tag == "CAC")
            {
                // The value cache CACUsedHEatSinks is created lazily by UsedHeatSinksCap(Mech) (AddStatistic(name, 0), returns 0): from the HUD heat getter of whichever
                // unit a peer has selected, and from the activation prefix. Calling the same method for every Mech at Init gives both peers the same key set with the
                // value the lazy path creates (IL: ldc.i4.0), see InitPostfix.
                cacHeatStatName = mod.GetType("CustAmmoCategories.CustomAmmoCategories", false)?.GetField("UsesHeatSinkCap", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as string;
                heatSinkCap = Resolve(mod, new Site("CustAmmoCategories.CustomAmmoCategories", "UsedHeatSinksCap", new[] { "Mech" }, 0)) as MethodInfo;
                log("component-guid-coop|CAC|heatsink-cap|" + (heatSinkCap != null ? "UsedHeatSinksCap(Mech) resolved" : "UsedHeatSinksCap(Mech) NOT FOUND"));
            }
            log("component-guid-coop|" + tag + "|patched|" + mod.GetName().Version + "|sites " + ok + " of " + sites.Length);
        }

        static MethodBase Resolve(Assembly assembly, Site s)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type t = assembly.GetType(s.Type, false);
            if (t == null) return null;
            MethodBase match = null;
            foreach (MethodInfo c in t.GetMethods(all))
            {
                if (c.Name != s.Method) continue;
                ParameterInfo[] ps = c.GetParameters();
                if (ps.Length != s.Params.Length) continue;
                bool same = true;
                for (int i = 0; i < ps.Length && same; i++) same = ps[i].ParameterType.Name == s.Params[i];
                if (!same) continue;
                if (match != null) return null;   // ambiguous: leave it alone
                match = c;
            }
            return match;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            string site = original.DeclaringType?.Name + "::" + original.Name;
            int arg = componentArg.TryGetValue(original, out int a) ? a : 0;
            foreach (CodeInstruction ins in instructions)
            {
                if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && ins.operand as MethodInfo == guidNewGuid)
                {
                    replacedCalls[original] = (replacedCalls.TryGetValue(original, out int n) ? n : 0) + 1;
                    var push = new CodeInstruction(OpCodes.Ldstr, site);   // takes over the replaced call's branch labels and exception-block markers
                    push.labels.AddRange(ins.labels);
                    push.blocks.AddRange(ins.blocks);
                    yield return push;
                    yield return arg == 0 ? new CodeInstruction(OpCodes.Ldarg_0) : arg == 1 ? new CodeInstruction(OpCodes.Ldarg_1) : new CodeInstruction(OpCodes.Ldarg_S, (byte)arg);
                    yield return new CodeInstruction(OpCodes.Call, newGuidFor);
                }
                else yield return ins;
            }
        }

        // Called in place of Guid.NewGuid() inside the mods' GUID getters: the component being asked for is passed as well.
        static Guid NewGuidFor(string site, object component)
        {
            try
            {
                if (!IsCoopCombat(component)) { if (++notCoop <= 3) log("component-guid-coop|guid-not-coop|" + site + "|random GUID minted|n=" + notCoop); return Guid.NewGuid(); }
                object parent = Get(component, "parent");
                string actor = Get(parent, "GUID") as string, uid = Get(component, "uid") as string;
                // The key must not depend on the order of the actor's inventory: pair-rp-modded14 found enemy (TargetTeam) mechs whose MechDef inventory is in a
                // different order on each peer (same components, different uids), and the retail reporter pairs components by name and occurrence, not by uid. So the key
                // is the component's defId and its occurrence among the actor's components with that defId, in allComponents order (the reporter's own "name#n").
                // AmmunitionBox components also carry no uid in retail. A component not yet in allComponents (CustomComponents asks inside Mech.Init, ahead of
                // allComponents.Add) gets a random GUID and is remembered, so the actor's Init postfix overwrites the stat with the keyed value.
                if (!string.IsNullOrEmpty(actor) && Get(parent, "allComponents") is IList list)
                {
                    int at = list.IndexOf(component);
                    if (at >= 0)
                    {
                        string def = Get(component, "defId") as string ?? "?";
                        int nth = 0;
                        for (int i2 = 0; i2 < at; i2++) if ((Get(list[i2], "defId") as string ?? "?") == def) nth++;
                        uid = def + "#" + nth;
                    }
                    else
                    {
                        lock (lateKeyed) lateKeyed[component] = true;          // rekeyed by defId/occurrence at Init if it ends up in allComponents
                        uid = string.IsNullOrEmpty(uid) ? null : "u:" + uid;   // melee/DFA/imaginary weapons never enter allComponents: their own uid keys them
                    }
                }
                if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(uid))
                {
                    lock (lateKeyed) lateKeyed[component] = true;
                    string where = "";
                    if (++noKey <= 6)
                    {
                        var frames = new System.Diagnostics.StackTrace(1, false).GetFrames();
                        for (int i = 0; frames != null && i < frames.Length && i < 8; i++) where += (i > 0 ? " < " : "") + frames[i].GetMethod()?.DeclaringType?.Name + "." + frames[i].GetMethod()?.Name;
                    }
                    log("component-guid-coop|guid-no-key|" + site + "|actor=" + actor + " uid=" + uid + " type=" + component.GetType().Name + " defId=" + (Get(component, "defId") as string ?? "?")
                        + "|random GUID minted" + (where.Length > 0 ? "|from " + where : ""));
                    return Guid.NewGuid();
                }
                string text = "comp-guid|" + site + "|" + actor + "|" + uid;
                ulong a = Fnv64(text, 14695981039346656037UL), b = Fnv64(text, 1099511628211UL ^ 0x9E3779B97F4A7C15UL);
                var bytes = new byte[16];
                Array.Copy(BitConverter.GetBytes(a), 0, bytes, 0, 8);
                Array.Copy(BitConverter.GetBytes(b), 0, bytes, 8, 8);
                var guid = new Guid(bytes);
                if (++minted <= 600) log("component-guid-coop|guid|" + site + "|key=" + actor + "/" + uid + " (" + (Get(component, "defId") as string ?? "?") + ")|" + guid);
                return guid;
            }
            catch (Exception ex) { log("component-guid-coop|guid-failed|" + site + "|" + ex.Message); return Guid.NewGuid(); }
        }

        static bool IsCoopCombat(object component)
        {
            object combat = Get(component, "combat") ?? Get(Get(component, "parent"), "Combat");
            object net = Get(combat, "NetworkGameInstance");
            if (net == null)
            {
                Type ugi = ModAssemblyHook.GameType("BattleTech.UnityGameInstance");
                net = Get(Get(ugi, "BattleTechGame"), "NetworkGameInstance");
            }
            return net != null && Get(net, "IsCoopCombat") is bool coop && coop;
        }

        // Identity stats are created right after an actor's Init on both peers, in component order, so the key sets match.
        static void PatchActorInit(Assembly game)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var harmony = new Harmony(OwnId + "-init");
            var postfix = new HarmonyMethod(typeof(ComponentGuidCoopDeterminism).GetMethod(nameof(InitPostfix), BindingFlags.Static | BindingFlags.NonPublic));
            int patched = 0;
            foreach (string name in new[] { "BattleTech.Mech", "BattleTech.Vehicle", "BattleTech.Turret" })
            {
                Type t = game.GetType(name, false);
                if (t == null) continue;
                foreach (MethodInfo m in t.GetMethods(all))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (m.Name != "Init" || ps.Length != 3 || ps[0].ParameterType.Name != "Vector3") continue;
                    harmony.Patch(m, postfix: postfix);
                    patched++;
                }
            }
            log("component-guid-coop|init|patched|Init postfixes=" + patched);
        }

        static void InitPostfix(object __instance)
        {
            try
            {
                if (ccGetGuid == null && cacGetGuid == null) return;
                var comps = Get(__instance, "allComponents") as IEnumerable;
                if (comps == null) return;
                int n = 0, cc = 0, cac = 0;
                string sample = "";
                foreach (object c in comps)
                {
                    if (c == null) continue;
                    n++;
                    if (n <= 4) sample += (sample.Length > 0 ? "," : "") + Get(c, "uid") + ":" + Get(c, "defId");
                    if (n == 1 && !IsCoopCombat(c)) return;
                    if (ccGetGuid != null) { ccGetGuid.Invoke(null, new[] { c }); cc++; }
                    bool late; lock (lateKeyed) late = lateKeyed.Remove(c);
                    if (late)
                    {
                        if (ccStatName != null && SetStat(c, ccStatName, "CustomCombatTagsHelper::getCCGUID")) rekeyed++;
                        if (cacStatName != null && SetStat(c, cacStatName, "CustomAmmoCategories::getCACGUID")) rekeyed++;
                    }
                    // CustomActivatableEquipment's ComponentVFXHelper mints the same "WeaponGUID" stat for EVERY component of an actor (ContainsStatistic,
                    // else Guid.NewGuid), so the stat is created here first, with the value getCACGUID would mint: its check then finds it.
                    if (cacStatName != null && EnsureStat(c, cacStatName, "CustomAmmoCategories::getCACGUID")) cac++;
                }
                if (heatSinkCap != null && IsMech(__instance))
                {
                    object stats = Get(__instance, "StatCollection");
                    MethodInfo contains = stats?.GetType().GetMethod("ContainsStatistic", new[] { typeof(string) });
                    bool had = contains != null && cacHeatStatName != null && (bool)contains.Invoke(stats, new object[] { cacHeatStatName });
                    object value = heatSinkCap.Invoke(null, new[] { __instance });
                    if (++heatLogged <= 12) log("component-guid-coop|heatsink-stat|" + Get(__instance, "GUID") + "|existed=" + had + "|value=" + value);
                }
                if (n > 0 && ++eagerActors <= 12) log("component-guid-coop|eager|" + Get(__instance, "GUID") + "|components=" + n + " cc=" + cc + " cac=" + cac + " rekeyed=" + rekeyed + " uids=" + sample);
            }
            catch (Exception ex) { log("component-guid-coop|eager-failed|" + (ex.InnerException ?? ex).Message); }
        }

        static bool IsMech(object o)
        {
            for (Type t = o.GetType(); t != null; t = t.BaseType) if (t.Name == "Mech") return true;
            return false;
        }

        // Overwrites an existing string stat with the keyed GUID (StatCollection.Set<string>(name, value)).
        static bool SetStat(object component, string statName, string site)
        {
            object stats = Get(component, "StatCollection");
            if (stats == null) return false;
            MethodInfo contains = stats.GetType().GetMethod("ContainsStatistic", new[] { typeof(string) });
            MethodInfo set = null;
            foreach (MethodInfo m in stats.GetType().GetMethods())
                if (m.Name == "Set" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(string)) { set = m.MakeGenericMethod(typeof(string)); break; }
            if (contains == null || set == null || !(bool)contains.Invoke(stats, new object[] { statName })) return false;
            set.Invoke(stats, new object[] { statName, NewGuidFor(site, component).ToString() });
            return true;
        }

        // Adds the string stat statName to the component's StatCollection with the deterministic GUID, when it is not there yet.
        static bool EnsureStat(object component, string statName, string site)
        {
            object stats = Get(component, "StatCollection");
            if (stats == null) return false;
            MethodInfo contains = stats.GetType().GetMethod("ContainsStatistic", new[] { typeof(string) });
            MethodInfo add = null;
            foreach (MethodInfo m in stats.GetType().GetMethods())
                if (m.Name == "AddStatistic" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(string)) { add = m.MakeGenericMethod(typeof(string)); break; }
            if (contains == null || add == null) return false;
            if ((bool)contains.Invoke(stats, new object[] { statName })) return false;
            add.Invoke(stats, new object[] { statName, NewGuidFor(site, component).ToString() });
            return true;
        }

        static ulong Fnv64(string text, ulong basis)
        {
            ulong h = basis;
            for (int i = 0; i < text.Length; i++) h = (h ^ text[i]) * 1099511628211UL;
            return h;
        }

        static object Get(object o, string name)
        {
            if (o == null) return null;
            const BindingFlags f = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type start = o as Type ?? o.GetType();
            object target = o is Type ? null : o;
            for (Type t = start; t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, f | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(target, null);
                FieldInfo fi = t.GetField(name, f | BindingFlags.DeclaredOnly);
                if (fi != null) return fi.GetValue(target);
            }
            return null;
        }
    }
}
