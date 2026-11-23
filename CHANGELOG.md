# Changelog

## 2.3.0-4B (BT-UCU Beta 4)

### New BT-UCU Team Members

We'd like to welcome @wyaeld and @gkirkpatrick to the development team. Both have been very resourceful in helping triage issues even before they had access to do very much, and we're excited to grow the BTUCU tent and leverage their experience! Beta 4 incorporates several updates from @gkirkpatrick's BTATuneUp project. 

### Multi-core rendering with DirectX 12, new netcode, modpack + MP pass, camera fixes, MP speed-up enabled

The headline updates for Beta 4 are meaningful DirectX 12 support with multi-core rendering (as an opt-in selection on the launcher, but one we'd like to make the default); numerous multiplayer/modpack issues that came about from investigating issue #24, which amounted to 'we were really shortsighted when we launched beta 1, because we tested modpacks and we tested multiplayer, but we didn't test modpacks in multiplayer' and now we have eaten our spinach to the tune of a great part of the below list, as well as much expanded documentation on [what UCU changes so that mods will work in MP and with combat saves](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/blob/main/docs/mods/README.md). A fix for 'why is this camera showing me nothing' that sometimes happens in retail; and a from-scratch re-write of the career/campaign netcode that leaves behind a previous, biblical dedication to reusing retail code even though retail code wasn't intended for stratcom-level multiplayer.  

- **Direct3D 12 with multi-core rendering is now an option in the launcher.** In the launcher's *Settings* tab, the third graphics choice, *DirectX 12 with multi-core rendering*, starts the game on Direct3D 12 and spreads the rendering work across several of your CPU cores. It is opt-in: DirectX 11 remains the default. On a laptop with an Intel Core Ultra 7 255HX and an NVIDIA GeForce RTX 5080 Laptop GPU, during the first minute of a quiet combat (a Last 'Mech Standing contract at 1920x1080 with ModTek loaded), the game averaged about 87 frames per second on DirectX 11, about 91 on plain DirectX 12 and about 104 with multi-core rendering, with no crash during play and no graphics driver reset. That is one run of each option on one PC, so mileage may vary. Let us know your results, particularly in busy fights. 
- **DLC, effects and 'Mech graphics now draw with UCU's updated shaders, including with mods.** The DLC, weapon and effect, and 'Mech asset bundles (45 in all) used shader copies built for the 2018 game; they now use the same updated shaders as the rest of UCU. With ModTek or BattleTech Advanced, the game now loads UCU's shaders too, where it used to load the original 2018 file. Nothing should look different; if something does, it is a bug, and we want to hear about it.
- **The percent and money text on contract results and in events no longer logs an error.** On installs with no mods (GOG included), the game logged an error while it built these lines.
- **Mod map loading failures throw correctly.** When a mod supplies a map in an asset bundle and the bundle is missing or fails to open, the game used to treat the load as a success and the map never started, with no error from the loader. It now stops with an error. Mod maps in an asset bundle now load, which they never did. The original game's maps load as before.
- **With mods, a mod's hover text no longer stays on the cursor** ([issue #31](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/31), [issue #21](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/21)). A mod's tooltip, such as the Combat Log mod's icon tooltips, stayed on the cursor after the pointer left the icon. UCU now clears it between screen updates, as the original game did. Nothing changes without mods.
- **With mods, a mod's coloured text now draws in its intended colour** ([issue #21](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/21)). Text a mod drew in a colour came out too dark. A mid-grey label showed at about half its brightness, which hid the Combat Log mod's resize grip against its button. UCU now draws it as the original game did. Nothing changes without mods.
- **Saves made inside a MissionControl custom contract now load** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). In 2.2.2-3B and 2.2.3-3B, with *in-mission saves (BTA)* ticked in the launcher, loading a save made inside a contract type that MissionControl adds (DuoDuel, the Gunslingers missions, DefenceInDepth) stopped with an error and the battle never finished loading. The same fault made a co-op player's rejoin to such a mission fail with REJOIN FAILED. UCU's MissionControl save adapter now restores what MissionControl sets up for these contracts when a save loads, and saves you already have load. Contracts from the original game load as before.
- **With in-mission saves on and MissionControl loaded, enemy lances spawn again** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). In 2.2.2-3B and 2.2.3-3B, with *in-mission saves (BTA)* ticked in the launcher, a lance's orders loaded empty on MissionControl contracts. The game then failed to start that lance (the Ambushers in Trap Sprung, for one), and in co-op the two players saw different numbers of enemy 'Mechs: 1 on the host and 3 on the guest. UCU's MissionControl save adapter no longer does this. Games without in-mission saves are unaffected.
- **With in-mission saves on, MissionControl's extra enemy lance is no longer built twice after a load** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). In 2.2.2-3B and 2.2.3-3B, with *in-mission saves (BTA)* ticked in the launcher, loading a combat save made in a contract where MissionControl had added an extra lance built that lance a second time. The game logged "Duplicate guid detected" and "No MessageMemory for OwnerID" after every such load, and only contracts where MissionControl had rolled an extra lance showed it. UCU's MissionControl save adapter now skips the second build, and the lance, its spawn points and its objective are the same before the save and after the load.
- **Co-op with CustomUnits: both players now get the same enemy units** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). CustomUnits drops units tagged for other biomes from a contract's pick list, but the co-op guest's game never told it which contract was running. The guest's list kept units the host had dropped, so the two players spawned different units at the same spawn point, and the game's first-round check caught that and disconnected the pair. UCU now sets that contract on both players' PCs. Single player is unaffected.
- **Co-op: orders given while a player's game reloads after a resync are no longer lost to that player** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). When a player's game disagreed with the host's, it downloaded the host's campaign and reloaded the battle. If the other player gave an order in that time (a move, a shot, DONE), the host and the other players ran it and the reloading player's game never saw it, so the games disagreed again and a second resync could stall for good. The game now holds those orders until the reloading player reports it has loaded, then plays them in the order they arrived. Single player is unaffected. Other causes of a disagreement are separate faults.
- **Co-op with IRTweaks: both players now roll the same combat dice** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). IRTweaks draws its combat rolls (its pilot-skill bonuses to a hit's damage, heat, action points and stability, the knockdown and forced-shutdown checks after an attack, and the pain-tolerance check on an injury) from a random generator that each player's game seeded separately. One player's 'Mech could take a stability bonus that the other player's never got, the game saw the two battles differ, and it resynced the guest. In co-op, UCU now seeds those rolls from the contract and the attack or pilot involved, so both players' games draw the same numbers. Single player and games without IRTweaks are unaffected.
- **Co-op: a resync requested just after the host announces the next unit's turn is no longer left waiting** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). The host takes a resync only at a safe point in the turn. A resync requested just after the host had announced the next unit's activation found no such point, and could wait there, stalling the round. The host now also takes the resync once that announcement has waited two seconds for the other players with no order running.
- **Co-op: both players now decide what follows a unit's turn at the same moment** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). When a unit finished acting, the game chose what came next (the same side again, the other side, or the end of the phase) when each player's own animation finished. A slower animation on one PC could let the two games choose differently: one re-activated a side while the other ended the phase, and the check between the games then failed. In co-op the choice now waits for the same point in the host's order stream on every PC. This also covers BattleTech Advanced, whose own mod replaces that game step. Single player is unaffected.
- **Co-op with CustomAmmoCategories: the host's AI no longer leaves weapon modes changed after a brace check** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). To find a weapon that can fire indirectly, CustomAmmoCategories tries every firing mode of every weapon when the AI braces, and left the weapons it did not pick in the last mode it tried (an enemy's anti-missile system left Off, for one). Only the host runs the AI, so only the host's game had the changed mode, the game saw the two battles differ, and it resynced the guest. In co-op, UCU now puts those weapons back. Single player is unaffected.
- **Co-op with StrategicOperations and FieldRepairs: both players now roll the same dice** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). Both mods draw their random rolls from a generator seeded by the clock, so in co-op each player rolled separately: StrategicOperations' support-ability picks, swarm, deswarm and strafe targets, battle-armour death and mount checks and damage-location picks, and FieldRepairs' poorly-maintained damage on contract start. UCU now seeds those rolls from the contract and the roll's place in the mod's code, so both players draw the same numbers. Single player and games without these mods are unchanged.
- **Co-op with mods: a resync or rejoin now carries the mods' state with in-mission saves off** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). When a co-op player's game disagrees with the host's, or a player rejoins, the host sends a combat save. With BTA that save needs UCU's mod save support, which switched on only when *in-mission saves (BTA)* was ticked in the launcher. With it unticked, the default, the host's save left out the mods' state and the other player's load ran without UCU's mod save adapters. UCU now switches that support on for the length of a co-op combat, and off again when it ends. Single player with the box unticked is unchanged.
- **With in-mission saves on, saves made in a MissionControl contract keep the contract's extra encounter objects** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). MissionControl adds five encounter types (the unit-swap, faction-swap, pilot-death and dialogue objects and its random chunk-state object) as subclasses of a game class that is abstract, and UCU's save support could not give them a save format, so the save dropped the contract's encounter objects and the loaded mission found none. UCU now gives them the format of the game class they extend. A DuoDuel save made mid-mission and loaded in the same session came back with its encounter objects, and the mission played on to Victory.
- **With BTA and in-mission saves on, a loaded battle no longer loses or doubles effects** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). Two BTA mods, RogueTechPerfFixes and CustomStatisticEffects, keep their own lists of a battle's effects and empty them while a load is still setting up, after the saved effects were already restored. After a load a unit showed 1 of its 71 effects on the status panel and in the checks that read them, and a design mask's sticky effect was applied a second time on top of the saved stat, so BTA's Rough gave 2.25 instead of 1.5 on received instability. UCU now rebuilds both lists after a load. A co-op resync is a load, where this also made the other player diverge from the host after every resync.
- **With CustomAmmoCategories and in-mission saves on, weapons keep their firing modes through a load** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). A loaded weapon came back in its base mode: an Artemis IV launcher lost its Artemis mode, a Gyrojet's ammo feed lost its mode, and an anti-missile system switched Off came back On. Mechs of the AI also lost the modes their attached equipment adds, because the save does not carry the links between them. UCU now restores the links and each weapon's saved mode and ammo after a load. In co-op each resync is a load, and every one reset the other player's Artemis launchers and Gyrojets.
- **Co-op with PracticeMakesPerfect: the other player no longer gets a StratCom the host doesn't have** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). PracticeMakesPerfect remembers the company's active StratCom, and fills in a default one when a save is loaded. A host that created its career and went straight into a co-op contract had never loaded a save, so it had none, while the other player loaded the host's campaign and got the default. Only that player then had the StratCom's effects: the Officer Trainee effect and +2 morale gain on every one of the player's 'Mechs, and the first check of the two players' units disagreed on every one. UCU now gives the other player the host's StratCom, or none when the host has none. The host and single player are unchanged.
- **With CustomActivatableEquipment and in-mission saves on, a loaded battle no longer applies an aura twice** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). Its aura equipment (a Guardian ECM, for one) keeps a list of the aura effects it has applied, and the list starts empty on a load while the game restores the saved effects. The next time the aura ran, it applied each effect again on top of the restored one, so a Guardian ECM's interference effect was on its carrier twice. UCU now adds the restored effects to that list first, so none is created again.
- **Co-op: a mismatch between the two players' units now resyncs the other player instead of disconnecting both** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). The host's game decides whose copy of a unit is right when the two players' stats differ, and it chose before anyone had connected, so it fell back to whichever player reported first. When it picked the other player, the host refused to repair itself, told both players their data was bad, and 5 seconds later both got the desync popup and disconnected. UCU now makes the host's own game the reference for every co-op comparison, so a mismatch resyncs the other player. Skirmish multiplayer is unchanged.
- **Co-op with CustomActivatableEquipment: a C3 slave's aura is no longer doubled after a resync** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). A co-op player's game that reloaded the host's save after a resync applied each C3 slave aura again on top of the restored one, so the effect on a slave went from -1 to -2 on that player after every resync while the host stayed at -1, and the next checks ran into the resync limit. UCU's aura fix worked only up to the start of the encounter, and the C3 aura applies a few seconds after it. It now works for the whole of a loaded battle.
- **Co-op with PanicSystem: both players now roll the same panic saving throws** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). PanicSystem rolls a pilot's saving throw with the game's shared random generator when an attack completes, a move or melee ends, or an activation ends, and the two players' generators are never in step. The same damage could give one player a successful save and the other an Unsettled pilot, and the checks then resynced them. UCU now seeds those rolls from the contract and the shared sequence, round and phase, so both players roll the same numbers.
- **Co-op with CustomAmmoCategories: a weapon's firing mode and ammo choice now reach the other player** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). The mode or ammo a player picks on a weapon from the HUD was a local choice that nothing sent to the other player, so the two games fired different modes, and when a check caught the difference, the resync reloaded the host's mode and a guest's own choice was lost. UCU now sends the change to the other player at once, through a new channel that lets mods attach data to co-op traffic, and a player that has loaded a resync or rejoin snapshot gets the host's full state.
- **Co-op with CustomAmmoCategories: a weapon-mode click now lands at the same point for both players** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). A weapon's mode or ammo click reached the other player a moment after the click and was applied on arrival, so a check of the two players' units that ran in between saw different values and cost one of the three resyncs. The host now holds the click and sends it just before the next order, every player applies it right before that order runs, and until then the clicking player's game reports the weapon's old mode to the check, so both players compare the same value either side of that point. A click that is lost or misapplied still fails the check.
- **Co-op with TisButAScratch: both players now roll the same pilot injuries** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). TisButAScratch picks each combat injury (its location, the injury and its effects) and its bleed-out tick with the game's shared random generator, and the two players' generators are never in step. The same hit could give the host a head injury and the other player a torso bleed, and the checks then resynced them. UCU now seeds those rolls from the contract, the unit, the pilot's injuries, the damage, the round and the phase.
- **Co-op with CustomAmmoCategories: both players now place a missile volley with the same random draws** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). For indirect fire, CustomAmmoCategories places each missile with the game's shared random generator, and the two players' generators are never in step, so the same volley could hit different units and locations on each player's screen, and the checks then resynced them. UCU now seeds those draws from the contract, the attack, the volley group and the weapon.
- **Co-op launch no longer hangs on a missing pilot portrait** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). When a lance pilot's portrait icon was missing from the game's data, as one pilot's was in the reported save, a co-op launch waited for it forever and both players sat on the loading screen. The game now starts the mission without the icon and logs a warning naming what is missing. Single player always did this.
- **Co-op with CustomUnits: one player seeing an enemy first no longer starts a resync** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). CustomUnits notes, per player, which enemy units that player has seen. The game's co-op check compared that note along with a unit's other stats, so when the host had seen an enemy that the guest had not, the check called the two games different. The guest saw "Your game disagreed with the host's. Resynchronizing with the host (1 of 3)", waited while the host's campaign downloaded, and used up one of the contract's three resyncs; after the third, the next one ended in REJOIN FAILED. The co-op check now leaves that one note out. Nothing CustomUnits does with it changes.
- **With CustomUnits and in-mission saves on, loading a save no longer throws an error** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). In 2.2.2-3B and 2.2.3-3B, with *in-mission saves (BTA)* ticked in the launcher, loading a save made in combat with CustomUnits loaded logged an error from CustomUnits as the battle loaded. This happened on original-game and MissionControl contracts alike. UCU's CustomUnits save adapter was meant to prevent it but never installed. It now does, and the load completes without the error.
- **Co-op over Steam: invite friends from the game** ([issue #29](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/29)). On Steam, co-op now uses Steam's connection by default. When a co-op host opens a campaign, the game also opens a friends-only Steam lobby. The host's Multiplayer panel shows **INVITE FRIENDS**, which opens the Steam invite dialog, and a friend who accepts the invite, or uses *Join Game* from Steam, joins the host without typing an address or forwarding a port. The host still listens for direct connections by address and on a local network at the same time, so players who join that way (a GOG player joining a Steam host by address), connect as before. The Multiplayer panel's "Nobody can connect?" help now names INVITE FRIENDS and Join Game before port forwarding. Starting the game with `-netTransport lidgren` turns Steam co-op off. A host whose Steam is unavailable uses direct connections only.
- **Co-op on GOG: invite friends from GOG Galaxy.** On a GOG install, a co-op host now gets a friends-only GOG Galaxy lobby with an **INVITE FRIENDS** button, and friends join with **JOIN** from their GOG Galaxy friends list. LAN and Direct Address work on GOG as before. This is untested (we don't have multiple GOG accounts) so report issues if you find them.
- **Co-op: *Speed up combat* and the fast-forward key now work** ([issue #30](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/30)). The original game ignored both in any network game, so in campaign co-op they did nothing. The host's *Speed up combat* setting now applies to everyone in the contract; a guest's own toggle has no effect in co-op combat. Pressing fast-forward during a move or an attack speeds it up for all players. The camera-follow sliders were already per player and are unchanged.
- **Co-op with large modpacks: shorter stalls when the campaign is sent.** In co-op, when the host moves to another room, the game sends the whole campaign to the other player, and with a large modpack like BTA that is about 11 MB. UCU now does most of the host's work of building that snapshot on another thread, unpacks it on the other player's side off the main thread, and moves every waiting network message each frame instead of one. On a BTA MP session, the host's freeze on a room change went from about 6 seconds to about 2, and the other player's longest freeze from about 6 seconds to about 4. Both players still pause for a few seconds on a room change, and the time the transfer itself takes did not change. Single-player is unaffected.
- **Co-op: the other player's jump jets, melee and death-from-above work again** ([issue #33](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/33)). The host checks that each order a player sends is for one of that player's own units. That check only recognised moves, shots, sensor locks, reserve and inspire, so the host dropped every other order from the non-hosting player without running it: a jump, a melee or death-from-above attack, standing up, restarting a shut-down 'Mech, venting heat, ejecting, an active probe, a morale ability or a mortar strike. The player could plan the jump, and then nothing happened. The host now recognises the unit in all of these orders, and in orders that mods add when they name their unit the same way, such as CustomUnits' manual shutdown in BattleTech Advanced. The host's own orders and single player are unaffected.
- **The melee camera shows the hit.** Courtesy of @wyaeld, the close-up cinematic camera for a melee attack now starts where it was meant to, instead of flying in from the overhead view, and it moves up when a slope between it and the fight would hide the 'Mechs. Ranged, death-from-above and movement cameras are unchanged. Thanks to wyaeld, who found both causes and wrote the fixes.
- **The Mech Bay no longer piles up hidden copies of 'Mech models.** (@gkirkpatrick's BTATuneUp project) Every visit to the Mech Bay added another hidden copy of each displayed 'Mech's model to the game's object pool, and none was ever used again, so a long session kept growing. A model the pool already holds enough copies of is no longer copied. The original game has this bug too.
- **Urban Warfare's building list is cleared when a battle ends.** (@gkirkpatrick's BTATuneUp project) The list was filled on the first city map of a session and never refreshed, so it kept that map's buildings, and the battle they belonged to, until you quit. On later city maps, changing the *urban cars* option during a battle updated those old buildings instead of the current map's. The list is now cleared when each battle ends. The original game has this bug too. 
- **The loader's cap on light loads in flight is 512 instead of 10.** (@gkirkpatrick's BTATuneUp project) With the original game's cap of 10, the loader stopped starting a frame's pending requests as soon as 10 light ones were running and left the rest for the next frame, which it did in nearly every frame of a career load and of an in-game reload. The heavy-load cap and the debug-console setting for both are unchanged, so mods that set the cap still do.
- **The first-launch welcome popup no longer appears.** The original game showed a Paradox welcome message the first time it started, naming online services that UCU does not offer. UCU skips it and marks it as seen, as closing it did. The privacy notice still shows on a first launch.
- **Co-op: after a resync, a critical hit lands on the same component for both players** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). A fresh battle lists a 'Mech's components in the order of its loadout, but loading a saved battle lists them grouped by type: weapons, ammo, jump jets, then the rest. The other player reloads the host's save on every resync and the host does not, so a critical-hit slot named a different component on each: the same roll exploded SRM ammo on the host and broke a jump jet on the other player. UCU now puts each unit's components back in loadout order after a co-op load. Single player keeps the original game's order.
- **Co-op with BTA: a resync no longer adds a second terrain effect to the other player's units** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). A saved battle holds each unit's terrain (design mask) effect, such as Rough's, but not which mask the unit stands on, so loading it applied the effect again. The other player reloads the host's save on every resync and the host does not, so each reload gave that player one more copy of the effect than the host, and the checks of the two players' effects disagreed (42 against 41 on one unit). UCU now sets the unit's mask from its cell before the game reapplies masks, so the saved effect stands and no second copy is added.
- **Co-op with CBTBehaviorsEnhanced: falls and melee hits now land their extra damage on the same locations for both players** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). CBTBehaviorsEnhanced rolls the location of each extra damage cluster of a fall, a melee attack or a death-from-above with its own random generator, which each player's game seeds separately, so the same knockdown hit the left torso and right arm on the host and the left arm and left torso on the other player, and the checks then resynced them. UCU now seeds that generator from the contract, the attacker, the target, the attack's kind, the cluster count, the round and the phase.
- **Co-op with AttackImprovementMod: a called shot on a CustomUnits unit now uses the same hit table for both players** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). With clustered called shots on (BTA), AttackImprovementMod builds the table for a called shot around the called location using the hit direction it last saw, and CustomUnits' units never set that direction, so each player used what its own last caller left: the same roll hit an arm on the host and a torso on the other player, whose table was the prone one. UCU now sets the direction from the attack itself (from the prone side for a prone target) when a CustomUnits mech, trooper squad or fake vehicle is hit.
- **Co-op: a resync now waits for a fall or knockdown to finish** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). The other player then reloads the unit already down, with its fall damage applied.
- **Co-op with PanicSystem: a resync no longer makes the other player miss a panic check the host makes** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). PanicSystem keeps its pending damage and panic levels only in memory, so a resync reload reset them on the other player and not on the host, and the host rolled panic saves the other player never rolled, leaving a unit Unsettled on the host's screen only. The host now sends that state to the other player when their snapshot has loaded.
- **Co-op: a pilot bonus that ends after a resync now wears off** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). The game saves no stat history for a pilot, so after a load an effect on a pilot that ended later could not take its change back. In BTA, a pilot's Life Support gear bonus stayed when the 'Mech died or lost the part after a resync, and the players' checks then disagreed. The host now sends each pilot's stat history with a co-op resync, and loading a co-op combat save rebuilds it from the restored effects. Single player is unchanged.
- **Co-op: the other player's game now runs the orders the host relays in the host's order** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). The other player runs each relayed order before the next unit activates, and in the order the host gave them. Orders the host gives while a resync is under way reach the other player once it has reloaded, and none is run twice.
- **Co-op: both players now spawn the same enemy units in lances whose difficulty is set dynamically** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). Before, the host and a guest could see different mechs in the same lance, with or without mods.
- **Co-op with BTA mods: more of the mods' state is now kept in step between the two players** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). This covers anti-missile, heat, knockdown and death state, artillery, mines, fires, jamming and other mod rolls, equipment toggles, and StrategicOperations abilities and dismounts.
- **Co-op: a resync freezes the game for less time, and a resync where only the battle disagrees reloads only the battle** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). The host now announces the resync before it builds the save, so the other player starts leaving the battle while the host builds and sends, and when the two players' campaigns agree, only the battle is sent and reloaded.
- **Leaving a battle no longer logs an error or leaves pilot voice-line objects behind.** After a battle ended, the original game kept its pilot voice-line queues running, which logged an error and kept them in memory.

