// CustomAmmoCategories' minefields and temporary design masks after a co-op resync or rejoin load (gh issue 24, snapshot-field sweep row
// "CAC DynamicMapHelper"; Sam 2026-11-22: fix the not-carried class before 4B).
// CustomAmmoCategories keeps its battlefield changes in its own map objects, not in the save: each MapTerrainHexCell's MineFields
// (DynamicMapHelper.hexGrid, by hex centre) and each MapTerrainDataCellEx's tempDesignMaskCounters (the cells in DynamicMapHelper.tempMaskCells),
// whose combined mask MapMetaData.GetPriorityDesignMask returns (CAC postfix MapMetaData_GetPriorityDesignMask). A combat's destruction
// clears them (CombatGameState_OnCombatGameDestroyed prefix: DynamicMapHelper.ClearTerrain), so a guest's reload loses every minefield and
// every smoke/oil/temporary mask while the host, which never reloads, keeps them. Both are written during play only in attack and move
// resolution, which runs on every peer with seeded draws (CustomAmmoCategoriesCoopImpact: the MineField constructor, mines on a move), so only
// the reload loses them and nothing is sent with an invocation.
// This adapter, only when an assembly named CustomAmmoCategories is loaded, registers a source with UCU's co-op side channel (patch 166).
// The host's full state for a peer that has just loaded is:
//  - per hex with minefields: its centre and, per MineField, the ammunition whose MineFieldDef it is (found by reference in CAC's
//    ExtAmmunitionDef registry), count, IFFLevel, stealthLvl, UIName, owner GUID, weapon uid and team GUID;
//  - per temp-mask cell: x, y, each (mask id, rounds left) in the host's dictionary order, and the resulting combined mask id.
// The guest replaces its own: a MineField is made without its constructor (which rolls misfires on UnityEngine.Random) with the host's
// fields, and added through CAC's own MapTerrainHexCell.AddMineFieldSync (indicator object included); each cell's counters are cleared and
// re-added in the host's order through CAC's own MapTerrainDataCellEx.AddDesignMask, which rebuilds the combined mask (CreateMask finds it
// in the DataManager by id or makes it); cells the host has no mask on are cleared (ReconstructTempDesignMask).
// Fires (issue 24 gap review, 2026-11-22: "host decides, carry the result"): the result is carried, not the roll. A fire's start and spread
// are seeded on both peers (CustomAmmoCategoriesCoopImpact `cacfire|...`: TryBurnCellSync, FireTick) and land in the same FIFO order (one
// request per frame, DyncamicMapAsyncProcessor.Update), so normal play agrees; a guest's reload loses every fire (ClearTerrain), and burning
// cells add heat to a unit that stands in or moves through them (CAC.cs:4217-4231, 40216, 40400), so the next compare would differ. A
// postfix on MapTerrainDataCellEx.burn (installed at the first combat, after CAC's Init) records every hex a fire touched; the host sends,
// per touched hex: centre, burnEffectCounter, isHasForest, wasHasForest, expandingThisTurn and the burning weapon, and per cell of it: x, y,
// terrainMask, BurningCounter, BurningStrength, the burning weapon, wasForest, wasCustom, wasRoad and the CustomDesignMask id; plus
// DynamicMapHelper.burningHexes in the host's order. The guest writes those fields before the minefields and the temporary masks (whose
// combined mask is built on the cell's base mask), puts burningHexes in the host's order and restarts a burning hex's fire effect
// (applyBurnVisuals). Not made again: deleted trees, scorch decals and minimap marks (visual).
// Rows: host `cac-map-coop|capture|hexes=<n>|mines=<guid-free list>|cells=<n>|masks=...|at=full`, guest `cac-map-coop|before|...` and
// `cac-map-coop|apply|...` in the same format, `cac-map-coop|unmapped|<what>|<key>`, `cac-map-coop|mask-differs|<x>,<y>|host=..|guest=..`,
// `|installed|`, `|FAILED|`, `|apply-failed|`, and `cac-map-coop|count|...` at each combat's end.
// shortcut: a third peer's held order and the full state share the host's one relay order, as for patch 166's other sources.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;

namespace BattleTechPort
{
    public static class CacDynamicMapCoopSync
    {
        const string Tag = "cac-map-coop";
        static Action<string> log;
        static CoopCarryCounter counter;
        static PropertyInfo battleTechGame;
        static FieldInfo hexGrid, tempMaskCells, loadedMasks, extAmmoRegistry, hexMineFields, hexCenter, cellCounters, cellTempMask, pointX, pointY;
        static FieldInfo mfCount, mfUIName, mfOwner, mfWeapon, mfDef, mfTeam, mfIFF, mfStealth, mfHex;
        static MethodInfo addMineField, updateIndicator, addDesignMask, reconstruct, findExtAmmo, findCombatant;
        static PropertyInfo extMineField;
        static Type mineFieldType;
        static int rows;
        // Fires.
        static FieldInfo burningHexes, hexTerrainCells, hexBurnCounter, hexHasForest, hexWasForest, hexExpanding, hexBurnWeapon, hexCombat, cellHex, cellX, cellY,
            cellTerrainMask, cellBurnCounter, cellBurnStrength, cellBurnWeapon, cellWasForest, cellWasCustom, cellWasRoad, cellCustomMask;
        static MethodInfo burnVisuals;
        static readonly Dictionary<object, bool> firedHexes = new Dictionary<object, bool>(new SameObject());
        static object firedCombat;

