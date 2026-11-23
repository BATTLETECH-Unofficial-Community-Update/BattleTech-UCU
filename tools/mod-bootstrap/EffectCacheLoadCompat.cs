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
// left alone. Each mod is handled only when its assembly loads; without either, nothing is patched and nothing is logged.
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
        const string RtpfId = "RogueTechPerfFixes", CseId = "ru.kmission.customstatisticeffects";
        static Action<string> log;
        static bool patched;
        static MethodInfo initMethod;
        static PropertyInfo loadingProp, managerProp;
        static FieldInfo effectsField, targetField;
        static FieldInfo rtpfCache;          // RogueTechPerfFixes.Patches.H_EffectManager._cache
        static MethodInfo cseHydratePostfix; // CustomStatisticEffects.EffectManager_Hydrate.Postfix(EffectManager, List<Effect>)
        static Type statisticEffectType;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("RogueTechPerfFixes", ApplyRtpf, logLine, Tag);
            ModAssemblyHook.When("CustomStatisticEffects", ApplyCse, logLine, Tag);
        }

        static void ApplyRtpf(Assembly mod)
        {
            // Reflection only: reading the field's value would run H_EffectManager's type initializer before ModTek calls the mod's Init.
            FieldInfo cache = mod.GetType("RogueTechPerfFixes.Patches.H_EffectManager", false)?.GetField("_cache", BindingFlags.Static | BindingFlags.NonPublic);
            if (cache == null || !typeof(IDictionary).IsAssignableFrom(cache.FieldType) || cache.FieldType.GetGenericArguments().Length != 2)
            {
                log(Tag + "|skipped|RogueTechPerfFixes|shape changed _cache=" + (cache != null));
                return;
            }
            if (!PatchInit()) return;
            rtpfCache = cache;
            log(Tag + "|armed|RogueTechPerfFixes|" + mod.GetName().Version);
        }

        static void ApplyCse(Assembly mod)
        {
            MethodInfo postfix = mod.GetType("CustomStatisticEffects.EffectManager_Hydrate", false)?.GetMethod("Postfix", BindingFlags.Static | BindingFlags.Public);
            ParameterInfo[] ps = postfix?.GetParameters();
            if (ps == null || ps.Length != 2 || ps[0].ParameterType.FullName != "BattleTech.EffectManager" || !typeof(IList).IsAssignableFrom(ps[1].ParameterType))
            {
                log(Tag + "|skipped|CustomStatisticEffects|shape changed EffectManager_Hydrate.Postfix=" + (postfix != null));
                return;
            }
            if (!PatchInit()) return;
            cseHydratePostfix = postfix;
            log(Tag + "|armed|CustomStatisticEffects|" + mod.GetName().Version);
        }

        // One postfix on CombatGameState._Init serves both mods; it runs after their own _Init postfixes (HarmonyAfter on their ids).
        static bool PatchInit()
        {
            if (patched) return true;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type combat = ModAssemblyHook.GameType("BattleTech.CombatGameState");
            Type manager = ModAssemblyHook.GameType("BattleTech.EffectManager");
            Type effect = ModAssemblyHook.GameType("BattleTech.Effect");
            statisticEffectType = ModAssemblyHook.GameType("BattleTech.StatisticEffect");
            initMethod = combat?.GetMethod("_Init", inst | BindingFlags.DeclaredOnly);
            loadingProp = combat?.GetProperty("IsLoadingFromSave", inst);
            managerProp = combat?.GetProperty("EffectManager", inst);
            effectsField = manager?.GetField("effects", inst);
            targetField = effect?.GetField("target", inst);
            if (initMethod == null || loadingProp == null || managerProp == null || effectsField == null || targetField == null || statisticEffectType == null)
            {
                log(Tag + "|skipped|shape changed _Init=" + (initMethod != null) + " IsLoadingFromSave=" + (loadingProp != null) + " EffectManager="
                    + (managerProp != null) + " effects=" + (effectsField != null) + " target=" + (targetField != null) + " StatisticEffect=" + (statisticEffectType != null));
                return false;
            }
            var postfix = new HarmonyMethod(typeof(EffectCacheLoadCompat).GetMethod(nameof(InitPostfix), BindingFlags.Static | BindingFlags.NonPublic))
            {
                after = new[] { RtpfId, CseId }
            };
            new Harmony("battletech.port.effect-cache-load").Patch(initMethod, postfix: postfix);
            patched = true;
            log(Tag + "|patched|CombatGameState._Init postfix after " + RtpfId + ", " + CseId);
            return true;
        }

        static void InitPostfix(object __instance)
        {
            try
            {
                if (!(bool)loadingProp.GetValue(__instance, null) || !CombatSavesRuntimeGate.SaveDataEnabled()) return;
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
