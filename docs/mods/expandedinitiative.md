# ExpandedInitiative

Mod source: [ExpandedInitiative](https://github.com/BattletechModders/ExpandedInitiative).

Code: [`ExpandedInitiativeSaveCompat.cs`](../../tools/mod-bootstrap/ExpandedInitiativeSaveCompat.cs). Kind: combat saves; same on the 2018 game.

**What broke.** The mod widens phases to 1–10 and postfixes `AbstractActor.HasValidInitiative` to accept them, but the
postfix takes `bool __result` by value, so its answer never reaches the caller. On save and load the game still applies
its 1–5 rule, logs an error, and puts a unit saved on phase 6–10 back on its base phase.

**What UCU does.** Applies the rule the mod's postfix computes, using the mod's own phase limits. It steps aside if the
postfix takes `ref bool __result`.

**Upstream:** declare the parameter as `ref bool __result`.
