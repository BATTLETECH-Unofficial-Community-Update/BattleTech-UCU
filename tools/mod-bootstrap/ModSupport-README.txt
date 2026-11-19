ModSupport: ModTek on BattleTech UCU
====================================

The installer's "Mod support" box (ticked by default) copies winhttp.dll, BattleTechPortBootstrap.dll and
doorstop_config.ini from this folder to beside BattleTech.exe. Nothing in this folder runs by itself.

To run ModTek v4 mods, put ModTek and your mods in a Mods folder beside BattleTech.exe, so that
Mods\ModTek\lib\ModTek.Preloader.dll exists, and start the game. The first start builds ModTek's cache and is slow.
BattleTechPortBootstrap.log beside BattleTech.exe ends with "done|retargeted=5" when it worked, and reads
"no ModTek in Mods\ModTek: nothing to do" when there are no mods. For BattleTech Advanced, double-click Link BTA.cmd in
this folder: it links each BTA mod folder into UCU's Mods folder (no copy, no extra disk space) and copies your saves,
and BTA updates reach UCU without a re-run. Both installs must be on NTFS drives in this PC. The two games then share
those folders, and mods write into them while the game runs: UCU writes logs, mod settings and each mod's
modstate.json into the BTA install, so turning a mod off in UCU turns it off in BTA too. Link BTA.cmd -Unlink removes
the links only, never what they point to.

To keep the two games apart, double-click Link BTA (copy).cmd instead. It copies the mods (about 20 GB) and only reads
the BTA install; run it again after each BTA update.

On Linux or a Steam Deck (Proton), open a terminal in this folder and run: bash link-bta.sh
It does the same with symbolic links (--copy copies instead; --unlink undoes it). Under Proton the mod loader also
needs this in UCU's Steam launch options: WINEDLLOVERRIDES="winhttp=n,b" %command%

To turn mod support off, run the installer again with the box unticked. Copying the three files beside BattleTech.exe
by hand turns it on, as the box does.

To opt in to combat saves with a large mod pack, open the UCU launcher, go to Mods, and check "EXPERIMENTAL: Enable in-mission saves. Modpacks disable this for a good reason! We try to take away the reason with some on-the-fly changes to many mods. Make sure you still save before and after missions as this feature is a priority but it is still new! These saves can only be loaded by UCU with this option enabled." This creates only UCU's Mods\.port\bta-combat-saves.on marker; it does not edit BTA or any
other mod files. Uncheck it to remove the marker. The setting takes effect the next time the game starts. Saves made
with it on load only on UCU with the save shim and this option enabled; they won't load on the 2018 game or retail BTA.
IRTweaks still skips the story-mission-start combat autosave, and its in-combat quit popup keeps its own wording.

winhttp.dll is UnityDoorstop 4.6.0 (x64), licensed under the GNU LGPL 2.1; the license is DOORSTOP-LICENSE.txt. Its source
is at https://github.com/NeighTools/UnityDoorstop (tag v4.6.0). ModTek's own copy of Doorstop crashes every D3D12 start,
which is why this one is used. BattleTechPortBootstrap.dll is built from tools/mod-bootstrap in the project's source.
