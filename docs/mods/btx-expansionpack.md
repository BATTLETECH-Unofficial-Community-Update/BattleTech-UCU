# BTX_ExpansionPack (BEX:T submods)

Mod source: part of the BattleTech Extended (BEX:T) 2.0 submods; seen with BTX_ExpansionPack 1.4.2 and 1.4.3.

Code: [`UnityInOverloadLookupCompat.cs`](../../tools/mod-bootstrap/UnityInOverloadLookupCompat.cs). Kind: Unity 6 difference.

**What broke.** Unity 6 adds a second public `Vector3.Distance(in Vector3, in Vector3)` beside the `Vector3.Distance(Vector3,
Vector3)` that Unity 2018 had. 138 UnityEngine methods gained such an `in` twin. The ArtilleryCrits transpiler looks the method up
by name alone (`AccessTools.Method(typeof(Vector3), "Distance")`), which finds one method on the 2018 game and throws "ambiguous
match" on UCU. The throw fails the mod's whole Harmony setup, so none of its patches apply and the main menu shows "There was a
problem loading the Expansion Pack. Check your install, then restart the game." (GitHub issue 42).

**What UCU does.** When a mod looks up a UnityEngine method by name alone, and the only other methods of that name are the
`in` or `ref` twins of a by-value one, UCU returns the by-value method: the one the 2018 game returned and the one the mod's
code calls. Every other lookup is unchanged. This applies to every mod, not only this one.

**Upstream:** give the lookup its parameter types, for example `AccessTools.Method(typeof(Vector3), "Distance", new[] {
typeof(Vector3), typeof(Vector3) })`.

**Not a UCU difference:** with BEX:T 2.0 base mods installed, ModTek also refuses CustomUnits and BTX_CAC_Compatibility (both
declare a conflict with MechResizer, and CAC_Compatibility with BTMLColorLOSMod), and so BTX_ExpansionPack, BTX_PlayableVehicles
and Lore_Pack_4SW, with the same message. The 2018 game does the same. Turn MechResizer and BTMLColorLOSMod off; Lore_Pack_4SW
also needs MoreMissions and CompanyDecorations.
