# SRCW Switch Porter

Converts **Sonic Racing: CrossWorlds** PC character/skin mods so they work on the **Nintendo Switch** version of the game.

Pick a PC mod folder, press **Convert for Switch**, and copy the resulting `atmosphere` folder to your SD card.

> **Beta.** Tested on a handful of mods (skins, UI, voice, an animation/blueprint mod). Every conversion writes a
> `conversion report.txt` — please attach it when reporting a problem.

## What it does

- Converts the PC mod format (IoStore `.utoc` / `.ucas`) to the legacy `.pak` format the Switch version loads.
- Re-links references to the game's own assets (materials, physics assets, textures, skeletons, animation compression
  settings…), using the names stored in the mod when available, or exact hash-verified name guessing otherwise.
- Fixes mods stored under non-standard folders (`Content/…` instead of `UNION/Content/…`).
- Moves materials built on custom / PC-only master materials onto the game's `M_Chara_Basic` (or `M_Chara_AfroHair`
  when the texture alpha really cuts holes in the model) and strips PC-compiled shader data.
- Rebuilds custom master materials used directly by a mesh as instances of the game's materials, using the mod's own
  color / normal textures.
- Makes see-through custom materials (glass) and shield-only parts (transformations) invisible instead of solid or
  overlapping.
- Switches off morph targets that would render gray with the game's materials (reported).
- Optionally lowers texture resolution to reduce stutter (UI textures are never touched).
- Handles files duplicated across several containers of one mod.
- Copies text and voice `.pak` files unchanged.
- **Character names:** rename the character a mod replaces (slot auto-detected), or rename characters without a skin
  mod. All names go into a single `SwitchPorter_Names_999_P.pak`.
- Optional **game name database** built from your own Switch dump for fully exact conversions (only file indexes and
  object tables are read).

## What it can't do

- Custom material effects (rim lighting, two-sided rendering, shield-triggered swaps…) can't run on Switch and are lost.
- Script mods that need a mod loader such as UE4SS can't work on Switch. Blueprints the game loads itself (e.g. animation
  notifies) are converted, but test them.
- High-poly models stay high-poly; reducing them needs Unreal Engine.

## Requirements

- Windows 10/11 (64-bit)
- `global.utoc` and `global.ucas` from **your own copy** of the PC game (`UNION\Content\Paks`).
  They are **not included**: they are SEGA's game files.
- A Switch running Atmosphère (mods load from `atmosphere/contents/01006E001823C000/romfs/UNION/Content/Paks`)
- Internet the first time you convert a compressed mod (it downloads Epic's Oodle library once)
- Optional: your own Switch game dump, to build the name database

## Download

Get the latest `SRCW Switch Porter.zip` from the [Releases](../../releases) page, unzip it, and run
`SRCW Switch Porter.exe` (keep the `bin` folder next to it). See `README.txt` inside the zip for usage details.

## Building from source

Requirements: [Git](https://git-scm.com/), [.NET 10 SDK](https://dotnet.microsoft.com/download), [Rust](https://rustup.rs/).

- **Windows:** `powershell -ExecutionPolicy Bypass -File build.ps1`
- **Linux (cross-compiling the Windows build):** `./build.sh` (also needs `mingw-w64` and
  `rustup target add x86_64-pc-windows-gnu`)

The scripts fetch the pinned versions of the dependencies into `external/`, apply `patches/retoc-unresolved-ids.patch`
to retoc, build everything and put the finished app in `dist/`.

### Project layout

| Path | Contents |
|---|---|
| `src/Core` | Conversion logic (material fixes, name resolution, texture tools, pak writer, character names) |
| `src/Gui` | Windows app (WinForms) |
| `src/Cli` | Command-line front end used for testing |
| `src/Core/Templates` | Built-in generic material instance and transparent texture (no game or mod artwork) |
| `patches/` | Change applied to retoc: keeps ids / names of references it can't resolve, so the app can link them |

## Credits

- [retoc](https://github.com/trumank/retoc) by trumank & Archengius (MIT) — IoStore → legacy conversion
- [UAssetAPI](https://github.com/atenfyr/UAssetAPI) by atenfyr (MIT) — asset editing
- [CUE4Parse](https://github.com/FabianFG/CUE4Parse) by FabianFG (Apache-2.0) — reading meshes, textures and paks
- Switch porting method from the [GameBanana tutorial](https://gamebanana.com/tuts/19660) by LukaLover67 and Emasplatter
- Developed with help from Claude (Anthropic)

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Disclaimer

Fan-made tool, not affiliated with or endorsed by SEGA, Nintendo or Epic Games. It contains no game files.
Use it only with games and mods you own or have permission to use.

## License

[MIT](LICENSE)
