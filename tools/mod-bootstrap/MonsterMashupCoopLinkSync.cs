// MonsterMashup linked actors at a co-op resync or rejoin load (gh issue 24 gap review, 2026-11-22: a gap that a resync landing on it does
// not degrade cleanly is fixed). MonsterMashup links turret "vehicles" to a monster or DropShip parent (BTA: Overlord, Union, Rattler,
// RattlerSL) only when the units spawn (UnitSpawnPointGameLogic.initializeActor, SpawnHelper.SpawnLinkedCUVehicle) and keeps the links in
// static ModState only; ModState.Reset at TurnDirector.OnCombatGameDestroyed empties it on a guest's reload, and nothing rebuilds it on a
// load. With an empty ParentState the guest, and not the host: lets linked turrets block their own parent's line of fire
// (LineOfSight.GetLineOfFireUncached), never spawns support units (Mech.OnActivationEnd), marks a FleeComponent parent as fleeing at once
// (MMASH_Flee_On_Round, then a MissionFailedMessage), does not restrict weapon firing arcs (WeaponAttachTransforms), and does not move a
// child with its parent (Mech.OnPositionUpdate; CustomUnits' CustomMech.linkedActors, also unsaved).
// The host sends, per parent: its GUID, the two linked lances' GUIDs, each child (GUID, attach transform name, its CustomUnits link:
// relative position and keepPosition), each support spawn config's SpawnCount, LastSpawnedRound and IsReady, and the spawned support
// units; plus the mechs whose weapons have an attach transform. The guest builds each ParentRelationships with the host's lances (an
// uninitialized object, so no new lance is made), finds the attach transform by name as MonsterMashup does (last match, ignoring case),
// re-runs MonsterMashup's own ProcessComponents (its support spawn configs, made with the parent's existing relationships) and
// LinkedEntityHelper.ProcessWeapons, writes the config counters, adds the CustomUnits links and ModState.Parents.
// A parent whose child, lance or transform cannot be found is left out whole with an `|unmapped|` row: a parent in ParentState with a child
// missing from LinkedActorTransforms would throw in MonsterMashup's OnPositionUpdate prefix.
// Not carried: ActorIds/ActorDistinctIds/NextActorId (DistinctId log names), FootprintVisuals (developer option), crush-radius scale (visual).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;

namespace BattleTechPort
{
    public static class MonsterMashupCoopLinkSync
    {
        const string Tag = "mm-coop";
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static Action<string> log;
        static FieldInfo parentState, parents, weaponTransforms;
        static FieldInfo prParent, prLinked, prTransforms, prLinkedLance, prConfigs, prSpawned, prSupportLance;
        static FieldInfo cfgCount, cfgLastRound, cfgReady;
        static MethodInfo processComponents, processWeapons, findCombatant, getComponents;
        static PropertyInfo battleTechGame;
        static Type prType, transformType, vector3Type;
        static CoopCarryCounter counter;
        static int rows;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("MonsterMashup", Apply, logLine, Tag);
        }

