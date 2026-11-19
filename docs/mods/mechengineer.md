# MechEngineer

Mod source: [MechEngineer](https://github.com/BattletechModders/MechEngineer).

Code: [`MechEngineerSaveCompat.cs`](../../tools/mod-bootstrap/MechEngineerSaveCompat.cs). Kind: combat saves.

**What broke.** MechEngineer treats a shared `MechDef` in the DataManager as stock only while its GUID is null. A combat
save gives every MechDef it writes a GUID, including the shared ones, and they keep it after the save.

**What UCU does.** Keeps the GUIDs the save assigned through both serialization passes, then clears them again on the
shared defs before anything else can read them.

**Upstream:** don't use a null GUID as the "stock" marker.
