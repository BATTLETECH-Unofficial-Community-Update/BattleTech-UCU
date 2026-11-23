# What UCU changes for each mod

UCU runs stock ModTek v4 mods. A few of them need help, either because Unity 6 differs from the 2018 game or because
a mod fails on the 2018 game too. These notes list every such change, mod by mod: what broke, what UCU does, and what
the mod could change upstream so it needs no help.

**What's here.** The adapters are UCU's own code, in [`tools/mod-bootstrap/`](../../tools/mod-bootstrap/). They are
Harmony patches applied in memory while the game runs, and each one is applied only when its mod's assembly loads.
No mod file and no game file changes on disk. Some fixes live in UCU's own copy of the game code instead.  That code isn't published; those fixes are described below, and each note says
whether its adapter depends on one.

**Combat saves.** Combat (mid-battle) saves fail with most large mod packs, which is why such packs typically turn them
off. As of UCU 2.2.0-3B an experimental option (launcher → *Mods*, off by default) works around this on the fly, as a
workaround for the symptoms. Several adapters exist only for it. They change nothing unless that option is on and a
combat is saved or loaded. [Combat saves: notes for mod authors](../mod-authors-combat-saves.md) explains the patterns
behind them and what a mod can do so it saves and loads correctly with no help.

| Mod | Note | Why |
|---|---|---|
| MonsterMashup | [monstermashup.md](monstermashup.md) | co-op |
| [ModTek](https://github.com/BattletechModders/ModTek) | [modtek.md](modtek.md) | runtime and loader differences on Unity 6 |
| AttackImprovementMod | [attackimprovementmod.md](attackimprovementmod.md) | co-op, with CustomUnits |
| [BTDebug](https://github.com/CWolfs/BTDebug) | [btdebug.md](btdebug.md) | gizmo shaders not found, load never ends |
| [CustomActivatableEquipment](https://github.com/BattletechModders/CustomActivatableEquipment) | [customactivatableequipment.md](customactivatableequipment.md) | combat saves, co-op |
| CBTBehaviorsEnhanced | [cbtbehaviorsenhanced.md](cbtbehaviorsenhanced.md) | co-op |
| [CustomAmmoCategories](https://github.com/BattletechModders/CustomAmmoCategories/tree/master/CustomAmmoCategories) | [customammocategories.md](customammocategories.md) | combat saves, co-op |
| [CustomPrewarm](https://github.com/CMiSSioN/CustomPrewarm) | [customprewarm.md](customprewarm.md) | cache build fails on null lists (2018 game too) |
| [CustomUnits](https://github.com/BattletechModders/CustomAmmoCategories/tree/master/CustomUnits) | [customunits.md](customunits.md) | combat saves, co-op |
| CustomUnitSpawn | [customunitspawn.md](customunitspawn.md) | Unity 6 renamed `Random.Range` parameters |
| [EnvironmentalDesignMasks](https://github.com/BlueWinds/EnvironmentalDesignMasks) | [spam-and-edm.md](spam-and-edm.md) | combat saves |
| [ExpandedInitiative](https://github.com/BattletechModders/ExpandedInitiative) | [expandedinitiative.md](expandedinitiative.md) | combat saves |
| [FieldRepairs](https://github.com/BattletechModders/FieldRepairs) | [fieldrepairs.md](fieldrepairs.md) | co-op |
| [IRBTModUtils](https://github.com/BattletechModders/IRBTModUtils) | [irbtmodutils.md](irbtmodutils.md) | Unity 6 refuses small compressed textures |
| [IRTweaks](https://github.com/BattletechModders/IRTweaks) | [irtweaks.md](irtweaks.md) | combat saves, co-op |
| [MechEngineer](https://github.com/BattletechModders/MechEngineer) | [mechengineer.md](mechengineer.md) | combat saves |
| [MissionControl](https://github.com/CWolfs/MissionControl) | [missioncontrol.md](missioncontrol.md) | combat saves, co-op |
| [PanicSystem](https://github.com/BattletechModders/PanicSystem) | [panicsystem.md](panicsystem.md) | co-op |
| PracticeMakesPerfect | [practicemakesperfect.md](practicemakesperfect.md) | co-op |
| RogueTechPerfFixes, CustomStatisticEffects | [effect-caches.md](effect-caches.md) | combat saves, co-op |
| [SoldiersPiratesAssassinsMercs](https://github.com/BattletechModders/SoldiersPiratesAssassinsMercs) | [spam-and-edm.md](spam-and-edm.md) | combat saves |
| [StrategicOperations](https://github.com/BattletechModders/StrategicOperations) | [strategicoperations.md](strategicoperations.md) | combat saves, co-op |
| [TisButAScratch](https://github.com/BattletechModders/TisButAScratch) | [tisbutascratch.md](tisbutascratch.md) | combat saves, co-op |

Some adapters aren't tied to one mod. [`ActorHydrateDeferral.cs`](../../tools/mod-bootstrap/ActorHydrateDeferral.cs)
and [`EncounterLoadDeferral.cs`](../../tools/mod-bootstrap/EncounterLoadDeferral.cs) hold back mod postfixes that run
too early on a save load. [`CoopSpawnDeterminism.cs`](../../tools/mod-bootstrap/CoopSpawnDeterminism.cs) seeds the
random numbers drawn by mods that hook unit spawning in co-op. [`ComponentOrderLoadCompat.cs`](../../tools/mod-bootstrap/ComponentOrderLoadCompat.cs)
puts a unit's component list back in loadout order after a co-op combat-save load (the game's load regroups it by type, and
the guest reloads on every resync while the host does not), so a critical-hit slot names the same component on both PCs. [`ModStateCensus.cs`](../../tools/mod-bootstrap/ModStateCensus.cs)
is diagnostic only: it lists mod static fields at save and load and changes nothing.

## Game-code fixes that help mods

Described here because their code isn't published:

- The multiplayer content check hashes each type's files in id order instead of directory order, and treats CR LF as
  LF, so two players with the same mod pack can join each other.
- A load batch that raised the priority of a request with no loader yet no longer leaves the game loading forever.
  This hit every modded combat load; it's a retail bug.
- The extension-method scan keeps the types that did load when a mod assembly has one that can't, so contract
  results no longer show `ERROR: resolving 'ToPercentString'`. The same scan now also skips a method or type whose
  signature can't resolve and carries on, which stops the error on installs with no mods. Also a retail bug.
- A map that a mod supplies in an asset bundle now fails with an error when the bundle is missing or won't open: the
  game goes back to the main menu with a message naming the map, and a second load is no longer refused as already
  active. The game used to count that load as a success and never start the map. The bundle is also now requested at the
  weight the game gives asset bundles, so a mod map in a bundle can load; the game's own maps are built-in scenes and
  load as before. Once the map has loaded, the game unloads its map and interstitial bundles; that step used to throw
  and leave them loaded, and now keeps their names for it. Also a retail bug.
- In co-op, the host builds the campaign snapshot for the other player off the main thread, the other player unpacks it
  off the main thread, every waiting network message moves each frame (the game moved one), and a snapshot whose
  campaign bytes haven't changed isn't sent again. This shortens the stalls a heavily modded campaign causes on a room
  change.
- A co-op launch no longer waits for good on a lance unit whose dependency can never load, such as a pilot's `Icon`
  with no sprite in the manifest. Once that unit's own loader has finished, the unit leaves the wait with a `[163]`
  warning naming what is missing, and the level loads without it. Single player never waited and shows no portrait.
  A unit whose loader is still running waits as before. Also a retail bug (retail skirmish multiplayer hangs the same
  way).
- The co-op checks that compare each unit's stats between the two players skip the stats named in a list in the game code
  (`CoopCompareExclusions`, empty in UCU). A mod adds a name for a stat that is per player by design, so one player seeing
  a unit that the other has not no longer starts a resync. CustomUnits' `CUSeenByPlayer` is the one entry.
- In co-op, the host's ownership check on a guest's orders now names the acting unit for every game order type (jump,
  melee, death-from-above, stand, startup, eject, active probe, morale, mortar and vent coolant, as well as the move, attack,
  sensor lock, reserve and inspire it already knew), so the host runs them. For an order type the game doesn't list,
  the host takes the first public string property named `SourceGUID`, `MechGUID`, `ActorGUID` or `targetGUID` as the acting
  unit and checks ownership on it. CustomUnits' `ShutdownInvocation` (the HUD's manual shutdown) passes that way. An order
  type with none of those, or with two that match, is still refused for a guest.
- In co-op, what follows an activation (the same team again, the next team, or the end of the phase) is decided at the
  same point in the order stream on every PC, through a barrier, instead of when each PC's own animation finished. BattleTech
  Advanced's CBTBehaviorsEnhanced replaces the body of `TurnDirector.OnTurnActorActivateComplete`, so the game-code change
  never ran under it; [`CoopActivationDecisionGate.cs`](../../tools/mod-bootstrap/CoopActivationDecisionGate.cs) skips the
  original and every later prefix that honours `__runOriginal` until that decision is due, then lets them run. It changes
  nothing outside a co-op combat.
- In co-op, the host's own client is the reference of every stats compare it takes part in, where the game picked
  whichever peer reported first. A mismatch therefore resyncs the guest; before, a pick of the guest made the host
  try to repair itself, which it refuses, and both players got the desync popup. Skirmish multiplayer picks as before.
- With in-mission saves on, a mod subclass of a saved game class (CustomUnits' mechs, for one) is saved with its
  nearest game class's save contract, and the game can find types in mod assemblies on load. When that game class is
  abstract, as `EncounterObjectGameLogic` is for MissionControl's five encounter types, the game gave it only a skeleton
  contract and its mod subclasses were dropped from the save. The abstract class now gets a complete contract, generated
  once, so they save and load.
- In co-op, mods can attach data to the game's traffic (patch 166). An invocation carries its sender's mod data, which the
  receiver applies just before the invocation runs; a peer that has just loaded a resync or rejoin snapshot gets the host's
  full state; and a change can be sent at once, with the host relaying it.
  Patch 176 lets a change be held by the host and applied at an order point both peers share.
  [`CustomAmmoCategoriesCoopModeSync.cs`](../../tools/mod-bootstrap/CustomAmmoCategoriesCoopModeSync.cs) uses it for weapon modes.
- After a co-op combat-save load, a pilot's stat history is rebuilt from the hydrated effects. Retail's `Pilot` saves no
  `StatHistory`, and `StatCollection.Load` restores values with an empty history, so a pilot-targeted effect that ends after a
  load cannot take its change back. TisButAScratch's Life Support bleeding-rate multiplier in BTA is one such effect.
  [`PilotStatHistoryLoadCompat.cs`](../../tools/mod-bootstrap/PilotStatHistoryLoadCompat.cs) rebuilds each such stat's
  history on the guest, and leaves a stat as loaded when the rebuilt value would differ. Single player is unchanged. Also a retail bug.
  Patch 183 makes the co-op snapshot carry each pilot's `StatHistory`, so a guest undoes a pilot effect when it ends as the
  host does, and the rebuild is skipped when the history came with the snapshot (`StatCollection.CoopPilotHistoryCarried`).
  Single-player save bytes are unchanged.
- In a co-op combat, mod static state that a guest's resync reload empties can be sent to it from the host.
  [`ModStateCarry.cs`](../../tools/mod-bootstrap/ModStateCarry.cs) takes a mod's static fields, encodes them by their declared
  types (including references to units by GUID) and registers one source with the patch 166 side channel.
  [`CoopActorTransientStateSync.cs`](../../tools/mod-bootstrap/CoopActorTransientStateSync.cs) sends the actor fields a combat
  save leaves out (pending heat, knockdown and prone flags, death flags, the occupied design mask).
  A decision only one peer makes, such as an AI check or a HUD toggle, is recorded by its authority and replayed on the
  others with the outcome forced ([`CoopDecisionChannel.cs`](../../tools/mod-bootstrap/CoopDecisionChannel.cs)).
  [`CoopRngSweepDeterminism.cs`](../../tools/mod-bootstrap/CoopRngSweepDeterminism.cs) seeds the remaining unseeded mod random
  draws (ConcreteJungle ambushes, CollateralDamage, TisButAScratch overcrowding and others) per scope in co-op.
- In a co-op combat the same mod save support is on whether or not in-mission saves is ticked, so the combat save a host
  sends for a resync or rejoin carries mod state and the other player's load uses UCU's save adapters. The adapters
  register at the first co-op combat, and MissionControl's cargo load patch comes off when it ends. IRTweaks' combat-save
  refusal and MechEngineer's adapter still follow the launcher option alone.