## 2.2.3-3B (BT-UCU Beta 3.3)

- **The drop ship on the mission briefing and loading screen no longer turns into a magenta rectangle** ([issue #28](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/28)). The change in 2.2.2-3B that releases the copies UCU makes of the original game's materials when a map unloads ran too early: while the next map was still loading, it found nothing using the drop ship's materials and freed them, although the loading screen still drew them. It now waits until the load has finished.

## 2.2.2-3B (BT-UCU Beta 3.2)

- **Co-op with mods: both players' games now agree on the battle** ([issue #24](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/24)). In co-op, each player's game checks at the start of every round that both games hold the same units, and a joining player whose game disagreed was dropped with "REJOIN FAILED". With BattleTech Advanced the games disagreed because several mods roll their own random numbers and unit and equipment IDs separately on each PC: which faction the enemy is and which enemy units drop and where, the IDs of support teams, weapons and ammunition, and what a repair mod has already damaged. UCU's mod loader now makes those values the same on both players' PCs, for MissionControl, StrategicOperations, FieldRepairs, SoldiersPiratesAssassinsMercs, CustomAmmoCategories, CustomComponents and any mod that hooks unit spawning, each only when that mod is loaded. With SoldiersPiratesAssassinsMercs, whose own random draw decides whether and to which faction the enemy is swapped, both players now get the same replacement faction.
- **Co-op with mods: a stat that exists on one player's game only no longer breaks the check.** Two more causes of the same disagreement are fixed in UCU's own game code. CustomUnits records how far a unit moved only on the PC that worked out the path, so the other player's game lacked that stat; the movement order now carries the path's cost and the other PC records it too. And when the host's AI weighs its attacks, MechEngineer creates slot-hit stats on read, on the host only; the co-op host now removes the unused default-valued stats it created while thinking, and keeps and logs any stat the AI really changed.
- **Co-op with CustomComponents: a 'Mech's components keep their order.** CustomComponents goes over every 'Mech definition each time a career is created, and its second pass reorders the components, so one player's 'Mech could list them in a different order from the other's. UCU's mod loader now skips a definition CustomComponents has already processed in the same session.
- **Objects and rocks no longer vanish for a frame as they change detail level** ([issue #18](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/18)). When an object swaps to a simpler or more detailed model, the game fades one out and the other in with a dither pattern, so that between them every pixel is covered. Unity 6 hands the shaders the fade for the incoming model as a negative number, which the original game's dither code didn't expect, so at the end of each fade neither model covered anything and the object disappeared for a frame, and could show holes during the fade. The vegetation, standard and urban shaders now handle the negative value. Thanks to wyaeld, who found the cause and wrote the fix.
- **Less memory kept between contracts** ([issue #22](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/22)). UCU kept two things from every map you had played until you quit: the map's terrain textures (two large splat maps per map, plus the terrain arrays), and copies of the materials that UCU makes for the original game's shaders. Both are now released when the map unloads, as in the original game.

## 2.2.1-3B (BT-UCU Beta 3.1)

- **Linking BattleTech Advanced works with a missing Steam library drive** ([issue #25](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/25)). If one of your Steam libraries was on a drive that no longer exists (an unplugged or removed disk), the Link-BTA script stopped with an error even when you gave it the BattleTech Advanced folder yourself. It now skips the missing library and carries on.
- **Linking BattleTech Advanced no longer refuses UCU's own Mods folder** ([issue #25](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/25)). With the in-mission saves checkbox on, the launcher creates a Mods folder holding only UCU's own `.port` and `.modtek` folders, and Link-BTA then refused to run because a Mods folder already existed. A Mods folder that holds only those is now treated as empty.
- **The launcher no longer calls every BattleTech Advanced install RogueTech.** BattleTech Advanced ships a mod called RogueTechPerfFix, and the launcher checked for RogueTech first, so its Mods tab said "It looks like RogueTech, though." for every BattleTech Advanced install. It now checks for BattleTech Advanced first.
- **The launcher can update the install it sits in** ([issue #27](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/27)). The launcher copy inside your UCU folder refused to update that folder with "Install somewhere outside this package folder.", so every update from it failed. It now refuses a folder only when that folder holds the installer package itself.
- **The launcher no longer freezes when you pick an existing install in "Install to".** Choosing a folder that already held a UCU install made the launcher's window stop responding ("Not responding") for about 30 seconds while it compared the package with that install. It now reads the folder in the background, shows "Reading that folder..." with a moving progress bar, and keeps Install disabled until it finishes.

## 2.2.0-3B (BT-UCU Beta 3)

- **Experimental: in-mission saves with mod packs.** Big mod packs turn off saving in the middle of a mission because those saves often can't be loaded again. The launcher's *Mods* tab now has a checkbox, "EXPERIMENTAL: Enable in-mission saves", off by default. With it on, UCU makes on-the-fly changes to several mods that facilitate their behavior with respect to serializing game state in save files (IRTweaks, CustomUnits, MissionControl, ExpandedInitiative, CustomAmmoCategories, StartDialogue and MechEngineer), each applied only when its mod is loaded. The checkbox warns to keep saving before and after missions, because the feature is new, and that these saves can only be loaded by UCU with the option on. With it off, the game saves as the original game does.
- **Big drops no longer stall the battle** ([issue #14](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/14)). With many units dropping at once (e.g. a modpack allowing 14 player units), the battle stalled for over a minute before the first turn, with the game running at a few frames per second. On the reporter's save in the editor, the first player turn came 26 seconds after the mission started instead of 85, and the game stayed smooth.
- **Co-op with mods: the joining player's mission starts** ([issue #20](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/20)). With BattleTech Advanced's MissionControl mod, a joining player's game never loaded the mission and the host stayed on "waiting on other player". UCU started the joining player's contract through a different call from the one MissionControl hooks, so the mod's setup never ran there. The joining player now starts it the way the original game does.
- **Mods that change buying a 'Mech load again.** Some BattleTech Extended and BattleTech Advanced mods (Quirks, Quirks Plus, Roles, IRTweaks, EngineSize) failed to load because UCU's co-op purchase check sat inside the game's code for adding a 'Mech, where those mods insert their own changes by position. The check now runs from the code that calls it, so that code matches the original game.
- **Co-op chat can be minimized.** In career and campaign co-op, a "-" tab on the chat panel's top edge folds the chat down to a "CHAT +" bar, and a click on the bar brings it back. Messages that arrive while it's folded are there when you reopen it, and opening the chat to type (T or the chat button) reopens it too. The choice lasts for the session and covers the Argo, mission setup and the battle map. On the battle map, the minimize tab sits above the mute and chat buttons.
- **Textures use the original game's image data.** Textures now use the 2018 game's own image data (from your existing install) instead of copies that Unity 6 had re-encoded. This reduces the build size by about 1/3 and should solve some texture-related issues.
- **BattleTech Extended: Tactics combat loads.** Starting a battle with BattleTech Extended stuck on "Preparing for Combat", because one of its mods (BTDebug) looks up two of the game's drawing shaders by name and UCU's newer Unity couldn't find them. UCU's mod loader now supplies them only when that mod is present. 
- **RogueTech: CustomUnitSpawn loads.** The CustomUnitSpawn mod included with RogueTech failed to start on UCU, because it asks Unity's random-number calls for their arguments by the names `min` and `max`, which Unity 6 renamed. UCU's mod loader now answers to the old names, and only when that mod is present. RogueTech is not yet compatible due to a hard requirement to use the RT launcher which itself has a hard requirement to use the retail .exe; the RT team is reviewing options.
- **Changing gamma or quality no longer minimizes the game.** Confirming a gamma or quality change in the Video settings made the game window minimize. When you confirmed, the game re-applied the display mode even though nothing about the display had changed, and on UCU's newer Unity that restarts the window-mode switch and minimizes the window. The game now re-applies the display mode only when the resolution changes or, in fullscreen, the monitor does, and the windowed toggle still works as before.
- **Cosmetic retail bug in contract results page fixed.** On some contracts, the expanded 'objectives' rollout would say 'Contract payment increased by ERROR: resolving 'ToPercentSTring' on 0.1 Exception Type 'System.REflection.ReflectionTypeLoadException' was thrown. Could not load file or assembly 'MonoMod.Core,Version=1.2.2.0, Culture=neutral, PublicKeyToken=null' or one of its dependencies.' This was harmless and did not affect the outcome of the contract or otherwise interrupt the game, but it is fixed.
- **DLC 'Mechs select faster and use less memory.** Selecting a 'Mech from Heavy Metal, Flashpoint or Urban Warfare for the first time in the Mech Bay froze the game for about four seconds in our test, and now takes under a tenth of a second. The DLC files carried their own copies of the game's shaders, built for the old Unity. UCU now points them at the shaders built for Unity 6, in memory when the game starts, and your own DLC files are never changed. Memory use with the DLCs is also about 480 MB lower.


## 2.1.5-2B (BT-UCU Beta 2.5)

- **GOG support.** UCU now runs on both the GOG and Steam editions of BATTLETECH. On a GOG install,
  the installer finds your GOG install, then copies the GOG files UCU needs from your
  GOG folder, including the markers for the DLC you own there. In the game, Flashpoint, Urban Warfare and Heavy Metal
  follow your GOG ownership, saves go to GOG Galaxy (synced to the cloud when Galaxy is signed in) and achievements go
  to your GOG account; Steam isn't needed. GOG's own BATTLETECH lists the saves UCU makes there. If you buy a DLC on
  GOG after installing UCU, run the installer again to repair your UCU folder and it picks the DLC up.
- **Co-op join stuck on "Receiving the campaign..." with big careers** ([issue #9](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/9)). A joining player asked the host for the
  campaign again every five seconds and threw away what had already arrived, so a career too big to arrive in five
  seconds (a BattleTech Advanced career is about 12 MB) never finished loading, and the host stalled each time it
  rebuilt it. The joining player now keeps the download while it is still arriving. If the campaign arrives but
  can't be loaded, the joining player now sees a message and returns to the main menu instead of waiting forever.
- **The installer is now a launcher.** The same window has five tabs: *Home* shows your installed version against the
  newest release and starts the game; *Install / Update* is the installer; *Mods* turns mod support on or off and links
  or copies a mod pack you installed for retail BATTLETECH (BattleTech Advanced, RogueTech, or another ModTek pack), so
  `Link BTA.cmd` is no longer needed; *Settings* picks DirectX 11 or 12 (12 has no benefit yet) and extra launch
  options; *Tools* verifies the install, opens the log and save-backup folders, collects a bug report (one zip of
  the game's and your mods' logs, ready to attach to a GitHub issue), and makes a desktop or Start menu shortcut.
  After installing, the launcher keeps a copy of itself in the install folder as `BattleTech UCU Launcher.exe`, so
  you no longer need the download to update. RogueTech has not been tested on UCU yet.
- **Mod packs on Linux and Steam Deck** ([issue #13](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/13)). Under Proton, BattleTech Advanced 20.3 and other packs on ModTek 4.4.x no
  longer get stuck repeating an error while they load ("ModTek died"). The MonoMod library that ships with ModTek 4.4.x could write
  broken jumps in the game's code when the memory next to that code was full, which happens with UCU's newer Unity
  under Proton. UCU's mod loader now applies MonoMod's own fix for this (MonoMod pull request 308, credit to the
  MonoMod team). With ModTek 4.5.1 or later, which already includes the fix, or without ModTek, it does nothing.
  Tested under Proton: BattleTech Advanced 20.3 loads and plays a skirmish. On Windows it plays as before. Not yet
  tested under Proton: a BattleTech Advanced career or loading saves.
- **Mods that look for the game's popup layer** ([issue #16](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/16)). UCU gave the layer that holds pop-ups a
  different name from the original game, so a mod that finds it by name got nothing. In LootMagnet's shift-click sale
  on the salvage screen, that left the item in place after paying its value. The layer now has the original game's
  name, PopupRoot. The LootMagnet sale itself hasn't been re-tested with the fix yet. In BattleTech Advanced, shift-click
  selling is off by default and you sell by clicking the item's icon, which already worked on UCU.
- **Mech Bay tile outlines** ([issue #15](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/15)). In the Mech Bay, the outline around each unit's
  tile and the frame around the selected tile didn't draw, because the two frame shapes pointed at the wrong data and
  came out empty. Both are drawn again.

## 2.1.4-2B (BT-UCU Beta 2.4)

- **Skirmish launch.** Pressing Play on the skirmish setup screen right after the lances fill in could freeze the load:
  the game kept repeating the same error and the mission never started. The lance crests no longer try to draw on a
  screen that has already closed. This is a retail bug.
- **Co-op game data check.** The check that both players have the same game data no longer depends on the order in
  which the files are listed on disk (with mods, that order came from the mod loader) or on line endings: a mod
  file with Windows line endings now counts the same as one with Unix line endings. Two BattleTech Advanced installs
  were refused because one PC's copies of the CAB mech packs had Windows line endings and the other's Unix ones,
  with the same content. This is a retail bug. The game also writes
  `mp-defhash.tsv` next to `Player.log` (in `AppData\LocalLow\BTUCU\BattleTechUCU`) when it starts, hosts or
  joins: one line per file in the check. If a join is refused with "Game data mismatch (def hash)", the two players
  can compare their copies to see which files differ. With BattleTech Advanced, first make sure both players have the
  same BTA version: the join compares mod names, not versions.
- **Blank rows on the salvage screen** ([issue #8](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/8),
  also seen in [issue #7](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/7)). After a few contracts in one session, some salvage rows took up space but
  showed nothing: the item could still be picked or was handed out at random, but you could not see it. The same
  blank rows could turn up in other item lists that reuse them, such as the Mech Lab inventory, until a restart.
  When the salvage screen closed, the game threw the old screen away in the same moment it moved the rows out to be
  reused, and the rows came back switched off. The game now throws those screens away at the end of the frame, after
  the rows are out.
- **Loading a mid-battle save with mods.** The game's file loader could get stuck for good on one file while it
  loaded a saved battle: the mission's load screen came up, but BEGIN did nothing. It happened when a faction's logo
  was still loading for one part of the game while the faction itself loaded for another. This showed on every
  mid-battle save loaded with BattleTech Advanced. This is a retail bug. BattleTech Advanced keeps mid-battle saves
  off by default, and other faults remain before those saves play on.
- **Smaller installs on the same drive as BATTLETECH.** About 11 GiB of UCU's files are identical to files in your
  own BATTLETECH install. When UCU is installed on the same NTFS drive, the installer now links the large ones (asset
  bundles, map data, videos and sound banks, 10.8 GiB) to BATTLETECH's instead of copying them, so they take no extra
  space; it checks each one first, as before. Code and every text or data file you might edit, such as the JSON files,
  are still copied, so editing them in UCU's folder never changes BATTLETECH. On another drive the installer copies
  everything and says so. An existing install on the same drive switches the next time you run the installer.
  Uninstalling or moving BATTLETECH doesn't break UCU: a link is the file itself.
- **Link BTA with more than one UCU install.** Run from an install's `ModSupport` folder, `Link BTA.cmd` and
  `link-bta.sh` now link that install, instead of listing every UCU install in your Steam libraries and asking.
- **Ghosting and smeared ground with anti-aliasing and motion blur.** On High settings, trees and foliage left
  ghost trails and the ground flickered and smeared, even with the camera still. Unity 6 drew the game's trees into
  the motion pass that the anti-aliasing and motion blur read, with false movement of hundreds of pixels; retail
  draws none there. Trees and foliage now give only the camera's own movement, as on retail.
- **Data folder renamed.** The game's saves, profiles, settings and `Player.log` now live in
  `AppData\LocalLow\BTUCU\BattleTechUCU`, not `AppData\LocalLow\DefaultCompany\BattleTechU6`. On the first start the
  game moves your saves, profiles and settings from the old folder, and on Windows (and under Proton) also copies
  the game's settings from the registry, such as whether mods are on; then it deletes the old folder and registry
  key. During that first start the window opens at the default size, then changes once to your old window size and
  mode; later starts open that way. If your old size is missing or no longer fits the screen, the game keeps the
  default window. Which monitor the window was on is not restored. If something stops the move, the old folder is
  kept and the next start tries again, so nothing is lost.
- **Unit picks with large mod packs.** With big mod packs such as BattleTech Advanced, a lance or contract that picks
  its units by tags could match more than 999 groups of tags. The game's SQLite 3.16.2 refuses a query that size
  ("too many SQL variables"), so that unit pick failed with an error instead of choosing a unit. UCU now ships SQLite
  3.54.1, whose limit is 32,766: in our tests with BattleTech Advanced loaded it answers all 132 such queries that
  3.16.2 refused, as the SQLite 3.38.2 that BattleTech Advanced installs for the original game does.
  Without mods the game never comes near the limit.
- **Known issue: memory in long sessions.** Memory use grows during a long session, about 1.5 GB above the original
  game after three missions and then about 200 MB per mission (around 9 GB after twelve). The original game also grows
  as you play. Restarting the game returns the memory. We are still working on it.

## 2.1.3-2B

- **Memory after loading a save.** Every career you loaded in one session (Continue, Load, or a new career after
  going back to the main menu) left the previous career in memory, star map and all, because two of the game's own
  listeners never let go of it. Loading a save now releases the career you left. This is a retail bug.
- **Link BTA without the copy.** `Link BTA.cmd` now links BTA's mod folders into UCU instead of copying about 20 GB,
  and BTA updates reach UCU without a re-run. Both games then share those folders: UCU writes mod logs and settings
  into the BTA install, and a mod turned off in one is off in the other. `Link BTA (copy).cmd` copies as before, and
  re-running either switches an install. Contributed by gkirkpatrick.
- **Link BTA on Linux and Steam Deck.** `link-bta.sh` in `ModSupport` does the same under Proton: it links BTA's mod
  folders with symbolic links (or copies them with `--copy`) and tells you the Steam launch option the mod loader
  needs. On our Linux test PC the mods load through the links, but BTA on UCU under Proton does not yet reach the
  main menu; we are looking into it.
- **BattleTech Advanced: fast preloading** ([issue #4](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/4)). With
  CustomPrewarm's caching turned on, the first start failed to write the cache and turned caching off again. The mod
  needs a small piece of .NET that the new engine does not ship. The mod loader now supplies it, so the cache is
  written once and later starts read it. In our test with BTA (Core 20.3), a save loaded in about
  25 seconds with the cache, against 62.5 seconds without. Reported, with the fix, by gkirkpatrick.
- **BattleTech Advanced: fast preloading with BTA's backgrounds** ([issue #5](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/5)).
  CustomPrewarm's cache build also stopped on BTA's career backgrounds, on retail as well, and turned caching off. The
  mod loader now works around that mod bug, so BTA can use the cache. Caching stays off unless you or BTA turn it on;
  if it already turned itself off, set `UseFastPreloading` back to `true`. Reported, with the fix, by gkirkpatrick.

## 2.1.1-2B

- **Menu and billboard videos on Linux and Steam Deck.** The main menu's background video and the nine Urban Warfare
  billboard screens now use the VP8/WebM video format instead of H.264, which Proton plays as colour bars. Under
  Proton the main-menu video now plays. The opening cinematic uses a different format and already played under
  Proton. Windows players get the same clips; they are encoded at the originals' size and quality, so the install
  does not grow.
- **Russian text.** With the game set to Russian, menus showed a box for every Cyrillic letter, because the Russian
  fonts the game switches to were missing. Retail's five Russian fonts are back, so Russian menus and popups read
  properly again.
- **BattleTech Advanced: ship menus after loading a save** ([issue #3](https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/3)). With BTA linked, some saves arrived on the ship with the
  menu buttons doing nothing. A mod BTA uses loads pilot portraits in a way the 2018 engine allowed and the new one
  refuses, so those portraits never loaded and the ship screens stopped setting up. The mod loader now lets those
  portraits load, and the menus work. Installing this version updates the loader; BTA does not need linking again.

## 2.1.0-2B (BT-UCU Beta 2)

Changes in version **2.1.0-2B** of the BattleTech: Unofficial Community Update (UCU)

- **Mod support is built in more explicitly.** The installer has a *Mod support* box, ticked by default, that puts the ModTek loader
  beside `BattleTech.exe`. Put ModTek and your mods in a `Mods` folder there and they load; with no `Mods` folder the
  game starts as before. The loader's files also come in the install's `ModSupport` folder, with Doorstop's license.
- **BattleTech Advanced.** BTA's own installer needs retail BattleTech, so `Link BTA.cmd`, in the install's `ModSupport` folder, copies the mods and saves from
  a BTA install you already have. This is a helper -- it's not required and you can manually move everything over.
- **Memory in long sessions.** We've taken a first pass at the famous 'memory leak'. One cause was that, when a battle ended, the game kept that whole battle in memory,
  including its map's terrain and encounter grids, because a few shared objects (lance and spawn-point references,
  and the battle's dialogue and conversations) still pointed back at it. Each different contract you played added
  about 80 MB that was never released, so a long session kept growing. The battle is now released when it ends. In
  our tests, memory kept per contract fell from about 100 MB to about 27 MB, and seeded test battles play out exactly
  as before. A separate, gradual increase in memory usage shows up on both the 2018 game and UCU; we are
  still looking into it.
- **Faster star map filters.** On the Navigation screen, picking a biome, store or difficulty filter highlights the
  matching star systems. To find each system's star on the map, the retail game searched the whole map from the start,
  once for every system, and repeated that 5 to 13 times per click. The work grew with the square of the map's size:
  barely noticeable on the stock map's 219 systems, but on a modded map the size of BattleTech Advanced's (about 3,300
  systems) each pass took about 175 ms, so every filter click froze the screen for one to two seconds. The game now
  keeps an index from each system to its star, so a lookup is immediate. On the stock map a filter change went from
  8-41 ms to 3-27 ms; the rest is drawing the highlights, which is unchanged.
- **Saving could stop for a whole session.** Now and then, a save never finished: the save icon stayed up and every
  later save, autosaves included, was quietly skipped until you quit the game. Earlier saves were safe. The game
  writes saves to Steam Cloud in the background, and Steam's reply could arrive before the game was ready for it and be
  lost; the game then waited for it forever. The game is now ready for each reply before it asks Steam. UCU 2.0.0-1B
  has the same problem.
- **A new career keeps its company name.** In the 2018 game, a new Career's first save could be written before the
  company got its name and crest, so continuing from that save brought back the default "Mason's Marauders" and the
  default crest. The first save now holds your "{callsign}'s Marauders" with your career's crest and colours. A career
  that has already reloaded as "Mason's Marauders" keeps that name, because the fix only changes new career starts; you
  can rename the company and change its crest in the Captain's Quarters, as in the 2018 game (in co-op, only the host
  can).
- **Save backup.** BATTLETECH and UCU share one Steam cloud folder for saves and settings. After it installs or
  updates, the installer copies that folder once for each Steam account to `Documents\BattleTech UCU save backup`, with
  a README on putting it back. Steam's folder is only read.
- **Co-op join refused over a version difference.** The message now names both releases: "Game version mismatch:
  host <version>, client <version>. Both players need the same version." Two builds of the same version no
  longer join each other. The host sees a PLAYER COULDN'T JOIN notice in the Multiplayer panel, or a line in the
  Argo's activity feed while the panel is closed.
- **Crew in co-op story scenes.** When a guest replays one of the host's crew conversations, the Argo's rooms show
  the crew the host had at that moment, not crew from a later point in the story.
- **Direct Address takes IP addresses.** In Join Career, the Direct Address box dropped "." and ":", so a typed IP
  address or port never reached the host; only a host name worked. It now takes an address, with or without :port.
  (Also in 2.0.0-1B.)
- **Leaving an Ironman host.** A guest in an Ironman host's career can leave or quit from Game Options; before, both
  buttons stayed greyed outside combat.
- **Co-op desyncs are logged.** When the players' battles drift apart, the host's `Player.log` names the first unit
  whose state differs and the round it happened in. Include it when you report a co-op battle going wrong. The check
  never changes play.

## 2.0.0-1B

Changes in version **2.0.0-1B** of the BattleTech: Unofficial Community Update (UCU), its first Beta build, from the final Alpha, 2.0.0-10A (see below for the complete list of changes between retail and the Alpha line).

- **Multiplayer text** goes through the game's localization, so it can be translated. English reads the same as before.
- **Fallback shaders are logged.** A short while after each scene loads, `Player.log` names any object drawing with a missing
  or unsupported shader. Include it when you report something drawn wrong.
- **Chat and Settings.** The co-op chat sits below an open Settings menu or the host's Multiplayer panel, so its title no
  longer covers the KEYBINDINGS tab on wide screens or a guest's permission boxes.
- **Game version for mods.** The game reports retail's version, 1.9.1, again, so a mod that names 1.9 or 1.9.1 as its
  game version loads as it does on retail, and retail no longer reads new saves as from a newer build. UCU's own
  version, 2.0.0, is in `VersionInfo.PortVersion`; the main menu and multiplayer use it. Saves from earlier port builds
  still load in UCU.
- **Crew conversations in co-op.** Guests now see the host's crew conversations and story cinematics, replayed on their
  own screen at their own pace. A guest with the Decisions permission can pick replies (as can the host). A guest busy elsewhere sees the
  scene from the start once free, then returns to where it was. Each player skips a cinematic alone; crew chats show
  the line only.
- **New crew in co-op.** When the host's story brings in a new crew member, guests now see them in the Argo's rooms
  too, and crew conversations no longer point the camera at someone who isn't there.
- **Depth-of-field log warning.** Conversations no longer fill the log with an `R8_SRGB` warning every frame. The
  blur looks the same.
- **Paradox analytics** reports name the game as UCU and its version,
  for example `UCU-2.0.0-1B`, so they can't be mistaken for retail's.
  The game sends only the reports Paradox's server accepts, the session start and the hardware summary; Paradox was rejecting everything else in the retail build, so we just don't send it.
- **Title screen.** Added (Unofficial Community Update) to the version display.

## 2.0.0-10A

What versions **2.0.0-1A** through **2.0.0-10A** of BattleTech UCU added and changed from retail *BATTLETECH* 1.9.1 (build 686R).

## New

### Campaign multiplayer

All of Campaign or Career mode can be shared with up to 3 other players. The host owns the game (Argo) state and clients receive save
snapshots, while combat keeps HBS's deterministic command model.

- **Sessions.** Player cap, password challenge, compatibility check, host approval, kick and ban. Permissions, bans and Always allow are kept per campaign, outside the save. Direct connect via IP can be probed and joined. A kicked or banned client stops reconnecting.
- **Joining.** A client  joins a running campaign and receives a snapshot, then stays in step through new snapshots at
  each time stop, with a day and funds tick between them.
- **DLC.** The host's DLC covers every guest. A guest who owns fewer DLCs, or none, can join and play the host's DLC
  content for the session, and is back to their own DLCs on leaving.
- **Argo.** Every client sees the host's Argo. A client can act only on the screens the host permits.
- **Screens.** MULTIPLAYER in the main menu's Career and Campaign flyouts opens Join Career or Join Campaign, a mode of the Skirmish browser
with skirmish-only settings pruned. MULTIPLAYER in the Argo's options opens a panel where the host opens the campaign,
  answers requests, sets seven permissions per player, and kicks or bans; a client sees it read-only. A player with no profile picks a
  callsign first. The Argo shows a Host Multiplayer or Guest Multiplayer tag under the career score (under the company
  name outside a career); the host's tooltip lists connected guests and a guest's lists their grants.
- **Co-op combat.** Per-unit control, host-ordered commands, AI on the host only, and a client that owns no unit can watch.
  Another player's portraits are dimmed and carry their name.
- **Contracts.** A host takes joined clients into a real contract. A client with no slot observes, a client that drops leaves
  its units with the host, and the host alone applies rewards before one snapshot returns everyone to the Argo. Joins are refused
  while a contract runs.
- **Mission setup and chat.** The host assigns each lance slot to a player on the retail lance screen. Each player readies,
  and Deploy stays off, with the reason shown, until all have. A changed 'Mech, pilot or owner takes back that player's ready. A
  client sees the contract, lance and owners before launch, then the host's results in an after-action view. Every player can chat
  on the Argo, in mission setup and in combat. Team-wide Reserve and Done-with-all are hidden in co-op. The chat can be moved and
  resized, and no longer blocks clicks on the screen behind it.
- **Rejoin.** A client that drops mid-mission can come back and finish the contract. Only players on the roster from contract
  accept are let back in, and their slots and units are restored.
- **Resync.** A peer whose combat state drifts from the host's is resynced from the host, up to three times per contract,
  before falling back to retail's desync. A host waiting on a rejoiner always gives up in the end and takes back the slots.
- **Save Snapshots.** Argo snapshots wait until a contract ends, and are compressed (about 42% smaller).
- **Stray shots.** Stray shots stay on in co-op, and the host now resolves every missed shot, so peers can't disagree about a
  secondary hit. Single player and Skirmish keep retail's path.
- **Mid-battle saves.** The host can save during a co-op mission and reload it later. The session reopens, and players from the
  roster rejoin into their slots; an absent player's slots go to the host after 180 seconds. With no co-op record the save loads as
  single player. The save format is unchanged.
- **Store for guests.** A client with the Store grant buys and sells. The host re-checks stock, price, funds and travel state,
  and refuses with a plain reason; of two buyers racing for the last item, one gets it and the other sees "Sold out".
- **Mech Bay for guests.** A client with the Mech Bay grant repairs, scraps, readies and stores 'Mechs, swaps bays and cancels
  work orders. The host builds every work order itself and refuses plainly. Clients can buy whole 'Mechs and pick where they go.
- **Mech Lab in co-op.** Opening a 'Mech in the Mech Lab locks it to that player. Anyone else, and anyone without the Mech Bay
  grant, sees the lab read-only with the holder's name. Parts taken from storage are claimed at once, so other labs see the lower
  count and claimed parts can't be sold. A client with the Mech Bay grant refits in their own lab; the host validates each
  refit and applies it the same way as in single player. Time and contract launch wait until every lab is closed.
- **MechWarriors for guests.** A client with the MechWarriors grant hires, dismisses, levels up and customizes pilots. The
  host checks each request against what the client saw (the applicant still listed at that price, the pilot's experience and
  skills) and refuses with a plain reason, such as "Someone else hired that MechWarrior." The commander can't be dismissed.
- **Engineering for guests.** A client with the Engineering grant buys an Argo upgrade and cancels the one in progress. The
  host checks the upgrade, its required modules, the cost shown and the funds, and the upgrade completes on the host as days pass.
- **Travel and time for guests.** A client with the Starmap grant sets a course, stops navigation, starts and pauses the
  host's clock, and accepts or aborts a flashpoint. The host plans the route itself and checks the cost and days shown.
- **Contracts and events for guests.** A client with the Contracts grant accepts a contract and launches an arrived travel
  contract; the host checks the terms and opens mission setup. Event popups show on every screen, and a client with Full Control
  answers them.
- **Permissions change live.** When the host changes a guest's permissions, the guest's screens follow at once. Anything
  the guest was confirming without the permission any more is cancelled, with one notice. A button the guest isn't permitted is
  grey from the first frame. Alliances stay the host's, and guests see what the host took as salvage.
- **Translatable text.** The multiplayer screens' text goes through the game's translation tables, as retail's own
  text does, and a guest shows the host's messages in the guest's language. Only the English text exists so far.

### Mod support

DLL mods run on stock ModTek through a small port bootstrap, on D3D11 and D3D12. We keep every existing member's name and signature so Harmony patches still land.

- Doorstop is v4.6.0. The copy ModTek ships crashes when Unity loads `d3d12.dll` by ordinal.
- The bootstrap applies three Mono 6.13 compatibility fixes and runs ModTek's assembly shim on `Assembly.LoadFile`.
- For mods built against Unity 2018, it writes a forwarded `CoreModule` copy under `Mods/.port` so `UnityEngine.Input` resolves.
- For Newtonsoft 13, it writes a copy with the three internals ModTek's merges call. Shipped DLLs stay stock.
- The game boots through retail's Main chain, which ModTek injects into. The embedded uGUI keeps retail's
  `ForceMeshUpdate(bool)`, `DeactivateInputField()` and 4-parameter `SetLayoutInputForAxis` as overloads.

### Diagnostics

- **Shader check.** Thirty seconds and two minutes after each scene loads, Player.log gets a `[ShaderFallbackCensus]`
  summary line, and one line for each object that draws with a missing or unsupported shader.

## Optimization

Each of the below optimizations passed a seeded equality check against the unpatched game. 
| Change | Measured |
|---|---|
| AI debug logs built only on request (new `aiLogCacheEnabled` setting, off by default) | Lower AI turn cost |
| `DataLoader.GetEntry` uses a dictionary | `LoadResource` 1,019/1,137 to 860/779 ms |
| `FromJSON` skips per-member reflection waste | 3,842 defs: 2,392/2,264 to 905/864 ms |
| Shader bundles stay pinned across `AutoUnloadAll` | Career load 29.1 to 17.8 s from the menu, 36 to 17.2 s in game |
| `RefreshTypedEntries` indexes addenda once | About 0.5 s per refresh |
| No scene-wide searches while no map is loaded; terrain tiles share one clip root | About 2.1 s per combat load |
| Each saved type name is looked up once | Save decode 16.4 to 8.1 s |
| Index for the tag lookup in the in-memory database | Tag SQL in Deploy 7.6 to 0.27 s |
| No camera scan while no camera exists | Camera scan in Deploy 7.8 to 0.02 s |
| Loads drop vsync, the frame rate target and low background priority, then restore them | Modded BTA career: load to the Argo 50.1 to 36.0 s, in-game reload 38.4 to 25.8 s, measured with the display unattended |
| Shader requests open at the first scene and load async; `VFX/Alpha` read once | Boot 9 s faster, combat load 3.6 s faster |
| Star map renderer lookup uses an index (2.1.0-2B; the same renderer for every system, checked on the live map) | One filter pass at 3,300 systems 175 to 0.1 ms; a biome or store change makes 5 to 13 passes |

Together, the save decode, tag lookup and camera scan changes take a modded save from 79.8 to 62.6 s and an in-game reload
from 55.4 to 45.8 s.

## Retail bugs fixed

Multiplayer:

- Direct and Online Direct share a socket, and strategies that weren't selected swallowed messages.
- A networked mortar strike lost its target positions on the wire.
- Support weapons had a null uid, so a receiver could never match the weapon.
- A forwarded artillery activation ran again, firing a second strike on receiving peers.
- A deferred `ReadString` read a recycled message and corrupted the host's settings update.
- The host's one-shot Ping could arrive before the server port was bound, and a retry reported failure too early.
- One lost datagram ended a join. The handshake now retries.
- The next team could activate while a peer's order was still running, which deadlocked whole Skirmish matches.
- A client sending an empty password hash skipped the host's password check.
- The handshake timeout fired off the main thread, leaving a client on the Argo with no message.

Elsewhere:

- Concurrent Vexe reflection builders crashed natively. They now lock the shared emitter.
- A Steam read refused during a concurrent write reported success, handing the deserializer an empty buffer. Retail tears
  246 of 256 overlapped loads; UCU delivers all 256.
- Every quit logged a CRITICAL ERROR from analytics.
- Each tree teardown leaked a culling group.
- `Shop.Purchase` lowered stock before checking funds, so a buy the company couldn't pay for still used up the item.
- A settings save made while another was still writing was refused, so the newer settings were lost. The newest one now waits
  its turn.
- Quitting logged "has no receiver" for every object without a quit handler.
- Closing a scene could create a new UI element manager while tearing down, leaving objects behind.
- Retail ships components and assets whose scripts were stripped, each logging a missing script. They're removed.
- Retail map data has two tree types with no prefab on a story map and antennas with no mesh on Boulder Field and The Mound,
  each logging an error on load. The trees are dropped and the antennas skipped quietly; nothing that drew changes.
- On a story map, the encounter's self-check counted buildings inside its own area twice and logged an error.
- Rebuilding a map's water edge left the old obstruction logic alive for a frame, so it was registered twice.
- Urban Warfare's opening conversation on New Jakobstad handed its music cue to the holo screen instead of the audio player, so
  the music never started and an error was logged. The music now plays, and the holo screen shows the same image as before.
- On Itrom's Restoration map, one artillery area was a copy of the other with the same six target point IDs, so each load
  logged twelve duplicate-ID errors. The copy's points have their own IDs now; both areas keep their six points.
- Quitting, or leaving combat, while a sound effect was still playing, such as a building collapse, logged a null-reference
  error from the audio shutdown. The sound's end callback no longer reaches for the game or the sound after they're gone.
- Quitting from combat could log a null-reference error from the fog of war, when its view was torn down before the
  objects that reveal it. Those objects now skip the view once it's gone.
- A joystick, throttle or pedal resting off-centre walked the menu selection, so an open dropdown showed a second highlight
  climbing from the hovered row to the top. Joystick axes no longer drive UI navigation.
- The main menu kept its intro-end handler after it closed, so an intro that ended later could run it on the closed
  menu's video player.
- **Encounter boundary.** Retail's boundary mask is all-opaque, so its distance field never spread. The fog now draws a gold
  dashed band.

## Changed from retail

- **Services.** Sift, AWS and ServiceStack callers are off, and so were analytics until 2.0.0-1B turned them back on. Paradox account sign-in is retail's. `S3Manager` is an offline shim with the same
  API, and the four AWSSDK DLLs are gone. Changing the callsign saves it locally instead of waiting on ComStar.
- **Version.** Up to 2.0.0-10A (2.0.0-1B reports retail's 1.9.1 again; see above), retail 1.9.1 saves read as older, so an Ironman save is backed up on load. New saves carry 2.0.0-10A. Multiplayer no
  longer matches retail clients. The mod-loader cache re-keys once. The unused `VersionInfo.CURRENT_VERSION_NUMBER` still says 1.9.1.
- **Graphics API.** D3D11 is the default, as in retail, and D3D12 is supported, modded and unmodded. Graphics jobs are off.
- **Third-party code.** Newtonsoft 13, current DOTween, the Steamworks.NET package, SVG Importer 2.0 and SQLite 3.54.1 (retail
  ships 3.16.2; game-data queries return the same results in the same order). InControl, Lidgren,
  Harmony 1.2, NGettext, Ionic.Zip, Dapper and protobuf-net stay as retail binaries.
- **Audio and video.** Retail's Wwise 2016.2.2 and Bink 1.300n runtimes with retail banks and movies, with no transcoding.
- **Developer mode.** The main menu's SIM GAME and DEBUG buttons depend on `DebugBridge.testToolsEnabled`: on in the Editor and
  development builds, off in release.
- **Editor only.** Saves go to `EditorCloud` and achievements to a log, so the Editor can't touch a developer's Steam data.

## Required technical changes

These keep retail's behaviour where retail's code or data doesn't work as shipped on Unity 6.

### Code and compiler

- Replaces 2018 Unity API calls, with an exact `GetInstanceID` helper. `BTLight.CompareTo` keeps the real call because a
  ModTek injector patches it.
- Updates HBS's TextMeshPro and uGUI wrappers for current packages.
- Drops a duplicate `AssemblyVersion`.
- Removes a `GetControlID` call that threw outside `OnGUI` and stopped the debug console loading its history.
- **Assemblies.** HBS code compiles into `Assembly-CSharp` and `Assembly-CSharp-firstpass`, the names retail's bundles reference.
  The firstpass tree is under `Plugins/HBS/`, `allowUnsafeCode` is on for Bink, and the language version is C# 12.

### Saves and serialization

- Maps type names so retail saves load.
- Skips a serializer candidate that throws `TypeLoadException`, because `TouchPhase` moved with no forwarder.
- Restores retail's unlimited JSON `MaxDepth`, which Newtonsoft 13 caps at 64.

### Textures and data

- Rounds DXT textures up to a multiple of 4, which Unity 6 requires, and a failed request no longer blocks the queue behind
  it (this had hung Campaign START).
- Applies the same limit to sprites loaded from disk: decoded as RGBA32, recompressed to DXT5 only when aligned.
- **Texture flags.** 4,827 textures use retail's sampling flags. 1,783 ripped textures had the alpha-is-transparency
  flag on, which painted window colour over the facade strips of urban office blocks; it is off, as built textures have none.
  2,664 textures were built uncompressed, which more than doubled the texture data; they now use the compressed format retail ships.
  619 more were built as BC7 where retail ships DXT1, DXT5 or Alpha8; they now use retail's format too.
  Six textures (the Leopard's hub and three rocks) were capped at half retail's resolution; they're full size again.
- **HDR textures.** 108 textures that retail stores as half-float or BC6H were extracted as 8-bit. They're restored at retail's
  format: the sky LUTs no longer draw contour bands on the loading-screen planet, lens flares no longer clip, and urban
  buildings collapse in smooth steps instead of jumps.
- **Encounter logic.** Stored inside the map scenes, as retail does. All 85 maps read back equal to the source.
- **Meshes.** Re-saved at version 12, which ends a warning on every load, and 491 wrong bone hashes restored. Geometry is unchanged.
- **Dangling assets.** The six attack direction arc meshes are staged, and all eight lunar terrain layers name real textures.
  Twelve biome assets named drop-pod effects and road materials the rip didn't have; they now name retail's, so the falling drop
  pod shows again. Five moods (Martian dust storm, jungle monsoon and allergy season, and two Restoration missions) named
  camera weather effects that weren't staged, so those moods played without their dust, rain or pollen; the effects are staged now.

### UI and scenes

- Guards `HBS_InputField.UpdateLabel` against re-entry, which overflowed the stack in character creation.
- Replaces a `foreach` that invalidated the in-world manager on the first `CombatHUD.Init`.
- `ElementManager` no longer throws when `UnityGameInstance` is already gone.
- `SVGImage` with a null mesh draws nothing instead of throwing.
- Keeps HBS's additions (`fillAmount`, `SVGCamera`) on SVG Importer 2.0, which is used through a pinned public fork. The fork
  also fixes `SVGImage` with no canvas, which drew blue boxes and green bands on the career HUD.
- **Animators.** `ArgoController` and the crew controllers have their `StateMachineBehaviour` lists restored, so travel ends and the
  crew vary their idles.
- **Star map.** The 21 VFX prefabs behind `StarPrototype` are staged, which ends an exception when a flashpoint is available.
- **Selectables.** Unity 6's default Selected colour replaced retail's highlight. Fixed in 14 prefabs.
- **Prefab references.** Retail references restored where they named nothing in UCU.
- **uGUI package.** The game camera, the movement reticle and the quality settings list still named Unity 2018's UI DLL, which
  a player build can't find. They now name the uGUI package.
- **Script references.** 747 SVG documents and eight Raw Cliffsides dialog images named script GUIDs from the rip. They're
  rebound to UCU's SVG Importer.
- **Ellipsis.** Unity 6's TextMeshPro looked the ellipsis up only in the bold or italic font slot, which BattleTech's fonts leave
  empty, so bold text cut off without one. It falls back to the regular weight, as retail did.
- **Credits.** Each slide was loaded three times, so the slideshow showed every slide three times at a third of retail's pace.
  It shows retail's 152 slides at retail's pace.
  The BATTLETECH, Urban Warfare and Heavy Metal title logos named missing Sprites, which threw four errors; they show retail's art.
- **Wide screens.** The main menu and popup canvases scale with Expand, as retail's do, so 21:9 and 32:9 screens keep the full UI.
- **Startup logos.** Retail's three splash logos play at startup again.
- **Shutdown.** `OnGameShutdown` is added after `UnityGameInstance`, as retail's `Main.unity` does.

### Networking and platform

- The external IP lookup uses HTTPS, because Unity 6 refuses plain HTTP.
- Two achievement call sites rewritten for the Steamworks.NET package.
- Wwise shuts down cleanly when the Editor quits.
- In the Editor, closing a map scene no longer calls `Destroy` outside Play: the mood's sky sphere and the post-processing stack are only destroyed in Play.
- On D3D12, Bink is handed the video's own texture, because the back buffer has no resource behind it.

### Rendering

- **Pipeline.** Built-in Render Pipeline with Linear colour space, retail's configuration, so HBS's renderer runs unchanged.
- **Shaders.** Unity 6 can't load 2018 shader bytecode, so HBS shaders are rebuilt as source and checked against retail's bytecode
  (`tools/shader-recovery/`). Those not yet recovered, including the mech shaders, are served from retail's `shaders`
  bundle through a binder.
- **Compute shaders.** Retail's bundle has no usable kernels on Unity 6, so an overlay bundle replaces it.
- **Variants.** Player builds keep only the keyword sets retail shipped, instead of 17x to 121x as many.
- **Shaders found by name.** SVG Importer and TextMeshPro shaders are Always Included. `Hidden/BT-VFX-Background`,
  `Hidden/BT-ExplosionFlare` and `UI/BT-ColorSwatch` are recovered, ending exceptions on artillery, strikes and Company Customization.
  `SaveShadowParams` asks `Shader.Find` for the right name.
- **Bundle-only materials.** A player build drops the data of materials whose shader exists only in a bundle, leaving metallic paint
  black and the Argo dark. Generated `RetailMaterialData` assets restore it.
- **Decals.** Terrain and road decal materials named a copy of HBS's decal shader with no instanced variant, and Unity 6
  dropped their draws without a log line. They now use the recovered shader, so retail's dust, snow and road markings draw.
- **Realtime GI.** Unity 6's Enlighten couldn't bake the maps at retail's settings, so each battle map's bake is rebuilt from
  retail's own layout: the terrain and the props retail baked, merged into retail's systems. Each prop's ripped pieces are welded
  back into one mesh and kept as a hidden GI proxy with its own material, so the lighting data stays near retail's size and the
  props give Enlighten their colour. The terrain does the same through retail's recovered FarPass shader. At load, each visible
  prop takes its proxy's place in the GI atlas, and its static-batched mesh carries the matching coordinates, so props draw from
  realtime GI as retail's do instead of from light probes. Only maps without live probes get the mood's ambient probe as a fill.
  The proxies sit on their own layer, which no camera draws, so they stay off the loading screen and out of shadows.

