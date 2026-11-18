# Changelog

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

