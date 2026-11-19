# CustomAmmoCategories

Mod source: [CustomAmmoCategories](https://github.com/BattletechModders/CustomAmmoCategories/tree/master/CustomAmmoCategories).

Code: [`CustomAmmoCategoriesSaveCompat.cs`](../../tools/mod-bootstrap/CustomAmmoCategoriesSaveCompat.cs). Kind: combat saves; fails on the 2018 game too.

**What broke.** CustomAmmoCategories skips the game's `ReapplyDesignMasks` call in `AbstractActor.Init` and makes it
later, from `ResetPathing`. In a new battle that's harmless. On a load, that skipped call is the only place
`occupiedEncounterLayerCells` is set, so `CheckEnteredCellsForRegions` reads null and the load stops at the first unit.

**What UCU does.** On a load, before `AbstractActor.Init`, an actor whose list is still null gets the list the skipped
call would have set. CustomAmmoCategories still applies the masks from `ResetPathing`.

**Upstream:** when skipping `ReapplyDesignMasks`, set `occupiedEncounterLayerCells` if it is null.
