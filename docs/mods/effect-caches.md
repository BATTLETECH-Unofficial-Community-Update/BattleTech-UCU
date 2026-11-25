# RogueTechPerfFixes and CustomStatisticEffects

Code: [`EffectCacheLoadCompat.cs`](../../tools/mod-bootstrap/EffectCacheLoadCompat.cs) (run by
[`LoadSteps.cs`](../../tools/mod-bootstrap/LoadSteps.cs) after `_Init` on a load),
[`DesignMaskStickyLoadCompat.cs`](../../tools/mod-bootstrap/DesignMaskStickyLoadCompat.cs). Kind: combat saves, co-op. Both
mods ship with BattleTech Advanced.

**What broke.** Each mod keeps its own index of `EffectManager`'s effects and clears it in a `CombatGameState._Init`
postfix. On a load the game runs `_Init` after `EffectManager.Hydrate`, so both indexes were empty while every hydrated
effect was still in `EffectManager.effects`:

- RogueTechPerfFixes answers `GetAllEffectsTargeting` from its cache. After a load a unit showed only the effects added
  since (1 of 71 in one leg), so the status panel, ECM, auras and every other caller lost the rest for the whole combat.
- CustomStatisticEffects enforces stack limits against its own index, so a design mask's sticky effect, re-applied on load,
  was applied a second time on top of the saved stat. BTA's Rough made `ReceivedInstabilityMultiplier` 2.25 instead of
  1.5, and a co-op guest diverged from the host after every resync.

Retail BattleTech never loads a combat save (IRTweaks refuses them). UCU's combat-saves option and co-op resyncs are what
bring these mods to a load.

**What UCU does.** On a combat loading from a save, with combat saves on (the marker or a co-op combat), a
`CombatGameState._Init` postfix ordered after both mods' own rebuilds RogueTechPerfFixes' cache from
`EffectManager.effects`, grouped by target, and calls CustomStatisticEffects' own `Hydrate` postfix. It writes no stat.
Each mod is handled only when its assembly loads.

**Design-mask effect applied twice.** A combat save holds every effect, the design-mask sticky included, but not
`AbstractActor.occupiedDesignMask`. On a load, `ReapplyDesignMasks` (which CustomAmmoCategories defers to the actor's first
`ResetPathing`, still inside the load) finds no mask and applies the sticky again. Retail's `CreateEffect` would only refresh
it at its stack limit, but CustomStatisticEffects always adds the effect, so every guest resync reload added one `DesignMask`
copy that the host, which never reloads, lacked, and the effects digest split. With CustomStatisticEffects loaded, while a
combat loads from a save with combat saves on, [`DesignMaskStickyLoadCompat.cs`](../../tools/mod-bootstrap/DesignMaskStickyLoadCompat.cs)
sets `occupiedDesignMask` to the mask the game is about to compute from the same cell, before `ReapplyDesignMasks` runs, so the
game's own equality guard skips the re-apply and the hydrated effect stands. It writes no effect, statistic or cell list.

**Upstream:** rebuild the index after `_Init` on a load, or read `EffectManager.effects` instead of keeping a second copy.