        static void Apply(Assembly mm)
        {
            try
            {
                const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                Type modState = mm.GetType("MonsterMashup.ModState", false);
                prType = mm.GetType("MonsterMashup.Helper.ParentRelationships", false);
                Type cfg = mm.GetType("MonsterMashup.Helper.SupportSpawnConfig", false);
                Type mech = ModAssemblyHook.GameType("BattleTech.Mech");
                parentState = modState?.GetField("ParentState", st);
                parents = modState?.GetField("Parents", st);
                weaponTransforms = modState?.GetField("WeaponAttachTransforms", st);
                prParent = prType?.GetField("parent", inst);
                prLinked = prType?.GetField("linkedActors", inst);
                prTransforms = prType?.GetField("linkedActorTransforms", inst);
                prLinkedLance = prType?.GetField("linkedActorsLance", inst);
                prConfigs = prType?.GetField("supportSpawnConfigs", inst);
                prSpawned = prType?.GetField("spawnedSupportActors", inst);
                prSupportLance = prType?.GetField("supportSpawnLance", inst);
                cfgCount = cfg?.GetField("SpawnCount", inst);
                cfgLastRound = cfg?.GetField("LastSpawnedRound", inst);
                cfgReady = cfg?.GetField("IsReady", inst);
                processComponents = mech == null ? null : mm.GetType("MonsterMashup.Patch.UnitSpawnPointGameLogic_initializeActor", false)?.GetMethod("ProcessComponents", st, null, new[] { mech }, null);
                processWeapons = mech == null ? null : mm.GetType("MonsterMashup.Helper.LinkedEntityHelper", false)?.GetMethod("ProcessWeapons", st, null, new[] { mech }, null);
                findCombatant = ModAssemblyHook.GameType("BattleTech.CombatGameState")?.GetMethod("FindCombatantByGUID", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(bool) }, null);
                battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", st);
                transformType = ModAssemblyHook.GameType("UnityEngine.Transform");
                vector3Type = ModAssemblyHook.GameType("UnityEngine.Vector3");
                getComponents = ModAssemblyHook.GameType("UnityEngine.Component")?.GetMethod("GetComponentsInChildren", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(Type) }, null);
                MethodInfo register = ModAssemblyHook.GameType("BattleTech.CoopInvocationSideData")?.GetMethod("Register", st);
                var missing = new List<string>();
                foreach (KeyValuePair<string, object> m in new Dictionary<string, object> {
                    { "ModState.ParentState", parentState }, { "ModState.Parents", parents }, { "ModState.WeaponAttachTransforms", weaponTransforms },
                    { "ParentRelationships", prType }, { "parent", prParent }, { "linkedActors", prLinked }, { "linkedActorTransforms", prTransforms },
                    { "linkedActorsLance", prLinkedLance }, { "supportSpawnConfigs", prConfigs }, { "spawnedSupportActors", prSpawned }, { "supportSpawnLance", prSupportLance },
                    { "SupportSpawnConfig.SpawnCount", cfgCount }, { "SupportSpawnConfig.LastSpawnedRound", cfgLastRound }, { "SupportSpawnConfig.IsReady", cfgReady },
                    { "UnitSpawnPointGameLogic_initializeActor.ProcessComponents(Mech)", processComponents }, { "LinkedEntityHelper.ProcessWeapons(Mech)", processWeapons },
                    { "CombatGameState.FindCombatantByGUID", findCombatant }, { "UnityGameInstance.BattleTechGame", battleTechGame }, { "UnityEngine.Transform", transformType },
                    { "UnityEngine.Vector3", vector3Type }, { "Component.GetComponentsInChildren(Type)", getComponents }, { "CoopInvocationSideData.Register", register } })
                    if (m.Value == null) missing.Add(m.Key);
                if (missing.Count > 0) { log(Tag + "|FAILED|shape changed, not found: " + string.Join(", ", missing.ToArray()) + " (needs UCU patch 166)"); return; }
                counter = CoopCarryCounter.For(Tag, log);
                register.Invoke(null, new object[] { Tag, new Func<object, string>(Capture), new Action<object, string>(ApplyData) });
                log(Tag + "|installed|MonsterMashup ParentState, Parents, WeaponAttachTransforms and CustomUnits links made again on a peer that loads a co-op snapshot");
            }
            catch (Exception e) { log(Tag + "|FAILED|" + ModStateCarry.Inner(e)); }
        }

        static object Combat() => ModStateCarry.Get(battleTechGame.GetValue(null, null), "Combat");
        static string Guid(object o) => ModStateCarry.Get(o, "GUID") as string ?? "";
        static string LanceGuid(object lance) => ModStateCarry.Get(lance, "lanceGuid") as string ?? "";
        static string F(object v, string axis) => Convert.ToSingle(ModStateCarry.Get(v, axis), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
        static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ');

        static object CuLink(object parent, object child)
        {
            foreach (object l in ModStateCarry.Get(parent, "linkedActors") as IEnumerable ?? new object[0])
                if (ReferenceEquals(ModStateCarry.Get(l, "actor"), child)) return l;
            return null;
        }

        // Host, at the peer's snapshot load.
        static string Capture(object invocation)
        {
            if (invocation != null) return null;
            try
            {
                if (Combat() == null) return null;
                var sb = new StringBuilder();
                var summary = new List<string>();
                foreach (DictionaryEntry e in (IDictionary)parentState.GetValue(null))
                {
                    object pr = e.Value;
                    var children = (IList)prLinked.GetValue(pr);
                    var transforms = (IDictionary)prTransforms.GetValue(pr);
                    var configs = (IList)prConfigs.GetValue(pr);
                    var spawned = (IList)prSpawned.GetValue(pr);
                    sb.Append("P\t").Append(Guid(e.Key)).Append('\t').Append(LanceGuid(prLinkedLance.GetValue(pr))).Append('\t').Append(LanceGuid(prSupportLance.GetValue(pr)))
                        .Append('\t').Append(configs.Count).Append('\n');
                    foreach (object c in children)
                    {
                        object t = transforms.Contains(c) ? transforms[c] : null;
                        object link = CuLink(e.Key, c);
                        object rel = link == null ? null : ModStateCarry.Get(link, "relativePosition");
                        sb.Append("C\t").Append(Guid(c)).Append('\t').Append(Clean(ModStateCarry.Get(t, "name") as string)).Append('\t').Append(link == null ? "0" : "1").Append('\t')
                            .Append(rel == null ? "0" : F(rel, "x")).Append('\t').Append(rel == null ? "0" : F(rel, "y")).Append('\t').Append(rel == null ? "0" : F(rel, "z")).Append('\t')
                            .Append(link != null && (bool)ModStateCarry.Get(link, "keepPosition") ? "1" : "0").Append('\n');
                    }
                    foreach (object s in configs)
                        sb.Append("S\t").Append((int)cfgCount.GetValue(s)).Append('\t').Append((int)cfgLastRound.GetValue(s)).Append('\t').Append((bool)cfgReady.GetValue(s) ? "1" : "0").Append('\n');
                    foreach (object a in spawned) sb.Append("A\t").Append(Guid(a)).Append('\n');
                    summary.Add(Guid(e.Key) + ":children=" + children.Count + ",configs=" + configs.Count + ",spawned=" + spawned.Count);
                }
                var owners = new Dictionary<string, bool>();
                foreach (object w in ((IDictionary)weaponTransforms.GetValue(null)).Keys) owners[Guid(ModStateCarry.Get(w, "parent"))] = true;
                foreach (string g in owners.Keys) sb.Append("W\t").Append(g).Append('\n');
                counter.Capture++;
                if (summary.Count > 0 || owners.Count > 0) counter.NonEmpty++;
                Row("capture", "parents=" + summary.Count + "|weapon-owners=" + owners.Count + "|" + string.Join(";", summary.ToArray()));
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
            }
            catch (Exception e) { Row("FAILED", "capture|" + ModStateCarry.Inner(e)); return null; }
        }

        sealed class Parent { public string Guid, LinkedLance, SupportLance; public int Configs; public List<string[]> Children = new List<string[]>(); public List<string[]> Spawns = new List<string[]>(); public List<string> Spawned = new List<string>(); }

        // Guest, after its snapshot load.
        static void ApplyData(object invocation, string data)
        {
            if (invocation != null || data == null) return;
            try
            {
                object combat = Combat();
                if (combat == null) return;
                var list = new List<Parent>();
                var weaponOwners = new List<string>();
                foreach (string line in Encoding.UTF8.GetString(Convert.FromBase64String(data)).Split('\n'))
                {
                    if (line.Length == 0) continue;
                    string[] p = line.Split('\t');
                    switch (p[0])
                    {
                        case "P": Need(p, 5); list.Add(new Parent { Guid = p[1], LinkedLance = p[2], SupportLance = p[3], Configs = int.Parse(p[4], CultureInfo.InvariantCulture) }); break;
                        case "C": Need(p, 8); Last(list).Children.Add(p); break;
                        case "S": Need(p, 4); Last(list).Spawns.Add(p); break;
                        case "A": Need(p, 2); Last(list).Spawned.Add(p[1]); break;
                        case "W": Need(p, 2); weaponOwners.Add(p[1]); break;
                        default: throw new FormatException("unknown line " + p[0]);
                    }
                }
                var state = (IDictionary)parentState.GetValue(null);
                Row("before", "parents=" + state.Count + "|weapon-transforms=" + ((IDictionary)weaponTransforms.GetValue(null)).Count);
                state.Clear();
                ((IDictionary)weaponTransforms.GetValue(null)).Clear();
                parents.FieldType.GetMethod("Clear").Invoke(parents.GetValue(null), null);
                int built = 0;
                foreach (Parent p in list)
                    if (Build(combat, p, state)) built++;
                int weapons = 0;
                foreach (string g in weaponOwners)
                {
                    object m = findCombatant.Invoke(combat, new object[] { g, false });
                    if (m == null || !processWeapons.GetParameters()[0].ParameterType.IsInstanceOfType(m)) { Unmapped("weapon-owner", g); continue; }
                    try { processWeapons.Invoke(null, new[] { m }); weapons++; }
                    catch (Exception e) { Row("apply-failed", "weapons|" + g + "|" + ModStateCarry.Inner(e)); }
                }
                counter.Apply++;
                if (built > 0 || weapons > 0) counter.NonEmpty++;
                Row("apply", "parents=" + built + "/" + list.Count + "|weapon-owners=" + weapons + "/" + weaponOwners.Count + "|weapon-transforms=" + ((IDictionary)weaponTransforms.GetValue(null)).Count);
            }
            catch (Exception e) { Row("apply-failed", ModStateCarry.Inner(e)); }
        }

        static void Need(string[] p, int n) { if (p.Length != n) throw new FormatException(p[0] + " line has " + p.Length + " fields, not " + n); }
        static Parent Last(List<Parent> l) { if (l.Count == 0) throw new FormatException("child line before a parent"); return l[l.Count - 1]; }

        // Resolves everything first; a parent is added to ParentState only when all of it was found.
        static bool Build(object combat, Parent p, IDictionary state)
        {
            object parent = findCombatant.Invoke(combat, new object[] { p.Guid, false });
            if (parent == null) { Unmapped("parent", p.Guid); return false; }
            object team = ModStateCarry.Get(parent, "team");
            object linkedLance = Lance(team, p.LinkedLance), supportLance = Lance(team, p.SupportLance);
            if (linkedLance == null || supportLance == null) { Unmapped("lance", p.Guid + "|" + (linkedLance == null ? p.LinkedLance : p.SupportLance)); return false; }
            object rep = ModStateCarry.Get(parent, "GameRep");
            Array all = rep == null ? null : getComponents.Invoke(rep, new object[] { transformType }) as Array;
            if (all == null) { Unmapped("parent-gamerep", p.Guid); return false; }
            var children = new List<object[]>();
            foreach (string[] c in p.Children)
            {
                object child = findCombatant.Invoke(combat, new object[] { c[1], false });
                object t = null;
                foreach (object x in all) if (string.Equals(ModStateCarry.Get(x, "name") as string, c[2], StringComparison.InvariantCultureIgnoreCase)) t = x;
                if (child == null || t == null) { Unmapped(child == null ? "child" : "attach-transform", p.Guid + "|" + c[1] + "|" + c[2]); return false; }
                children.Add(new[] { child, t, c });
            }
            var spawned = new List<object>();
            foreach (string g in p.Spawned)
            {
                object a = findCombatant.Invoke(combat, new object[] { g, false });
                if (a == null) Unmapped("support-actor", p.Guid + "|" + g);
                else spawned.Add(a);
            }
            object pr = FormatterServices.GetUninitializedObject(prType);
            prParent.SetValue(pr, parent);
            prLinked.SetValue(pr, Activator.CreateInstance(prLinked.FieldType));
            prTransforms.SetValue(pr, Activator.CreateInstance(prTransforms.FieldType));
            prConfigs.SetValue(pr, Activator.CreateInstance(prConfigs.FieldType));
            prSpawned.SetValue(pr, Activator.CreateInstance(prSpawned.FieldType));
            prLinkedLance.SetValue(pr, linkedLance);
            prSupportLance.SetValue(pr, supportLance);
            foreach (object[] c in children)
            {
                ((IList)prLinked.GetValue(pr)).Add(c[0]);
                ((IDictionary)prTransforms.GetValue(pr)).Add(c[0], c[1]);
            }
            foreach (object a in spawned) ((IList)prSpawned.GetValue(pr)).Add(a);
            state[parent] = pr;
            // MonsterMashup's own support spawn configs, built on the relationships just added (so it makes no new lance).
            if (p.Configs > 0)
            {
                if (!processComponents.GetParameters()[0].ParameterType.IsInstanceOfType(parent)) Unmapped("spawn-parent-not-mech", p.Guid);
                else
                {
                    try { processComponents.Invoke(null, new[] { parent }); }
                    catch (Exception e) { Row("apply-failed", "spawn-configs|" + p.Guid + "|" + ModStateCarry.Inner(e)); }
                    var configs = (IList)prConfigs.GetValue(pr);
                    if (configs.Count != p.Spawns.Count) Unmapped("spawn-configs", p.Guid + "|host=" + p.Spawns.Count + "|here=" + configs.Count);
                    for (int i = 0; i < configs.Count && i < p.Spawns.Count; i++)
                    {
                        cfgCount.SetValue(configs[i], int.Parse(p.Spawns[i][1], CultureInfo.InvariantCulture));
                        cfgLastRound.SetValue(configs[i], int.Parse(p.Spawns[i][2], CultureInfo.InvariantCulture));
                        cfgReady.SetValue(configs[i], p.Spawns[i][3] == "1");
                    }
                }
            }
            parents.FieldType.GetMethod("Add").Invoke(parents.GetValue(null), new[] { parent });
            // CustomUnits' links (moved with the parent by CustomMech.OnPositionUpdate).
            MethodInfo addLink = parent.GetType().GetMethod("AddLinkedActor", BindingFlags.Instance | BindingFlags.Public, null,
                new[] { ModAssemblyHook.GameType("BattleTech.AbstractActor"), vector3Type, typeof(bool) }, null);
            foreach (object[] c in children)
            {
                string[] f = (string[])c[2];
                if (f[3] != "1" || CuLink(parent, c[0]) != null) continue;
                if (addLink == null) { Unmapped("cu-link", p.Guid + "|" + f[1]); continue; }
                object v = Activator.CreateInstance(vector3Type, float.Parse(f[4], CultureInfo.InvariantCulture), float.Parse(f[5], CultureInfo.InvariantCulture), float.Parse(f[6], CultureInfo.InvariantCulture));
                addLink.Invoke(parent, new[] { c[0], v, f[7] == "1" });
            }
            return true;
        }

        static object Lance(object team, string guid)
        {
            foreach (object l in ModStateCarry.Get(team, "lances") as IEnumerable ?? new object[0])
                if (LanceGuid(l) == guid) return l;
            return null;
        }

        static void Unmapped(string what, string key) => Row("unmapped", what + "|" + key);

        static void Row(string kind, string text)
        {
            if (++rows > 300) return;
            object combat = Combat();
            string at = combat == null ? "?" : ModStateCarry.Get(ModStateCarry.Get(combat, "TurnDirector"), "CurrentRound") + "/" + ModStateCarry.Get(ModStateCarry.Get(combat, "TurnDirector"), "CurrentPhase");
            log(Tag + "|" + kind + "|" + text + "|at=" + at);
        }
    }
}
