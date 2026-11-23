# PracticeMakesPerfect

Code: [`PracticeMakesPerfectCoopStratCom.cs`](../../tools/mod-bootstrap/PracticeMakesPerfectCoopStratCom.cs). Kind: co-op.

**What broke.** PracticeMakesPerfect keeps the company's active StratCom in `SpecHolder.activeStratCom` and fills in
`StrategicResolve` as a default when a save is rehydrated. A host that created its career and went straight into a co-op
contract had never rehydrated, so its holder stayed null. The guest rehydrated the host's snapshot and got the default.
Only the guest then applied the StratCom's effects (`StatusEffect-OfficerTrainee` and +2 `MoraleBonusGain` on every
player 'Mech), so the first unit compare differed on every unit.

**What UCU does.** Only with PracticeMakesPerfect loaded and only on a campaign client (the first load and every later
snapshot apply), the adapter reads the `PMP_ACTIVESTRATCOM_` company tag before PracticeMakesPerfect's `Rehydrate`
postfix consumes it, and after that postfix writes the host's value, null included, into the holder. The host and single
player are untouched. It logs `pmp-coop-stratcom|mirrored|host=<value>|pmp-default-was=<value>`.

**Upstream:** set the holder from the company tag when a career is created, not only when a save is rehydrated, so a
career that never loaded a save has the same holder as one that did.
