// PracticeMakesPerfect's active StratCom on the co-op guest (gh issue 24, fault A; .hermes/reports/issue24-faultA-officertrainee-read-1.md).
// PMP applies the effects of its active StratCom ("StrategicResolve" = StatusEffect-OfficerTrainee, +2 resolve) to every local-team unit at
// TurnDirector.StartFirstRound when the commander is not deployed. Its holder (SpecHolder.HolderInstance.activeStratCom) starts null and is filled
// only by a SimGameState.Rehydrate postfix: DeserializeSpecState reads the company tag PMP_ACTIVESTRATCOM_<json> the host's Dehydrate wrote, and
// a null or empty result is replaced by the first StratCom. A host that never went through Rehydrate has a null holder and applies nothing; its
// snapshot carries PMP_ACTIVESTRATCOM_null, the guest's Rehydrate defaults it to StrategicResolve, and the guest alone adds the effect (and the
// MoraleBonusGain stat) to its units, so the MECHS compare fails in round 1.
// This adapter, only when an assembly named PracticeMakesPerfect is loaded and only while this peer is a campaign client (NetworkGameInstance
// .IsCampaignClient), puts the host's value back after PMP's own postfix: a Priority.First postfix on SimGameState.Rehydrate captures the tag
// before PMP strips it, a Priority.Last postfix writes the decoded value (null stays null) into the holder. IsCampaignClient is true for both ways a
// guest hydrates the host's snapshot: the first load (CampaignSessionClient load-start, LoadTransitioning to GameInstance.Load to Rehydrate) and every
// later SimGameSnapshotApply.Apply. The host, single player and a game without PMP are untouched.
using System;
using System.Collections;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace BattleTechPort
{
    public static class PracticeMakesPerfectCoopStratCom
    {
        const string OwnId = "battletech.port.pmp-coop-stratcom";
        const string TagPrefix = "PMP_ACTIVESTRATCOM_";
        static Action<string> log;
        static PropertyInfo holderProp;   // SpecHolder.HolderInstance (static)
        static FieldInfo activeField;     // SpecHolder.activeStratCom
        static bool captured;
        static string capturedJson;
        static int logged;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("PracticeMakesPerfect", Apply, logLine, "pmp-coop-stratcom");
        }

        static void Apply(Assembly pmp)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type holder = pmp.GetType("PracticeMakesPerfect.Framework.SpecHolder", false);
            holderProp = holder?.GetProperty("HolderInstance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            activeField = holder?.GetField("activeStratCom", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Type sim = ModAssemblyHook.GameType("BattleTech.SimGameState");
            MethodInfo rehydrate = null;
            try { rehydrate = sim?.GetMethod("Rehydrate", all, null, new[] { ModAssemblyHook.GameType("BattleTech.Save.GameInstanceSave") ?? typeof(object) }, null); }
            catch (Exception) { }
            bool companyTags = sim?.GetProperty("CompanyTags", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
            if (holderProp == null || activeField == null || activeField.FieldType != typeof(string) || rehydrate == null || !companyTags
                || ModAssemblyHook.GameType("BattleTech.UnityGameInstance") == null)
            {
                log("pmp-coop-stratcom|skipped|shape changed SpecHolder.HolderInstance=" + (holderProp != null) + " activeStratCom=" + (activeField != null)
                    + " SimGameState.Rehydrate=" + (rehydrate != null) + " CompanyTags=" + companyTags);
                return;
            }
            Harmony harmony = new Harmony(OwnId);
            harmony.Patch(rehydrate,
                postfix: new HarmonyMethod(typeof(PracticeMakesPerfectCoopStratCom).GetMethod(nameof(CapturePostfix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
            harmony.Patch(rehydrate,
                postfix: new HarmonyMethod(typeof(PracticeMakesPerfectCoopStratCom).GetMethod(nameof(MirrorPostfix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
            log("pmp-coop-stratcom|installed|host's PMP_ACTIVESTRATCOM_ tag restored after PMP's Rehydrate postfix on a campaign client");
        }

        // Runs before PMP's Rehydrate postfix, which removes the tag from the company tags.
        static void CapturePostfix(object __instance)
        {
            captured = false;
            capturedJson = null;
            try
            {
                if (!IsCampaignClient()) return;
                if (!(Get(__instance, "CompanyTags") is IEnumerable tags)) return;
                foreach (object o in tags)
                {
                    if (o is string s && s.StartsWith(TagPrefix, StringComparison.Ordinal))
                    {
                        capturedJson = s.Substring(TagPrefix.Length);
                        captured = true;
                        return;
                    }
                }
                if (++logged <= 8) log("pmp-coop-stratcom|no-tag|snapshot carries no " + TagPrefix + "*; PMP's own default stays");
            }
            catch (Exception ex) { log("pmp-coop-stratcom|capture failed|" + ex.Message); }
        }

        // Runs after PMP's Rehydrate postfix.
        static void MirrorPostfix()
        {
            if (!captured) return;
            string json = capturedJson;
            captured = false;
            capturedJson = null;
            try
            {
                if (!TryDecode(json, out string host))
                {
                    if (++logged <= 8) log("pmp-coop-stratcom|skipped|tag value not understood|" + json);
                    return;
                }
                object holder = holderProp.GetValue(null, null);
                if (holder == null) return;
                string was = activeField.GetValue(holder) as string;
                if (was != host) activeField.SetValue(holder, host);
                if (++logged <= 8) log("pmp-coop-stratcom|mirrored|host=" + (host ?? "null") + "|pmp-default-was=" + (was ?? "null"));
            }
            catch (Exception ex) { log("pmp-coop-stratcom|mirror failed|" + ex.Message); }
        }

        static bool IsCampaignClient()
        {
            object game = Get(ModAssemblyHook.GameType("BattleTech.UnityGameInstance"), "BattleTechGame");
            object net = Get(game, "NetworkGameInstance");
            return net != null && Get(net, "IsCampaignClient") is bool b && b;
        }

        // PMP stores JsonConvert.SerializeObject(string): the literal null, or a quoted string.
        static bool TryDecode(string json, out string value)
        {
            value = null;
            if (json == null) return false;
            json = json.Trim();
            if (json == "null") return true;
            if (json.Length < 2 || json[0] != '"' || json[json.Length - 1] != '"') return false;
            StringBuilder sb = new StringBuilder();
            for (int i = 1; i < json.Length - 1; i++)
            {
                char c = json[i];
                if (c == '\\')
                {
                    if (++i >= json.Length - 1) return false;
                    c = json[i];
                    if (c != '"' && c != '\\' && c != '/') return false;
                }
                else if (c == '"') return false;
                sb.Append(c);
            }
            value = sb.ToString();
            return true;
        }

        // Instance members of o, or the static members of o when o is a Type.
        static object Get(object o, string name)
        {
            if (o == null) return null;
            Type start = o as Type;
            BindingFlags f = (start != null ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic;
            for (Type t = start ?? o.GetType(); t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, f | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(start != null ? null : o, null);
                FieldInfo fi = t.GetField(name, f | BindingFlags.DeclaredOnly);
                if (fi != null) return fi.GetValue(start != null ? null : o);
            }
            return null;
        }
    }
}
