# Abilifier

Mod source: [Abilifier](https://github.com/ajkroeg/Abilifier).

Code: [`AbilifierBarracksPanelCompat.cs`](../../tools/mod-bootstrap/AbilifierBarracksPanelCompat.cs). Kind: Unity 6 difference;
Abilifier works on the 2018 game. Reported with XAI BattleTech (GitHub issue 36) and with BTA Light, where it shows on the
level-5 ability pips.

| Pack | Abilifier | Lookup in `Abilifier.Framework.Helpers.SetTempPilotSkill` | UCU |
|---|---|---|---|
| XAI BattleTech | 1.2.0.2 | `FindObjectsOfTypeAll<SGBarracksAdvancementPanel>().First()` | patched |
| BTA Light 19.7.1.lite | 1.4.1.4 | the same | patched |
| BTA Ultimate 20.3 | 1.4.2.0 | takes the panel as a parameter (the upstream fix below) | logs `abilifier-panel\|skipped`, patches nothing |

**What broke.** In the Barracks, Abilifier handles every skill-pip click itself. Its `Helpers.SetTempPilotSkill` finds the
advancement panel with `Resources.FindObjectsOfTypeAll<SGBarracksAdvancementPanel>().First()`. That list also holds panels
on loaded prefabs and inactive copies, in no promised order. When the first one isn't the panel on screen, the click
updates nothing: no XP is spent and Confirm stays grey.

**What UCU does.** It remembers the panel the player clicked, and in that one method only, `First()` returns that panel
when the list holds it (otherwise the list's first entry, as before). The method is patched at the first pip click, so
nothing changes until then, and nothing at all without Abilifier.

**Upstream:** pass the clicked panel (`__instance` of the `OnValueClick` prefix) down to `SetTempPilotSkill` instead of
looking it up.

**Co-op: pilot resolve after a resync.** Abilifier keeps each pilot's resolve in a process-wide table
(`PilotResolveTracker`), not in the combat save. In co-op the host keeps playing while a guest reloads the host's combat
for a resync, so a guest that reloads after a round has begun missed that round's resolve gain: the host then granted
Inspired and the guest did not, and the next compare tore the session down. UCU sends the host's resolve table with the
combat it sends for a resync (the `abl-resolve` row of
[`LoadSteps.cs`](../../tools/mod-bootstrap/LoadSteps.cs)), and the guest takes it before combat resumes. Nothing changes
in single player or without Abilifier.