        public static void Register(Action<string> logLine)
        {
            log = logLine;
            ModAssemblyHook.When("CustomAmmoCategories", Apply, logLine, Tag);
        }

        static void Apply(Assembly cac)
        {
            try
            {
                const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type dmh = cac.GetType("CustAmmoCategories.DynamicMapHelper", false);
                Type hex = cac.GetType("CustAmmoCategories.MapTerrainHexCell", false);
                Type cell = cac.GetType("CustAmmoCategories.MapTerrainDataCellEx", false);
                Type point = cac.GetType("CustAmmoCategories.MapPoint", false);
                Type main = cac.GetType("CustAmmoCategories.CustomAmmoCategories", false);
                Type ext = cac.GetType("CustAmmoCategories.ExtAmmunitionDef", false);
                mineFieldType = cac.GetType("CustAmmoCategories.MineField", false);
                hexGrid = dmh?.GetField("hexGrid", st);
                tempMaskCells = dmh?.GetField("tempMaskCells", st);
                loadedMasks = dmh?.GetField("loadedMasksDef", st);
                extAmmoRegistry = main?.GetField("ExtAmmunitionDef", st);
                findExtAmmo = main?.GetMethod("findExtAmmo", st, null, new[] { typeof(string) }, null);
                extMineField = ext?.GetProperty("MineField", inst);
                hexMineFields = hex?.GetField("MineFields", inst);
                hexCenter = hex?.GetField("center", inst);
                addMineField = mineFieldType == null ? null : hex?.GetMethod("AddMineFieldSync", inst, null, new[] { mineFieldType }, null);
                updateIndicator = hex?.GetMethod("UpdateIndicatorVisibility", inst, null, Type.EmptyTypes, null);
                cellCounters = cell?.GetField("tempDesignMaskCounters", inst);
                cellTempMask = cell?.GetField("tempDesignMask", inst);
                Type maskDef = ModAssemblyHook.GameType("BattleTech.DesignMaskDef");
                addDesignMask = maskDef == null ? null : cell?.GetMethod("AddDesignMask", inst, null, new[] { maskDef, typeof(int) }, null);
                reconstruct = cell?.GetMethod("ReconstructTempDesignMask", inst, null, Type.EmptyTypes, null);
                burningHexes = dmh?.GetField("burningHexes", st);
                hexTerrainCells = hex?.GetField("terrainCells", inst);
                hexBurnCounter = hex?.GetField("burnEffectCounter", inst);
                hexHasForest = hex?.GetField("isHasForest", inst);
                hexWasForest = hex?.GetField("wasHasForest", inst);
                hexExpanding = hex?.GetField("expandingThisTurn", inst);
                hexBurnWeapon = hex?.GetField("burningWeapon", inst);
                hexCombat = hex?.GetField("Combat", inst);
                burnVisuals = hex?.GetMethod("applyBurnVisuals", inst, null, Type.EmptyTypes, null);
                cellHex = cell?.GetField("hexCell", inst);
                cellX = cell?.GetField("x", inst);
                cellY = cell?.GetField("y", inst);
                cellTerrainMask = ModAssemblyHook.GameType("BattleTech.MapTerrainDataCell")?.GetField("terrainMask", inst);
                cellBurnCounter = cell?.GetField("BurningCounter", inst);
                cellBurnStrength = cell?.GetField("BurningStrength", inst);
                cellBurnWeapon = cell?.GetField("BurningWeapon", inst);
                cellWasForest = cell?.GetField("wasForest", inst);
                cellWasCustom = cell?.GetField("wasCustom", inst);
                cellWasRoad = cell?.GetField("wasRoad", inst);
                cellCustomMask = cell?.GetField("CustomDesignMask", inst);
                Type weaponT = ModAssemblyHook.GameType("BattleTech.Weapon");
                MethodInfo burn = weaponT == null ? null : cell?.GetMethod("burn", inst, null, new[] { weaponT, typeof(int), typeof(int) }, null);
                pointX = point?.GetField("x", inst);
                pointY = point?.GetField("y", inst);
                mfCount = mineFieldType?.GetField("count", inst);
                mfUIName = mineFieldType?.GetField("UIName", inst);
                mfOwner = mineFieldType?.GetField("owner", inst);
                mfWeapon = mineFieldType?.GetField("weapon", inst);
                mfDef = mineFieldType?.GetField("Def", inst);
                mfTeam = mineFieldType?.GetField("team", inst);
                mfIFF = mineFieldType?.GetField("IFFLevel", inst);
                mfStealth = mineFieldType?.GetField("stealthLvl", inst);
                mfHex = mineFieldType?.GetField("hex", inst);
                battleTechGame = ModAssemblyHook.GameType("BattleTech.UnityGameInstance")?.GetProperty("BattleTechGame", st);
                findCombatant = ModAssemblyHook.GameType("BattleTech.CombatGameState")?.GetMethod("FindCombatantByGUID", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(bool) }, null);
                MethodInfo register = ModAssemblyHook.GameType("BattleTech.CoopInvocationSideData")?.GetMethod("Register", st);
                var missing = new List<string>();
                foreach (KeyValuePair<string, object> m in new Dictionary<string, object> {
                    { "hexGrid", hexGrid }, { "tempMaskCells", tempMaskCells }, { "loadedMasksDef", loadedMasks }, { "ExtAmmunitionDef registry", extAmmoRegistry },
                    { "findExtAmmo", findExtAmmo }, { "ExtAmmunitionDef.MineField", extMineField }, { "MineFields", hexMineFields }, { "center", hexCenter },
                    { "AddMineFieldSync", addMineField }, { "UpdateIndicatorVisibility", updateIndicator }, { "tempDesignMaskCounters", cellCounters }, { "tempDesignMask", cellTempMask }, { "AddDesignMask", addDesignMask },
                    { "ReconstructTempDesignMask", reconstruct }, { "MapPoint.x/y", pointX == null ? null : pointY }, { "MineField.count", mfCount }, { "MineField.UIName", mfUIName },
                    { "MineField.owner", mfOwner }, { "MineField.weapon", mfWeapon }, { "MineField.Def", mfDef }, { "MineField.team", mfTeam }, { "MineField.IFFLevel", mfIFF },
                    { "MineField.stealthLvl", mfStealth }, { "MineField.hex", mfHex }, { "BattleTechGame", battleTechGame }, { "FindCombatantByGUID", findCombatant },
                    { "CoopInvocationSideData.Register", register }, { "burningHexes", burningHexes }, { "terrainCells", hexTerrainCells },
                    { "burnEffectCounter", hexBurnCounter }, { "isHasForest", hexHasForest }, { "wasHasForest", hexWasForest }, { "expandingThisTurn", hexExpanding },
                    { "burningWeapon", hexBurnWeapon }, { "MapTerrainHexCell.Combat", hexCombat }, { "applyBurnVisuals", burnVisuals }, { "hexCell", cellHex },
                    { "MapTerrainDataCellEx.x/y", cellX == null ? null : cellY }, { "MapTerrainDataCell.terrainMask", cellTerrainMask }, { "BurningCounter", cellBurnCounter },
                    { "BurningStrength", cellBurnStrength }, { "BurningWeapon", cellBurnWeapon }, { "wasForest", cellWasForest }, { "wasCustom", cellWasCustom },
                    { "wasRoad", cellWasRoad }, { "CustomDesignMask", cellCustomMask }, { "MapTerrainDataCellEx.burn", burn } })
                    if (m.Value == null) missing.Add(m.Key);
                if (missing.Count > 0) { log(Tag + "|FAILED|shape changed: " + string.Join(",", missing.ToArray()) + " not found (needs UCU patch 166)"); return; }
                counter = CoopCarryCounter.For(Tag, log);
                // CAC methods are patched only at the first combat, after CAC's Init (fault M).
                ModAssemblyHook.AtFirstCombat(() => PatchBurn(burn), log, Tag);
                register.Invoke(null, new object[] { Tag, new Func<object, string>(Capture), new Action<object, string>(ApplyData) });
                log(Tag + "|installed|" + cac.GetName().Version + "|minefields, temporary design masks and fires sent to a peer that loads a co-op snapshot");
            }
            catch (Exception e) { log(Tag + "|FAILED|" + ModStateCarry.Inner(e)); }
        }

