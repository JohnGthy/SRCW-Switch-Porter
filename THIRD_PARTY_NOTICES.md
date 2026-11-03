# Third-party notices

SRCW Switch Porter uses the following open-source projects. Their full license texts are in [`licenses/`](licenses)
and are shipped with every release.

| Project | License | Use | Modified? |
|---|---|---|---|
| [retoc](https://github.com/trumank/retoc) (trumank, Archengius) | MIT | Converts IoStore containers to legacy assets; shipped as `bin/retoc.exe` | Yes — see `patches/retoc-unresolved-ids.patch` |
| [UAssetAPI](https://github.com/atenfyr/UAssetAPI) (atenfyr) | MIT | Reading and editing assets | No |
| [CUE4Parse](https://github.com/FabianFG/CUE4Parse) (FabianFG) | Apache-2.0 | Reading meshes, textures, text and pak files | No (its `NOTICE` file is included) |

At runtime the app may download **Oodle** (`oo2core_9_win64.dll`, © Epic Games / RAD Game Tools) to decompress mods
that use it. It is not part of this repository or of the releases.

No game files are included. `global.utoc` / `global.ucas` and any game dump must come from the user's own copy.
