# Combat saves: notes for mod authors

BattleTech's combat saves fail with most large ModTek packs, which is why packs such as BattleTech Advanced turn them
off. UCU 2.2.0-3B includes an experimental save shim, off by default (the launcher's *Mods* tab, "EXPERIMENTAL: Enable
in-mission saves"), that works around these failures from outside the mods by making on-the-fly changes to a number of
them ([what UCU changes for each mod](mods/README.md)). It only patches over the symptoms. A mod that follows the points below saves and loads correctly on its own, on the
2018 game and on UCU, and needs no shim.

Every point comes from a problem we saw on a real BattleTech Advanced 20.3 combat save, or found by reading the game's
load path. The mods are named so you can see the pattern; none of this is a complaint about them, since most of these
paths never run while combat saves are off.

## 1. Subclassing a saved game class

*Seen with CustomUnits' `CustomMech`, `TrooperSquad`, `QuadMech` and `FakeVehicleMech`.*

The game's serializer can't save a subclass of a saved class (such as `Mech`) unless the subclass has a save contract
of its own. Its `ClassTypeResolver` also resolves types only inside `Assembly-CSharp`, so a type in a mod assembly
isn't found on load. On retail the save fails with a `NullReferenceException`.

Either:
- give the class its own save contract (HBS's `[SerializableContract]` on the class and `[SerializableMember]` on each
  field the battle needs) and a parameterless constructor that sets everything a load needs; or
- keep per-unit mod data somewhere the game already saves: `StatCollection` statistics or tags.

Don't rely on the constructor body: a load creates the object without running it, so field initializers and
constructor logic don't run.

## 2. The load order is not the new-battle order

On a load, some state arrives later than in a new battle. A patch or override that runs on a load should null-check
these, or set them itself:

| State | New battle | On a load, set at |
|---|---|---|
| `CombatGameState.EncounterLayerData` | inside `InitializeContract` | the start of `EncounterLayerParent.InitFromSavePassTwo` |
| `MechDef.Chassis` (and `VehicleDef`, `TurretDef`) | before the unit exists | the end of `Mech`/`Vehicle`/`Turret.InitFromSave`, after `BehaviorTree.InitFromSave` has already asked for the hit table |
| `AbstractActor.Pathing`, `JumpPathing` | constructor | after `base.InitFromSave` in `Mech`/`Vehicle.InitFromSave` |
| `AbstractActor.occupiedEncounterLayerCells` | constructor (empty list) | `AbstractActor.Init` → `ReapplyDesignMasks` |
| aura and ability lists (`AuraCache`, `AuraAbilities`, `ComponentAbilities` and others) | constructor | `Mech.Init`'s load branch, after `base.Init` |

For example, an override of `GetHitTable` that reads `MechDef.Chassis` throws on a load, because the game asks for the
hit table before the chassis is restored. Retail's `Mech` doesn't need the chassis at that point.

## 3. Don't skip or defer a step the load depends on

If a patch skips a game step and does it later, a load may need what that step sets in between. CustomAmmoCategories
skips `ReapplyDesignMasks` during `AbstractActor.Init` and runs it later from `ResetPathing`. In a new battle the
constructor has already set an empty cell list, so nothing breaks. On a load the list is still null, and the game's
`CheckEnteredCellsForRegions` throws on it in the same `Init`.

If you defer a step, set what it would have set, or let it run on a load.

## 4. One-time setup hooks run again on a load

`CombatGameState.InitializeContract` and `TurnDirector.OnInitializeContractComplete` fire on a load too. Check
`combat.IsLoadingFromSave` (or `WasFromSave` after the load) before doing setup whose result is already in the save:

- CustomUnits adds its player-controlled convoy lance in an `InitializeContract` postfix. Running it again on a load
  would add the lance twice.
- IRTweaks scales objective buildings' structure on contract complete and whenever an objective attaches a building.
  On a load the saved structure is already scaled, and its "already scaled" list was cleared, so each load scales them
  again, silently. UCU skips that for a building whose `StartingStructure` is already not its unscaled value.

Static mod state cleared in `CombatGameState._Init` is gone after a load unless your mod puts it back from the save,
for example in a `Hydrate` postfix.

## 5. Don't put mod types in the game's trigger cargo

The game's `SerialBox<T>` saves its cargo's type as `GetType().FullName` and loads it with `Type.GetType(name)`, which
finds types only in `Assembly-CSharp`. MissionControl's `DesignResult` and `DesignConditional` subclasses in that cargo
can't load, even on retail, and the encounter fails to deserialize.

Encounter objects created at run time with `Guid.NewGuid()` and not saved also vanish from the encounter's object list
on load, while the saved encounter data still points at them. Give run-time encounter objects stable GUIDs and rebuild
them the same way on a load, or save them yourself.

## 6. Don't assume a career

A skirmish has no `SimGameState`. Save hooks that read `Simulation` (StrategicOperations' `CompanyStats` in a
`PreSerialization` prefix) or the mod's own career reference (WarTechIIC's `sim` in `PostSerialization`) throw on every
skirmish save. Check for a career first.

## 7. Patch parameters and load-only methods

- A postfix on `AbstractActor.Hydrate` runs before `Mech.Hydrate` has set the pilot and `MechDef`, because the concrete
  class calls `base.Hydrate` first. Patch the concrete class's `Hydrate`, or null-check.
- A postfix that changes a result must take `ref __result`. ExpandedInitiative's `HasValidInitiative` postfix takes
  `bool __result` by value, so its rule never applies, and a unit saved on phase 6 to 10 loads on its base phase.
- If your mod patches another mod's methods, wait until that mod has run its own `Init`. Patching a method compiles
  it, which can run that mod's static constructors before its `Init` has set them up.
- Load-only methods (`Hydrate`, `InitFromSave`, `SaveGameDeserialize`) have never run with packs that keep combat saves
  off. Test one save and load before turning them on.

## Testing a mod's combat save

1. Start a skirmish with your mod, save on round 1, load the save, and begin the mission.
2. Look for errors in `Player.log` between "Initializing from save!" and the first turn.
3. Compare your mod's state before the save and after the load, including static fields that hold battle state.