        static object Combat() => ModStateCarry.Get(battleTechGame.GetValue(null, null), "Combat");

        // ---- host ----
        static string Capture(object invocation)
        {
            if (invocation != null) return null;
            try
            {
                object combat = Combat();
                if (combat == null) return null;
                var w = new W();
                Dictionary<object, string> ammoOf = MineFieldAmmo();
                var hexes = new List<object>();
                foreach (DictionaryEntry e in (IDictionary)hexGrid.GetValue(null))
                    if (hexMineFields.GetValue(e.Value) is IList l && l.Count > 0) hexes.Add(e.Value);
                w.Int(hexes.Count);
                foreach (object h in hexes)
                {
                    w.Vec(hexCenter.GetValue(h));
                    IList fields = (IList)hexMineFields.GetValue(h);
                    w.Int(fields.Count);
                    foreach (object mf in fields)
                    {
                        ammoOf.TryGetValue(mfDef.GetValue(mf) ?? new object(), out string ammo);
                        w.Str(ammo ?? "");
                        w.Int((int)mfCount.GetValue(mf)); w.Int((int)mfIFF.GetValue(mf)); w.Int((int)mfStealth.GetValue(mf));
                        w.Str(mfUIName.GetValue(mf) as string ?? "");
                        w.Str(ModStateCarry.Get(mfOwner.GetValue(mf), "GUID") as string ?? "");
                        w.Str(ModStateCarry.Get(mfWeapon.GetValue(mf), "uid") as string ?? "");
                        w.Str(ModStateCarry.Get(mfTeam.GetValue(mf), "GUID") as string ?? "");
                    }
                }
                List<object[]> cells = MaskCells(combat);
                w.Int(cells.Count);
                foreach (object[] c in cells)
                {
                    w.Int((int)c[0]); w.Int((int)c[1]);
                    var counters = (IDictionary)cellCounters.GetValue(c[2]);
                    w.Int(counters.Count);
                    foreach (DictionaryEntry e in counters) { w.Str(ModStateCarry.Get(e.Key, "Id") as string ?? ""); w.Int((int)e.Value); }
                    w.Str(ModStateCarry.Get(cellTempMask.GetValue(c[2]), "Id") as string ?? "");
                }
                int fires = WriteFires(w, combat);
                counter.Capture++;
                if (hexes.Count > 0 || cells.Count > 0 || fires > 0) counter.NonEmpty++;
                Row("capture", Summary(combat), "full");
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(w.ToString()));
            }
            catch (Exception e) { log(Tag + "|FAILED|capture|" + ModStateCarry.Inner(e)); return null; }
        }

        // The ammunition each MineFieldDef belongs to (CAC keeps one MineFieldDef per ExtAmmunitionDef).
        static Dictionary<object, string> MineFieldAmmo()
        {
            var r = new Dictionary<object, string>(new SameObject());
            if (extAmmoRegistry.GetValue(null) is IEnumerable registry)
                foreach (object kv in registry)
                {
                    string id = ModStateCarry.Get(kv, "Key") as string;
                    object def = extMineField.GetValue(ModStateCarry.Get(kv, "Value"), null);
                    if (id != null && def != null && !r.ContainsKey(def)) r[def] = id;
                }
            return r;
        }

        // [x, y, cell] for every cell in tempMaskCells with counters, in x then y order.
        static List<object[]> MaskCells(object combat)
        {
            var list = new List<object[]>();
            Array grid = ModStateCarry.Get(ModStateCarry.Get(combat, "MapMetaData"), "mapTerrainDataCells") as Array;
            if (grid == null || !(tempMaskCells.GetValue(null) is IEnumerable points)) return list;
            foreach (object p in points)
            {
                int x = (int)pointX.GetValue(p), y = (int)pointY.GetValue(p);
                if (x < 0 || y < 0 || x >= grid.GetLength(0) || y >= grid.GetLength(1)) continue;
                object c = grid.GetValue(x, y);
                if (c != null && cellCounters.GetValue(c) is IDictionary d && d.Count > 0) list.Add(new[] { x, y, c });
            }
            list.Sort((a, b) => (int)a[0] != (int)b[0] ? ((int)a[0]).CompareTo((int)b[0]) : ((int)a[1]).CompareTo((int)b[1]));
            return list;
        }

        // ---- guest ----
        static void ApplyData(object invocation, string data)
        {
            if (invocation != null || string.IsNullOrEmpty(data)) return;
            try
            {
                object combat = Combat();
                if (combat == null) return;
                var r = new R(Encoding.UTF8.GetString(Convert.FromBase64String(data)));
                // Parse in full first, so a malformed payload changes nothing.
                var mines = new List<KeyValuePair<object, List<string[]>>>();
                int nh = r.Int();
                for (int i = 0; i < nh; i++)
                {
                    object centre = r.Vec();
                    int nf = r.Int();
                    var list = new List<string[]>();
                    for (int j = 0; j < nf; j++) list.Add(new[] { r.Str(), r.Int().ToString(CultureInfo.InvariantCulture), r.Int().ToString(CultureInfo.InvariantCulture), r.Int().ToString(CultureInfo.InvariantCulture), r.Str(), r.Str(), r.Str(), r.Str() });
                    mines.Add(new KeyValuePair<object, List<string[]>>(centre, list));
                }
                var masks = new List<object[]>();
                int nc = r.Int();
                for (int i = 0; i < nc; i++)
                {
                    int x = r.Int(), y = r.Int(), n = r.Int();
                    var pairs = new List<KeyValuePair<string, int>>();
                    for (int j = 0; j < n; j++) pairs.Add(new KeyValuePair<string, int>(r.Str(), r.Int()));
                    masks.Add(new object[] { x, y, pairs, r.Str() });
                }
                List<object[]> fireHexes = ReadFires(r, out List<int> burningOrder);
                if (!r.End) throw new FormatException("trailing data");
                Row("before", Summary(combat), "apply");
                ApplyFires(combat, fireHexes, burningOrder);
                IDictionary grid = (IDictionary)hexGrid.GetValue(null);
                if (grid.Count == 0 && (mines.Count > 0)) { log(Tag + "|apply-failed|hexGrid is empty on this peer: minefields not applied"); }
                // Minefields: the host decides; every hex's list is replaced.
                foreach (DictionaryEntry e in grid)
                    if (hexMineFields.GetValue(e.Value) is IList l && l.Count > 0) { l.Clear(); updateIndicator.Invoke(e.Value, null); }
                foreach (KeyValuePair<object, List<string[]>> h in mines)
                {
                    object hex = grid.Contains(h.Key) ? grid[h.Key] : Nearest(grid, h.Key);
                    if (hex == null) { Unmapped("hex", Vec(h.Key)); continue; }
                    foreach (string[] f in h.Value) AddMine(combat, hex, f);
                }
                // Temporary masks.
                Array cells = ModStateCarry.Get(ModStateCarry.Get(combat, "MapMetaData"), "mapTerrainDataCells") as Array;
                var keep = new Dictionary<long, bool>();
                foreach (object[] m in masks) keep[((long)(int)m[0] << 32) | (uint)(int)m[1]] = true;
                foreach (object[] own in MaskCells(combat))
                    if (!keep.ContainsKey(((long)(int)own[0] << 32) | (uint)(int)own[1])) { ((IDictionary)cellCounters.GetValue(own[2])).Clear(); reconstruct.Invoke(own[2], null); }
                foreach (object[] m in masks)
                {
                    int x = (int)m[0], y = (int)m[1];
                    object cell = cells != null && x >= 0 && y >= 0 && x < cells.GetLength(0) && y < cells.GetLength(1) ? cells.GetValue(x, y) : null;
                    if (cell == null || cellCounters.GetValue(cell) == null) { Unmapped("cell", x + "," + y); continue; }
                    ((IDictionary)cellCounters.GetValue(cell)).Clear();
                    foreach (KeyValuePair<string, int> p in (List<KeyValuePair<string, int>>)m[2])
                    {
                        object def = Mask(combat, p.Key);
                        if (def == null) { Unmapped("mask", x + "," + y + ":" + p.Key); continue; }
                        addDesignMask.Invoke(cell, new[] { def, (object)p.Value });
                    }
                    if (((IDictionary)cellCounters.GetValue(cell)).Count == 0) reconstruct.Invoke(cell, null);
                    string mine = ModStateCarry.Get(cellTempMask.GetValue(cell), "Id") as string ?? "";
                    if (mine != (string)m[3] && ++rows <= 300) log(Tag + "|mask-differs|" + x + "," + y + "|host=" + m[3] + "|guest=" + mine);
                }
                counter.Apply++;
                if (mines.Count > 0 || masks.Count > 0 || fireHexes.Count > 0) counter.NonEmpty++;
                Row("apply", Summary(combat), "apply");
            }
            catch (Exception e) { log(Tag + "|apply-failed|" + ModStateCarry.Inner(e)); }
        }

        // [ammo, count, iff, stealth, uiName, owner, weapon uid, team]
        static void AddMine(object combat, object hex, string[] f)
        {
            object ext = f[0].Length == 0 ? null : findExtAmmo.Invoke(null, new object[] { f[0] });
            object def = ext == null || ModStateCarry.Get(ext, "Id") as string != f[0] ? null : extMineField.GetValue(ext, null);
            if (def == null) { Unmapped("minefield-ammo", f[0]); return; }
            object owner = f[5].Length == 0 ? null : findCombatant.Invoke(combat, new object[] { f[5], false });
            object weapon = null;
            if (owner != null)
                foreach (object w in ModStateCarry.Get(owner, "Weapons") as IEnumerable ?? new object[0])
                    if (ModStateCarry.Get(w, "uid") as string == f[6]) { weapon = w; break; }
            object team = null;
            if (ModStateCarry.Get(combat, "Teams") is IEnumerable teams)
                foreach (object t in teams) if (ModStateCarry.Get(t, "GUID") as string == f[7]) { team = t; break; }
            // CAC reads the owner's weapon and team when a mine goes off: a minefield without them is not made.
            if ((f[5].Length > 0 && owner == null) || (f[6].Length > 0 && weapon == null) || (f[7].Length > 0 && team == null))
            { Unmapped("minefield-owner", f[5] + "/" + f[6] + "/" + f[7]); return; }
            object mf = FormatterServices.GetUninitializedObject(mineFieldType);
            mfHex.SetValue(mf, hex); mfDef.SetValue(mf, def); mfOwner.SetValue(mf, owner); mfWeapon.SetValue(mf, weapon); mfTeam.SetValue(mf, team);
            mfCount.SetValue(mf, int.Parse(f[1], CultureInfo.InvariantCulture)); mfIFF.SetValue(mf, int.Parse(f[2], CultureInfo.InvariantCulture));
            mfStealth.SetValue(mf, int.Parse(f[3], CultureInfo.InvariantCulture)); mfUIName.SetValue(mf, f[4]);
            addMineField.Invoke(hex, new[] { mf });
        }

        static object Mask(object combat, string id)
        {
            if (loadedMasks.GetValue(null) is IDictionary loaded && loaded.Contains(id)) return loaded[id];
            object store = ModStateCarry.Get(ModStateCarry.Get(battleTechGame.GetValue(null, null), "DataManager"), "DesignMaskDefs");
            if (store == null) return null;
            var types = new List<Type> { store.GetType() };
            types.AddRange(store.GetType().GetInterfaces());
            foreach (Type t in types)
            {
                MethodInfo tryGet = t.GetMethod("TryGet", BindingFlags.Instance | BindingFlags.Public);
                if (tryGet == null || tryGet.GetParameters().Length != 2) continue;
                object[] args = { id, null };
                if ((bool)tryGet.Invoke(store, args)) return args[1];
                return null;
            }
            return null;
        }

        static object Nearest(IDictionary grid, object centre)
        {
            object best = null;
            double bestD = 0.25;
            float cx = F(centre, "x"), cz = F(centre, "z");
            foreach (DictionaryEntry e in grid)
            {
                double d = Math.Abs(F(e.Key, "x") - cx) + Math.Abs(F(e.Key, "z") - cz);
                if (d < bestD) { bestD = d; best = e.Value; }
            }
            return best;
        }

        static float F(object v, string c) => (float)v.GetType().GetField(c).GetValue(v);

        static string Vec(object v) => v == null ? "?" : F(v, "x").ToString("R", CultureInfo.InvariantCulture) + "_" + F(v, "y").ToString("R", CultureInfo.InvariantCulture) + "_" + F(v, "z").ToString("R", CultureInfo.InvariantCulture);

        static void Unmapped(string what, string key) { if (++rows <= 300) log(Tag + "|unmapped|" + what + "|" + key); }

        // hexes=<n>|mines=<centre:ammo*count/...;...>|cells=<n>|masks=<x,y:id*rounds/...=combined;...>, sorted, so both peers' rows compare as text.
        static string Summary(object combat)
        {
            Dictionary<object, string> ammoOf = MineFieldAmmo();
            var mines = new List<string>();
            foreach (DictionaryEntry e in (IDictionary)hexGrid.GetValue(null))
                if (hexMineFields.GetValue(e.Value) is IList l && l.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (object mf in l)
                    {
                        ammoOf.TryGetValue(mfDef.GetValue(mf) ?? new object(), out string ammo);
                        parts.Add((ammo ?? "?") + "*" + mfCount.GetValue(mf) + "@" + (ModStateCarry.Get(mfTeam.GetValue(mf), "GUID") as string ?? ""));
                    }
                    mines.Add(Vec(e.Key) + ":" + string.Join("/", parts.ToArray()));
                }
            mines.Sort(StringComparer.Ordinal);
            var masks = new List<string>();
            foreach (object[] c in MaskCells(combat))
            {
                var parts = new List<string>();
                foreach (DictionaryEntry e in (IDictionary)cellCounters.GetValue(c[2])) parts.Add((ModStateCarry.Get(e.Key, "Id") as string ?? "?") + "*" + e.Value);
                masks.Add(c[0] + "," + c[1] + ":" + string.Join("/", parts.ToArray()) + "=" + (ModStateCarry.Get(cellTempMask.GetValue(c[2]), "Id") as string ?? ""));
            }
            return "hexes=" + mines.Count + "|mines=" + string.Join(";", mines.ToArray()) + "|cells=" + masks.Count + "|masks=" + string.Join(";", masks.ToArray()) + "|" + FireSummary(combat);
        }

        static void Row(string kind, string summary, string at)
        {
            if (++rows > 300) return;
            log(Tag + "|" + kind + "|" + summary + "|at=" + at);
        }

        // ---- fires ----
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void PatchBurn(MethodInfo burn)
        {
            new HarmonyLib.Harmony("battletech.port.cac-map-coop-fire").Patch(burn,
                postfix: new HarmonyLib.HarmonyMethod(typeof(CacDynamicMapCoopSync).GetMethod(nameof(BurnPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            log(Tag + "|fire-hook|MapTerrainDataCellEx.burn postfix records the hexes a fire touched");
        }

        // Every peer records the hexes a fire touched in this combat (the host's record is the one sent).
        static void BurnPostfix(object __instance)
        {
            try
            {
                object combat = Combat();
                if (!ReferenceEquals(combat, firedCombat)) { firedHexes.Clear(); firedCombat = combat; }
                object hex = cellHex.GetValue(__instance);
                if (hex != null) firedHexes[hex] = true;
            }
            catch (Exception e) { if (++rows <= 300) log(Tag + "|FAILED|fire-hook|" + ModStateCarry.Inner(e)); }
        }

        static List<object> FiredHexes(object combat)
        {
            var list = new List<object>();
            if (combat == null) return list;
            var seen = new Dictionary<object, bool>(new SameObject());
            if (ReferenceEquals(combat, firedCombat)) foreach (object h in firedHexes.Keys) { seen[h] = true; list.Add(h); }
            // A fire restored without the burn path (a loaded combat save) is still in burningHexes.
            foreach (object h in burningHexes.GetValue(null) as IEnumerable ?? new object[0]) if (h != null && !seen.ContainsKey(h)) { seen[h] = true; list.Add(h); }
            list.Sort((a, b) => string.CompareOrdinal(Vec(hexCenter.GetValue(a)), Vec(hexCenter.GetValue(b))));
            return list;
        }

        static string WeaponKey(object weapon) => weapon == null ? "" : (ModStateCarry.Get(ModStateCarry.Get(weapon, "parent"), "GUID") as string ?? "") + "/" + (ModStateCarry.Get(weapon, "uid") as string ?? "");

        static object WeaponOf(object combat, string key)
        {
            int slash = key.IndexOf('/');
            if (slash <= 0) return null;
            object owner = findCombatant.Invoke(combat, new object[] { key.Substring(0, slash), false });
            foreach (object w in ModStateCarry.Get(owner, "Weapons") as IEnumerable ?? new object[0])
                if (ModStateCarry.Get(w, "uid") as string == key.Substring(slash + 1)) return w;
            return null;
        }

        static bool B(FieldInfo f, object o) => (bool)f.GetValue(o);

        // Touched hexes with their cells, then burningHexes as indexes into them (host order). Returns the touched-hex count.
        static int WriteFires(W w, object combat)
        {
            List<object> hexes = FiredHexes(combat);
            var index = new Dictionary<object, int>(new SameObject());
            w.Int(hexes.Count);
            foreach (object h in hexes)
            {
                index[h] = index.Count;
                w.Vec(hexCenter.GetValue(h));
                w.Int((int)hexBurnCounter.GetValue(h)); w.Int(B(hexHasForest, h) ? 1 : 0); w.Int(B(hexWasForest, h) ? 1 : 0); w.Int(B(hexExpanding, h) ? 1 : 0);
                w.Str(WeaponKey(hexBurnWeapon.GetValue(h)));
                IList cells = hexTerrainCells.GetValue(h) as IList ?? new object[0];
                w.Int(cells.Count);
                foreach (object c in cells)
                {
                    w.Int((int)cellX.GetValue(c)); w.Int((int)cellY.GetValue(c)); w.Int(Convert.ToInt32(cellTerrainMask.GetValue(c), CultureInfo.InvariantCulture));
                    w.Int((int)cellBurnCounter.GetValue(c)); w.Int((int)cellBurnStrength.GetValue(c)); w.Str(WeaponKey(cellBurnWeapon.GetValue(c)));
                    w.Int((B(cellWasForest, c) ? 1 : 0) | (B(cellWasCustom, c) ? 2 : 0) | (B(cellWasRoad, c) ? 4 : 0));
                    w.Str(ModStateCarry.Get(cellCustomMask.GetValue(c), "Id") as string ?? "");
                }
            }
            var burning = new List<int>();
            foreach (object h in burningHexes.GetValue(null) as IEnumerable ?? new object[0])
                if (h != null && index.TryGetValue(h, out int i)) burning.Add(i);
            w.Int(burning.Count);
            foreach (int i in burning) w.Int(i);
            return hexes.Count;
        }

        // [centre, counter, hasForest, wasForest, expanding, weapon, List<object[] cell fields>]
        static List<object[]> ReadFires(R r, out List<int> burningOrder)
        {
            var hexes = new List<object[]>();
            int n = r.Int();
            for (int i = 0; i < n; i++)
            {
                object centre = r.Vec();
                int counter = r.Int(), has = r.Int(), was = r.Int(), exp = r.Int();
                string weapon = r.Str();
                int nc = r.Int();
                var cells = new List<object[]>();
                for (int j = 0; j < nc; j++)
                {
                    int x = r.Int(), y = r.Int(), mask = r.Int(), bc = r.Int(), bs = r.Int();
                    string bw = r.Str();
                    int flags = r.Int();
                    string custom = r.Str();
                    cells.Add(new object[] { x, y, mask, bc, bs, bw, flags, custom });
                }
                hexes.Add(new object[] { centre, counter, has, was, exp, weapon, cells });
            }
            burningOrder = new List<int>();
            int nb = r.Int();
            for (int i = 0; i < nb; i++) burningOrder.Add(r.Int());
            return hexes;
        }

        static void ApplyFires(object combat, List<object[]> hexes, List<int> burningOrder)
        {
            IDictionary grid = (IDictionary)hexGrid.GetValue(null);
            Array map = ModStateCarry.Get(ModStateCarry.Get(combat, "MapMetaData"), "mapTerrainDataCells") as Array;
            var resolved = new object[hexes.Count];
            if (!ReferenceEquals(combat, firedCombat)) { firedHexes.Clear(); firedCombat = combat; }
            for (int i = 0; i < hexes.Count; i++)
            {
                object[] h = hexes[i];
                object hex = grid.Contains(h[0]) ? grid[h[0]] : Nearest(grid, h[0]);
                if (hex == null) { Unmapped("fire-hex", Vec(h[0])); continue; }
                resolved[i] = hex;
                hexCombat.SetValue(hex, combat);
                hexBurnCounter.SetValue(hex, (int)h[1]); hexHasForest.SetValue(hex, (int)h[2] == 1); hexWasForest.SetValue(hex, (int)h[3] == 1);
                hexExpanding.SetValue(hex, (int)h[4] == 1);
                object hw = WeaponOf(combat, (string)h[5]);
                if (hw == null && ((string)h[5]).Length > 0) Unmapped("fire-weapon", (string)h[5]);
                hexBurnWeapon.SetValue(hex, hw);
                foreach (object[] c in (List<object[]>)h[6])
                {
                    int x = (int)c[0], y = (int)c[1];
                    object cell = map != null && x >= 0 && y >= 0 && x < map.GetLength(0) && y < map.GetLength(1) ? map.GetValue(x, y) : null;
                    if (cell == null || !cellBurnCounter.DeclaringType.IsInstanceOfType(cell)) { Unmapped("fire-cell", x + "," + y); continue; }
                    cellTerrainMask.SetValue(cell, Enum.ToObject(cellTerrainMask.FieldType, (int)c[2]));
                    cellBurnCounter.SetValue(cell, (int)c[3]); cellBurnStrength.SetValue(cell, (int)c[4]);
                    object cw = WeaponOf(combat, (string)c[5]);
                    if (cw == null && ((string)c[5]).Length > 0) Unmapped("fire-weapon", (string)c[5]);
                    cellBurnWeapon.SetValue(cell, cw);
                    int flags = (int)c[6];
                    cellWasForest.SetValue(cell, (flags & 1) != 0); cellWasCustom.SetValue(cell, (flags & 2) != 0); cellWasRoad.SetValue(cell, (flags & 4) != 0);
                    string id = (string)c[7];
                    object mask = id.Length == 0 ? null : Mask(combat, id);
                    if (mask == null && id.Length > 0) Unmapped("fire-mask", x + "," + y + ":" + id);
                    cellCustomMask.SetValue(cell, mask);
                    reconstruct.Invoke(cell, null);
                }
                firedHexes[hex] = true;
            }
            IList burning = (IList)burningHexes.GetValue(null);
            burning.Clear();
            foreach (int i in burningOrder)
            {
                if (i < 0 || i >= resolved.Length || resolved[i] == null) continue;
                burning.Add(resolved[i]);
                // The fire effect only (visual); a failure is logged and leaves the fire itself in place.
                try { burnVisuals.Invoke(resolved[i], null); }
                catch (Exception e) { if (++rows <= 300) log(Tag + "|apply-failed|fire-visual|" + Vec(hexCenter.GetValue(resolved[i])) + "|" + ModStateCarry.Inner(e)); }
            }
        }

        // fires=<burning hexes>|burned=<touched hexes>|fire=<fnv32 of every touched hex's and cell's fields>
        static string FireSummary(object combat)
        {
            uint h = 2166136261;
            var sb = new StringBuilder();
            List<object> hexes = FiredHexes(combat);
            foreach (object x in hexes)
            {
                sb.Append(Vec(hexCenter.GetValue(x))).Append(':').Append(hexBurnCounter.GetValue(x)).Append(B(hexHasForest, x)).Append(B(hexWasForest, x));
                foreach (object c in hexTerrainCells.GetValue(x) as IEnumerable ?? new object[0])
                    sb.Append(cellX.GetValue(c)).Append(',').Append(cellY.GetValue(c)).Append('=').Append(Convert.ToInt32(cellTerrainMask.GetValue(c), CultureInfo.InvariantCulture))
                        .Append('/').Append(cellBurnCounter.GetValue(c)).Append('/').Append(cellBurnStrength.GetValue(c)).Append('/')
                        .Append(ModStateCarry.Get(cellCustomMask.GetValue(c), "Id") as string ?? "");
            }
            foreach (char ch in sb.ToString()) { h ^= ch; h *= 16777619; }
            int burningCount = burningHexes.GetValue(null) is ICollection bc ? bc.Count : 0;
            return "fires=" + burningCount + "|burned=" + hexes.Count + "|fire=" + h.ToString("x8", CultureInfo.InvariantCulture);
        }


        sealed class SameObject : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        // Length-prefixed text writer/reader: ints "i<n>;", strings "s<len>:<chars>", vectors "v<x>;<y>;<z>;" (round-trip floats).
        sealed class W
        {
            readonly StringBuilder sb = new StringBuilder();
            public void Int(int v) => sb.Append('i').Append(v.ToString(CultureInfo.InvariantCulture)).Append(';');
            public void Str(string s) => sb.Append('s').Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s);
            public void Vec(object v) => sb.Append('v').Append(F(v, "x").ToString("R", CultureInfo.InvariantCulture)).Append(';').Append(F(v, "y").ToString("R", CultureInfo.InvariantCulture)).Append(';').Append(F(v, "z").ToString("R", CultureInfo.InvariantCulture)).Append(';');
            public override string ToString() => sb.ToString();
        }

        sealed class R
        {
            readonly string s;
            int pos;
            public R(string text) { s = text; }
            public bool End => pos == s.Length;
            void Expect(char c) { if (pos >= s.Length || s[pos] != c) throw new FormatException("expected '" + c + "' at " + pos); pos++; }
            string Until(char c) { int e = s.IndexOf(c, pos); if (e < 0) throw new FormatException("missing '" + c + "' at " + pos); string r = s.Substring(pos, e - pos); pos = e + 1; return r; }
            public int Int() { Expect('i'); return int.Parse(Until(';'), CultureInfo.InvariantCulture); }
            public string Str() { Expect('s'); int n = int.Parse(Until(':'), CultureInfo.InvariantCulture); string r = s.Substring(pos, n); pos += n; return r; }
            public object Vec()
            {
                Expect('v');
                Type t = ModAssemblyHook.GameType("UnityEngine.Vector3");
                return Activator.CreateInstance(t, new object[] { float.Parse(Until(';'), NumberStyles.Float, CultureInfo.InvariantCulture),
                    float.Parse(Until(';'), NumberStyles.Float, CultureInfo.InvariantCulture), float.Parse(Until(';'), NumberStyles.Float, CultureInfo.InvariantCulture) });
            }
        }
    }
}
