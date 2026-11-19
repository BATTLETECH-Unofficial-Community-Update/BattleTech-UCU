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
| [ModTek](https://github.com/BattletechModders/ModTek) | [modtek.md](modtek.md) | runtime and loader differences on Unity 6 |
| [BTDebug](https://github.com/CWolfs/BTDebug) | [btdebug.md](btdebug.md) | gizmo shaders not found, load never ends |
| [CustomAmmoCategories](https://github.com/BattletechModders/CustomAmmoCategories/tree/master/CustomAmmoCategories) | [customammocategories.md](customammocategories.md) | combat saves |
| [CustomPrewarm](https://github.com/CMiSSioN/CustomPrewarm) | [customprewarm.md](customprewarm.md) | cache build fails on null lists (2018 game too) |
| [CustomUnits](https://github.com/BattletechModders/CustomAmmoCategories/tree/master/CustomUnits) | [customunits.md](customunits.md) | combat saves |
| CustomUnitSpawn | [customunitspawn.md](customunitspawn.md) | Unity 6 renamed `Random.Range` parameters |
| [EnvironmentalDesignMasks](https://github.com/BlueWinds/EnvironmentalDesignMasks) | [spam-and-edm.md](spam-and-edm.md) | combat saves |
| [ExpandedInitiative](https://github.com/BattletechModders/ExpandedInitiative) | [expandedinitiative.md](expandedinitiative.md) | combat saves |
| [FieldRepairs](https://github.com/BattletechModders/FieldRepairs) | [fieldrepairs.md](fieldrepairs.md) | co-op |
| [IRBTModUtils](https://github.com/BattletechModders/IRBTModUtils) | [irbtmodutils.md](irbtmodutils.md) | Unity 6 refuses small compressed textures |
| [IRTweaks](https://github.com/BattletechModders/IRTweaks) | [irtweaks.md](irtweaks.md) | combat saves |
| [MechEngineer](https://github.com/BattletechModders/MechEngineer) | [mechengineer.md](mechengineer.md) | combat saves |
| [MissionControl](https://github.com/CWolfs/MissionControl) | [missioncontrol.md](missioncontrol.md) | combat saves, co-op |
| [SoldiersPiratesAssassinsMercs](https://github.com/BattletechModders/SoldiersPiratesAssassinsMercs) | [spam-and-edm.md](spam-and-edm.md) | combat saves |
| [StrategicOperations](https://github.com/BattletechModders/StrategicOperations) | [strategicoperations.md](strategicoperations.md) | combat saves |
| [TisButAScratch](https://github.com/BattletechModders/TisButAScratch) | [tisbutascratch.md](tisbutascratch.md) | combat saves |

Some adapters aren't tied to one mod. [`ActorHydrateDeferral.cs`](../../tools/mod-bootstrap/ActorHydrateDeferral.cs)
and [`EncounterLoadDeferral.cs`](../../tools/mod-bootstrap/EncounterLoadDeferral.cs) hold back mod postfixes that run
too early on a save load. [`CoopSpawnDeterminism.cs`](../../tools/mod-bootstrap/CoopSpawnDeterminism.cs) seeds the
random numbers drawn by mods that hook unit spawning in co-op. [`ModStateCensus.cs`](../../tools/mod-bootstrap/ModStateCensus.cs)
is diagnostic only: it lists mod static fields at save and load and changes nothing.

## Game-code fixes that help mods

Described here because their code isn't published:

- The multiplayer content check hashes each type's files in id order instead of directory order, and treats CR LF as
  LF, so two players with the same mod pack can join each other.
- A load batch that raised the priority of a request with no loader yet no longer leaves the game loading forever.
  This hit every modded combat load; it's a retail bug.
- The extension-method scan keeps the types that did load when a mod assembly has one that can't, so contract
  results no longer show `ERROR: resolving 'ToPercentString'`. Also a retail bug.
- With in-mission saves on, a mod subclass of a saved game class (CustomUnits' mechs, for one) is saved with its
  nearest game class's save contract, and the game can find types in mod assemblies on load.
