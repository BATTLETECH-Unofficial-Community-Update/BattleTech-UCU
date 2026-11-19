# CustomUnitSpawn

Mod source: part of RogueTech; no public repository found.

Code: [`CustomUnitSpawnCompat.cs`](../../tools/mod-bootstrap/CustomUnitSpawnCompat.cs). Kind: Unity 6 difference.

**What broke.** Unity 6 renamed `UnityEngine.Random.Range`'s parameters from `min`/`max` to `minInclusive`/`maxInclusive`
(float) and `minInclusive`/`maxExclusive` (int). CustomUnitSpawn 1.0.0.8 binds its Harmony patch arguments by the old
names, so the patches fail to apply.

**What UCU does.** Lets the old names bind to the new ones, for that mod's patches on those two methods only.

**Upstream:** bind the arguments by position (`__0`, `__1`) instead of by name.
