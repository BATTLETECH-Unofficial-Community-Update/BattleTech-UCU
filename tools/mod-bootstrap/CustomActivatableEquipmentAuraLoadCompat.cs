// CustomActivatableEquipment aura effects after a combat load (mod support, gh issue 24 fault W, C3 part;
// .hermes/reports/issue24-faultW-read-1.md Q3). CustomActivatableEquipment remembers which aura effects a unit holds in
// AuraActorBody.affectedAurasEffects, a per-combat MonoBehaviour field that starts empty (a new body per unit at combat init) and is
// not in a save. A load restores the aura effects themselves (EffectManager hydrate, ids AuraEffect_<owner>_<target>_<aura>_<desc>_<i>),
// so the first trigger enter after it finds the aura missing from the body and ApplyAuraEffects creates every effect again. Aura data
// uses stackLimit -1, which retail's CreateEffect does not guard, so both copies apply: the co-op guest's C3 slaves went from -1 to -2
// ADD_ENCOUNTER_TAG_C3_Network after a resync load (B-L3-fix-3), and a single-player box load doubled a Guardian ECM self aura
// (U-fix-targets: GECMInterference x1 before save, x2 after load).
// The gate is CombatGameState.WasFromSave (set by Hydrate, true for the whole loaded combat), not IsLoadingFromSave: TurnDirector
// .OnEncounterBegin clears IsLoadingFromSave, and an ally aura such as the C3 slave's applies seconds after Encounter Begin
// (W-L1 guest: 1.3-5 s after each resync's Encounter Begin, 0 adopted rows; .hermes/reports/issue24-faultX-read-1.md). A live
// exit cancels every copy of an id, so outside the load's leftovers there is nothing for the prefix to adopt.
// This adapter, only when an assembly named CustomActivatableEquipment is loaded, on a combat loaded from a save while combat saves
// are on (CombatSavesRuntimeGate.SaveDataEnabled: the marker or a co-op combat), runs a prefix on AuraActorBody.ApplyAuraEffects ahead
// of it: each effect id the call would create that a live effect on this body's unit already carries is put into the body's own
// bookkeeping (affectedAurasEffects[aura], aura.affectedTo), as ApplyAuraEffects itself does, so its own check skips the second
// CreateEffect. No effect, statistic or registry is written; CustomActivatableEquipment's RemoveAuraEffects and ReapplyAllEffects then
// cancel the restored copy by id as they would their own. The patch is installed at the first combat (ModAssemblyHook.AtFirstCombat),
// after CustomActivatableEquipment's Init, so none of its type initializers runs early (fault M). A new mission is untouched.
using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace BattleTechPort
{
    public static class CustomActivatableEquipmentAuraLoadCompat
    {
        const string OwnId = "battletech.port.cae-aura-load";
        const string Tag = "cae-aura-load";
        static Action<string> log;
        static Assembly cae;
        static MethodInfo withId;   // EffectManager.GetAllEffectsWithID(string)
        static int rows;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("CustomActivatableEquipment", a => { cae = a; ModAssemblyHook.AtFirstCombat(Apply, logLine, Tag); }, logLine, Tag);
        }

        static void Apply()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type body = cae.GetType("CustomActivatableEquipment.AuraActorBody", false);
            Type bubble = cae.GetType("CustomActivatableEquipment.AuraBubble", false);
            MethodInfo apply = bubble == null ? null : body?.GetMethod("ApplyAuraEffects", inst, null, new[] { bubble, typeof(bool) }, null);
            withId = ModAssemblyHook.GameType("BattleTech.EffectManager")?.GetMethod("GetAllEffectsWithID", inst, null, new[] { typeof(string) }, null);
            if (apply == null || withId == null)
            {
                log(Tag + "|skipped|shape changed AuraActorBody.ApplyAuraEffects=" + (apply != null) + " EffectManager.GetAllEffectsWithID=" + (withId != null));
                return;
            }
            var prefix = new HarmonyMethod(typeof(CustomActivatableEquipmentAuraLoadCompat).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
            new Harmony(OwnId).Patch(apply, prefix: prefix);
            log(Tag + "|patched|" + cae.GetName().Version + "|AuraActorBody.ApplyAuraEffects on a load: restored aura effects adopted before it creates them");
        }

        static void Prefix(object __instance, object aura)
        {
            object owner = Get(__instance, "owner");
            try
            {
                object combat = Get(owner, "Combat");
                if (!(Get(combat, "WasFromSave") is bool fromSave) || !fromSave || !CombatSavesRuntimeGate.SaveDataEnabled()) return;
                object def = Get(aura, "Def");
                IList effects = Get(def, "statusEffects") as IList;
                IDictionary held = Get(__instance, "affectedAurasEffects") as IDictionary;
                object manager = Get(combat, "EffectManager");
                if (effects == null || held == null || manager == null) return;
                string auraOwner = Get(Get(aura, "owner"), "GUID") as string, target = Get(owner, "GUID") as string, auraId = Get(def, "Id") as string;
                object ids = held.Contains(aura) ? held[aura] : null;
                int adopted = 0;
                for (int i = 0; i < effects.Count; i++)
                {
                    // CustomActivatableEquipment's own id (AuraActorBody.ApplyAuraEffects).
                    string id = string.Format("AuraEffect_{0}_{1}_{2}_{3}_{4}", auraOwner, target, auraId, Get(Get(effects[i], "Description"), "Id"), i);
                    if (ids != null && (bool)Call(ids, "Contains", id)) continue;
                    bool live = false;
                    foreach (object e in (withId.Invoke(manager, new object[] { id }) as IEnumerable) ?? new object[0])
                        if (ReferenceEquals(Get(e, "Target"), owner)) { live = true; break; }
                    if (!live) continue;
                    if (ids == null)
                    {
                        ids = Activator.CreateInstance(held.GetType().GetGenericArguments()[1]);
                        held.Add(aura, ids);
                        Call(Get(aura, "affectedTo"), "Add", __instance);
                    }
                    Call(ids, "Add", id);
                    adopted++;
                }
                if (adopted > 0 && ++rows <= 80) log(Tag + "|adopted|" + target + "|" + auraId + "|" + adopted);
            }
            catch (Exception ex) { log(Tag + "|prefix-failed|" + Get(owner, "GUID") + "|" + (ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex).Message); }
        }

        static object Call(object o, string name, object arg)
        {
            foreach (MethodInfo m in o.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                if (m.Name == name && m.GetParameters().Length == 1) return m.Invoke(o, new[] { arg });
            throw new MissingMethodException(o.GetType().Name, name);
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
