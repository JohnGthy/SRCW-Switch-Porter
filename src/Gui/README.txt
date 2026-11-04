SRCW Switch Porter 1.9.2
========================
Converts Sonic Racing CrossWorlds PC skin mods so they work on the Nintendo Switch version.

HOW TO USE
1. Run "SRCW Switch Porter.exe" (keep the "bin" folder next to it).
2. PC mod folder: the folder with the mod's .utoc / .ucas / .pak files (you can drag it onto the window).
3. global.utoc / .ucas folder: a folder containing global.utoc and global.ucas from the PC game
   (UNION\Content\Paks). Remembered after the first time.
   "Mod parts / outfits": if the mod has options (a mod.ini like PC mod managers use, or files ending in
   ".disabled"), press "Choose..." to pick the outfit, accessories, transformation, etc. Only the chosen
   parts are converted. Without a choice, the parts that are currently enabled in the folder are used.
4. Pick a max texture size (1024 recommended; 512 if the game stutters), then press "Convert for Switch".
   "Transparency material": Auto checks whether a texture's alpha really cuts holes in the model (hair,
   lace...) and only then uses the game's M_Chara_AfroHair; otherwise M_Chara_Basic. If a part looks dark or
   has holes, try "Never" or "Always".
5. Copy the "atmosphere" folder from the output folder (default: "<mod folder>\Switch port") to the root of
   your SD card. Files land in atmosphere\contents\01006E001823C000\romfs\UNION\Content\Paks.
   Read "conversion report.txt" there for everything that was changed.
   Output paks are named "<name>_999_P.pak" so they take priority over the game's own update paks.
   When you re-convert a mod, delete the older .pak files of that mod from the SD card first.

WHAT IT DOES AUTOMATICALLY
- Converts IoStore (.utoc/.ucas) containers to the legacy .pak format the Switch version loads.
- Re-links references to the game's own assets (materials, physics assets, textures, skeletons,
  animation compression settings...), using the names stored in the mod when available.
- Moves materials built on custom/PC-only master materials onto the game's M_Chara_Basic
  (M_Chara_AfroHair for transparent ones) and strips PC-compiled shader data.
- Fixes mods that store files under "Content/..." instead of "UNION/Content/..." (PC ignores this, Switch doesn't).
- Makes see-through custom materials (glass, eye refraction layers) invisible instead of solid.
- Custom master materials used directly by a mesh are rebuilt as instances of the game's materials,
  using the mod's own color/normal textures (see-through ones become invisible).
- Hides parts that only appear while shielded (transformations such as an alternate "shield" model):
  the game's materials can't swap parts on shield, so they would otherwise overlap the normal model.
- Switches off morph targets that would make a part render gray with the game's materials (this can
  include facial expressions; the report lists them).
- Optionally lowers texture resolution (UI textures are never touched).
- Material instances with no parent are rebuilt (or left alone if nothing uses them).
- Files that appear in several containers of the same mod are kept once when identical, or in their own
  pak when different (the report says which).
- Places every file by its real package name (some mods store files under unrelated paths).
- Leaves out replacements of blueprints shared by the whole game (e.g. driver animation templates) unless
  "Include shared game blueprints" is ticked: made for another game version, they can break every racer.
- Copies text/audio .pak files unchanged.
- Mods with options (mod.ini / .disabled files): only the chosen parts are converted, and a chosen
  outfit/accessory replaces the base mod's version of the same file.
- Hides parts a custom material switches off (e.g. an accessory not chosen), cartoon outlines (they would
  become a gray shell over the model) and parts only shown while transformed.
- Materials whose textures were set inside the custom PC material (or use other texture names) get those
  textures back; parts with only a color (e.g. a halo) get a solid-color texture.
- Name texts from the mod's .json text files (e.g. "chara12001" = "GOKU", machine names) go into the
  names pak (needs the game's text, see below).
- Materials that cut their shape out of a black/white mask (e.g. a cloud made of flat cards) get a
  cut-out texture in the material's color instead of showing the mask as black squares.
- Checks that the mod's own files link to each other correctly and reports any broken link.

CHARACTER NAMES (OPTIONAL)
Type a "New name" to rename the character the mod replaces. The name slot is detected from the mod's
UI files (e.g. UI_CharaIcon_sha_02 -> chara106 "SHADOW (AI RACER)", UI_..._Extnd11_Character001 ->
chara11001 "JOKER"); you can also pick it yourself from the list.
Every name you set is remembered and written into ONE pak, SwitchPorter_Names_999_P.pak, so names from
different mods don't overwrite each other. Copy the newest one to the SD card (replacing the old one).
"Clear saved names" forgets them.
The game's text is needed as a starting point. It comes from (in order): the mod's own text pak, the
"Base game text" pak you pick, or the text saved when you build the name database from your Switch dump.
Use the dump's text: PC text packs are usually older than your Switch game, and characters added in
later updates then show code names (like chara18001) instead of their names.
Only one text pak can be active at a time; a mod's own text pak is merged into the names pak.

RENAMING WITHOUT A SKIN MOD (e.g. on top of a text mod)
Leave "PC mod folder" empty, pick the text mod's .pak as "Base game text", type the new name, pick the
name slot from the list and press "Make names pak only". The result contains all of the text mod's text
plus your names, so copy only SwitchPorter_Names_999_P.pak (not the original text mod) to the SD card.

GAME NAME DATABASE (OPTIONAL)
Without it, references to game assets are found by guessing names from the game's naming patterns; a guess
is only used when it matches the mod's stored hash exactly, so it's never wrong, but unusual names can be
missed (the report says so). To resolve everything, point "Switch dump Paks folder" at your dump's
UNION\Content\Paks folder and press "Build database" once. Only file indexes and object tables are read.
If your dump's paks are encrypted, enter the AES key.

LIMITATIONS
- Custom material effects (rim light, two-sided rendering, etc.) can't run on Switch and are lost.
- Blueprints that the game itself loads (e.g. animation notifies used by a character's animations) are
  converted, but they run game code that may differ between the PC and Switch versions: test them.
  Blueprints that need a mod loader such as UE4SS can't work on Switch.
- Very heavy meshes stay heavy; reducing them needs Unreal Engine.
- Converting Oodle-compressed containers downloads Epic's oo2core_9_win64.dll into "bin" the first time
  (needs internet once).

Built with retoc (MIT), UAssetAPI (MIT) and CUE4Parse (Apache-2.0); see the "licenses" folder.
