# CBTBehaviorsEnhanced

Code: [`CBTBehaviorsEnhancedCoopDeterminism.cs`](../../tools/mod-bootstrap/CBTBehaviorsEnhancedCoopDeterminism.cs),
[`CBTBehaviorsEnhancedCoopStartupDecision.cs`](../../tools/mod-bootstrap/CBTBehaviorsEnhancedCoopStartupDecision.cs). Kind: co-op. It ships with
BattleTech Advanced.

**What broke.** CBTBehaviorsEnhanced rolls the location of each extra damage cluster of a fall, a melee attack and a
death-from-above in `AttackHelper.CreateImaginaryAttack` with its own unseeded `System.Random` (`Mod.Random`). In co-op the
two peers' generators differ, so the same knockdown hit the left torso and right arm on the host and the left arm and left
torso on the guest, and the unit compare resynced the guest.

**What UCU does.** In a co-op combat only, the adapter seeds that generator around `CreateImaginaryAttack` from the contract
GUID, the attacker, the target, whether it is a melee attack and its damage type, the cluster count, the round and the
phase, and puts each peer's own state back afterwards. Single player is untouched.

**Startup checks.** When the AI's startup check fails, CBTBehaviorsEnhanced keeps the unit shut down and runs its pilot
injury, system failure and ammo explosion checks in `MechStartUpNode.Tick`, which runs where the AI thinks, the host only.
No invocation carried the results, so the guest kept a component, an ammo box or pilot health the host lost.
`CBTBehaviorsEnhancedCoopStartupDecision` records each check's arguments, `UnityEngine.Random.state` and threshold results on
the host, and sends them with the AI's next invocation through `CoopDecisionChannel`; the other peers make the same call with
those results forced.

**Upstream:** seed the generator from the attack's own data so both machines agree with no help.
