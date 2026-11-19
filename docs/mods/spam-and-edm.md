# SoldiersPiratesAssassinsMercs and EnvironmentalDesignMasks

Mod source: [SoldiersPiratesAssassinsMercs](https://github.com/BattletechModders/SoldiersPiratesAssassinsMercs), [EnvironmentalDesignMasks](https://github.com/BlueWinds/EnvironmentalDesignMasks).

Code: [`StartDialogueSaveCompat.cs`](../../tools/mod-bootstrap/StartDialogueSaveCompat.cs). Kind: combat saves; fails on the 2018 game too.

**What broke.** Both mods prefix `TurnDirector.QueuePilotChatter` and build a one-time start-of-battle dialogue
(`Chunk_Dialogue_SPAM_<faction>`, `Chunk_Dialog_<mood>_startMission`) under the encounter layer. They never run
`AlwaysInit` on it, so its `MessageMemory` has no owner, and `EncounterLayerData.BeforeSave` throws "Owner GUID is null
or empty!", which fails the whole save. A load wouldn't rebuild those objects either.

**What UCU does.** While the encounter layer is saved, those chunks are switched off, so the save doesn't see them.
They come back on afterwards. Their GUIDs are also left out of the item registry's debug lists, so the load doesn't
report them missing. A chunk is touched only when its mod is loaded, its name matches, and it has no owner.

**Upstream:** call `AlwaysInit` on the chunk you build, or remove it once the dialogue has played.
