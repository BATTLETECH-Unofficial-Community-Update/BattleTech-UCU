# MonsterMashup

Code: [`MonsterMashupCoopLinkSync.cs`](../../tools/mod-bootstrap/MonsterMashupCoopLinkSync.cs). Kind: co-op. It ships with
BattleTech Advanced.

**What broke.** MonsterMashup links turret "vehicles" to a monster or DropShip parent (in BTA: Overlord, Union, Rattler,
RattlerSL) only when the units spawn, and keeps the links in static `ModState`, which `ModState.Reset` empties when a guest's
combat is destroyed for a resync reload. Nothing rebuilds them on a load. With the links gone, only the guest lets linked
turrets block their parent's line of fire, never spawns support units, marks a flee-capable parent as fleeing at once,
doesn't restrict weapon firing arcs, and doesn't move a child with its parent.

**What UCU does.** In a co-op combat with MonsterMashup loaded, the host sends each parent's GUID, its two linked lances,
and each child's GUID, attach transform and CustomUnits link, through `ModStateCarry` to a peer that has just loaded a
snapshot, which rebuilds the links.

**Upstream:** rebuild the links from the units' own data on a load, or save them.
