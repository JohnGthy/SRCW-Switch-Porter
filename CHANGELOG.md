# Changelog

## 1.9.1
- Window layout: the settings scroll in their own area, while the buttons, progress bar, current step and log stay
  visible at the bottom (drag the divider to resize the log).
- "Conversion settings", "Character name" and "Game name database" fold open/closed with arrows and show a one-line
  summary when closed; the window remembers which ones are open and the log size.

## 1.9
- **Mod parts / outfits:** mods with a `mod.ini` (PC mod manager options) or `.disabled` files get a "Choose..." dialog
  to pick outfits, accessories, transformations and add-ons; only those parts are converted, and a chosen option's
  files replace the base mod's copies.
- Fixed materials whose names end in `_<number>` (e.g. `4_t01_0`): the rebuilt material wasn't found by the mesh, so
  the model showed the default gray checker material (Jack Frost).
- Materials whose textures were set inside the custom PC master (or under other parameter names) are rebuilt with
  those textures instead of plain gray (Goku).
- Parts hidden by the custom material are hidden on Switch too: switches such as `GokuHaloOn` that are off, cartoon
  outlines (inside-out shells that became a gray layer over the model) and transformation-only parts.
- Color-only parts (e.g. a halo) get a solid-color texture.
- Material instances stored in the compact (unversioned) format are rebuilt from their own textures (Zelda's eyes, face
  and hair had the body texture).
- Links between the mod's own files are checked and broken ones reported; references into the mod's own packages are
  matched exactly.
- Text replacements from the mod's `.json` files go into the names pak.
- Better color / normal / MRO texture detection (`_di`, `_dm`, line/occlusion maps ignored, matching names preferred).

## 1.8
- Transparency check can now read BC7 textures. Before, BC7 color textures couldn't be measured and the parts fell back
  to the transparency material (dark "shadow" on head, hands and body, e.g. Maria). If a texture still can't be measured,
  only hair-like materials get the transparency material.
- Morph targets on parts that now use the game's material are switched off inside the mesh itself, so the game's own
  animation curves can no longer drive them either (gray body parts / clothes while a morph is active).
- Meshes whose morph targets can't be inspected (unversioned assets) get their morphs switched off when they use
  converted materials.

## 1.7
- Files are placed by their real package name (fixes mods whose files are stored under unrelated paths, e.g. UI in the wrong folder).
- Replacements of blueprints shared by the whole game (driver animation templates) are left out by default, with an
  option to include them: built for another game version they can break every racer.
- Better color/normal texture detection for rebuilt materials (names like _Alb, _skin, single-texture materials,
  normal maps recognised by their BC5 format). Fixes all-white models.
- Animation montages, blend spaces, sequences and blueprint classes are linked with the right type.
- Much clearer warning when the names pak uses text that isn't from the user's own Switch game.

## 1.6
- Material instances without a parent: left alone when unused, rebuilt from the built-in template when used (fixed a crash).
- Files present in several containers of one mod: kept once when identical, in their own pak when different.
- Error reports include technical details.

## 1.5
- "Transparency material" (Auto / Never / Always). Auto checks whether a texture's alpha really cuts holes in the model
  before using M_Chara_AfroHair (fixes dark shading on bodies with padded alpha).

## 1.4
- Uses the package names stored in newer mods: exact links for skeletons, animation compression settings, meshes.
- Custom master materials used directly by meshes are rebuilt as instances of the game's materials.
- Built-in material template and transparent texture.
- Grouped log lines.

## 1.3
- Parts shown only while shielded (transformations) are hidden instead of overlapping.
- "Make names pak only": rename characters without a skin mod.

## 1.2
- Character names: auto-detected name slot, all names in one SwitchPorter_Names_999_P.pak.

## 1.1
- Fixes mods stored under "Content/…" instead of "UNION/Content/…".
- Output paks named *_999_P.pak for priority over game update paks.
- See-through custom materials become invisible.
- Name guessing for 02_Union (DLC) characters.

## 1.0
- First version: IoStore → pak conversion, game reference linking, custom material fixes, morph fix,
  texture downscaling, optional name database.
