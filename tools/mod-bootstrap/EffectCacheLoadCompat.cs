// RogueTechPerfFixes and CustomStatisticEffects adapter for combat saves (mod support, gh issue 24 fault U). Each mod keeps
// its own index of EffectManager's effects: RogueTechPerfFixes answers EffectManager.GetAllEffectsTargeting from a cache
// keyed by target, CustomStatisticEffects counts stack limits in a cache keyed by stat collection. Both rebuild the index in
// an EffectManager.Hydrate postfix and clear it in a CombatGameState._Init postfix. On a load retail runs _Init after
// EffectManager.Hydrate (GameInstance.CreateCombatFromSave: HydrateCombat, then LaunchContractFromSave -> InitFromSave), so
// both indexes are empty while the hydrated effects are all still in EffectManager.effects. GetAllEffectsTargeting then
// returns only the effects added after the load, and CustomStatisticEffects applies a design mask's sticky effect a second
// time on top of the saved stat value (stack limit 1 counted against an empty index). RogueTechPerfFixes' Hydrate postfix
// also replaces its list for every effect, so even without the clear it would keep one effect per target.
// On a combat loading from a save, with combat saves on (the marker or a co-op combat), a postfix on _Init that runs after
// both mods' own puts each index back from EffectManager.effects, the list both mods index (as retail's
// GetAllEffectsTargeting reads it; expiringEffects is a per-tick scratch list): RogueTechPerfFixes' grouped by target, each
// effect appended; CustomStatisticEffects' by its own Hydrate postfix. No statistic is touched: retail's saved values and
// restored stat history stay as loaded. A mod whose _Init postfix is not installed (RogueTechPerfFixes' Patch.Vanilla off) is
// left alone. Run as LoadSteps' phase-2 step (the registry patches _Init once, after both mods' ids, and calls this for a combat loading
// from a save); without either mod the step is not installed and nothing is logged.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class EffectCacheLoadCompat
    {
        const string Tag = "effect-cache-load";
        public const string RtpfId = "RogueTechPerfFixes", CseId = "ru.kmission.customstatisticeffects";
        static Action<string> log;
        static bool prepared;
        static MethodInfo initMethod;
        static PropertyInfo managerProp;
        static FieldInfo effectsField, targetField;
        static FieldInfo rtpfCache;          // RogueTechPerfFixes.Patches.H_EffectManager._cache
        static MethodInfo cseHydratePostfix; // CustomStatisticEffects.EffectManager_Hydrate.Postfix(EffectManager, List<Effect>)
        static Type statisticEffectType;

        // LoadSteps phase 2 (after CombatGameState._Init on a load, after RTPF's and CSE's own _Init postfixes). The registry patches
        // _Init once for every step and passes only a combat that is loading from a save.
        public static void AfterInit(object combat, Action<string> logLine)
        {
            log = logLine;
            if (!prepared) { prepared = true; Prepare(); }
            if (initMethod == null) return;
            InitPostfix(combat);
        }

        // Reflection on the mods' types, at the first load: every mod has run its Init by now (a read of the field's value before it
        // would run H_EffectManager's type initializer too early).
        static void Prepare()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Assembly rtpf = ModStateCarry.FindAssembly("RogueTechPerfFixes");
            Assembly cse = ModStateCarry.FindAssembly("CustomStatisticEffects");
            FieldInfo cache = rtpf?.GetType("RogueTechPerfFixes.Patches.H_EffectManager", false)?.GetField("_cache", BindingFlags.Static | BindingFlags.NonPublic);
            if (rtpf != null && (cache == null || !typeof(IDictionary).IsAssignableFrom(cache.FieldType) || cache.FieldType.GetGenericArguments().Length != 2))
                log(Tag + "|skipped|RogueTechPerfFixes|shape changed _cache=" + (cache != null));
            else if (rtpf != null) { rtpfCache = cache; log(Tag + "|armed|RogueTechPerfFixes|" + rtpf.GetName().Version); }
            MethodInfo postfix = cse?.GetType("CustomStatisticEffects.EffectManager_Hydrate", false)?.GetMethod("Postfix", BindingFlags.Static | BindingFlags.Public);
            ParameterInfo[] ps = postfix?.GetParameters();
            if (cse != null && (ps == null || ps.Length != 2 || ps[0].ParameterType.FullName != "BattleTech.EffectManager" || !typeof(IList).IsAssignableFrom(ps[1].ParameterType)))
                log(Tag + "|skipped|CustomStatisticEffects|shape changed EffectManager_Hydrate.Postfix=" + (postfix != null));
            else if (cse != null) { cseHydratePostfix = postfix; log(Tag + "|armed|CustomStatisticEffects|" + cse.GetName().Version); }
            if (rtpfCache == null && cseHydratePostfix == null) return;
            Type combat = ModAssemblyHook.GameType("BattleTech.CombatGameState");
            Type manager = ModAssemblyHook.GameType("BattleTech.EffectManager");
            Type effect = ModAssemblyHook.GameType("BattleTech.Effect");
            statisticEffectType = ModAssemblyHook.GameType("BattleTech.StatisticEffect");
            MethodInfo init = combat?.GetMethod("_Init", inst | BindingFlags.DeclaredOnly);
            managerProp = combat?.GetProperty("EffectManager", inst);
            effectsField = manager?.GetField("effects", inst);
            targetField = effect?.GetField("target", inst);
            if (init == null || managerProp == null || effectsField == null || targetField == null || statisticEffectType == null)
            {
                log(Tag + "|skipped|shape changed _Init=" + (init != null) + " EffectManager=" + (managerProp != null) + " effects=" + (effectsField != null)
                    + " target=" + (targetField != null) + " StatisticEffect=" + (statisticEffectType != null));
                return;
            }
            initMethod = init;
        }

        static void InitPostfix(object __instance)
        {
            try
            {
                if (!CombatSavesRuntimeGate.SaveDataEnabled()) return;
                object manager = managerProp.GetValue(__instance, null);
                IList effects = manager == null ? null : (IList)effectsField.GetValue(manager);
                if (effects == null) { log(Tag + "|skipped|no hydrated EffectManager on the load"); return; }
                List<string> owners = InitPostfixOwners();
                if (rtpfCache != null)
                {
                    if (owners.Contains(RtpfId)) RebuildRtpf(effects);
                    else log(Tag + "|skipped|RogueTechPerfFixes|its _Init postfix is not installed");
                }
                if (cseHydratePostfix != null)
                {
                    if (owners.Contains(CseId)) RebuildCse(manager, effects);
                    else log(Tag + "|skipped|CustomStatisticEffects|its _Init postfix is not installed");
                }
            }
            catch (Exception ex) { log(Tag + "|FAILED|" + ex); }
        }

        static List<string> InitPostfixOwners()
        {
            var owners = new List<string>();
            Patches info = Harmony.GetPatchInfo(initMethod);
            if (info != null) foreach (Patch p in info.Postfixes) owners.Add(p.owner);
            return owners;
        }

        static void RebuildRtpf(IList effects)
        {
            try
            {
                var cache = (IDictionary)rtpfCache.GetValue(null);
                Type listType = rtpfCache.FieldType.GetGenericArguments()[1];
                cache.Clear();
                int indexed = 0, untargeted = 0;
                foreach (object e in effects)
                {
                    object target = e == null ? null : targetField.GetValue(e);
                    if (target == null) { untargeted++; continue; } // a null key would throw; retail's lookup never matches a null target either
                    var list = (IList)cache[target];
                    if (list == null) { list = (IList)Activator.CreateInstance(listType); cache[target] = list; }
                    list.Add(e);
                    indexed++;
                }
                log(Tag + "|rebuilt|RogueTechPerfFixes|targets=" + cache.Count + " effects=" + indexed + (untargeted > 0 ? " untargeted=" + untargeted : ""));
            }
            catch (Exception ex) { log(Tag + "|FAILED|RogueTechPerfFixes|" + ex); }
        }

        static void RebuildCse(object manager, IList effects)
        {
            try
            {
                cseHydratePostfix.Invoke(null, new[] { manager, effects });
                var targets = new Dictionary<object, bool>(new RefEq()); // mscorlib only: no HashSet
                int indexed = 0;
                foreach (object e in effects)
                {
                    if (e == null || !statisticEffectType.IsInstanceOfType(e)) continue;
                    indexed++;
                    object target = targetField.GetValue(e);
                    if (target != null) targets[target] = true;
                }
                log(Tag + "|rebuilt|CustomStatisticEffects|targets=" + targets.Count + " effects=" + indexed);
            }
            catch (Exception ex) { log(Tag + "|FAILED|CustomStatisticEffects|" + (ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex)); }
        }

        sealed class RefEq : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }
}
