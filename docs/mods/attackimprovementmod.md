# AttackImprovementMod

Code: [`AttackImprovementModCoopCalledShot.cs`](../../tools/mod-bootstrap/AttackImprovementModCoopCalledShot.cs). Kind: co-op, with CustomUnits.

**What broke.** With `CalledShotUseClustering` on (BTA), AttackImprovementMod swaps a called shot's hit table for one
clustered around the called location, built for its static `CurrentHitDirection`. It sets that only from
`HitLocation.GetMechHitTable`, which CustomUnits' `GetHitLocation` never calls, so each peer used what its last caller left:
the host's AI thinking had left `FromFront` (a hit on the left arm) and the guest `None` (the prone table, the left torso)
for the same roll.

**What UCU does.** In a co-op combat only, the adapter sets `CurrentHitDirection` to the attack's own direction (`ToProne`
for a prone target, otherwise `GetAttackDirection`) around `GetHitLocation` for CustomUnits' `CustomMech`, `TrooperSquad`
and `FakeVehicleMech`, and puts each peer's value back afterwards. It installs when CustomAmmoCategories loads.

**Upstream:** set `CurrentHitDirection` from the attack at the point of use, not from a side effect of one method.
