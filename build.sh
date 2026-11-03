#!/usr/bin/env bash
# Builds the Windows release of SRCW Switch Porter from Linux.
# Needs: git, .NET 10 SDK, Rust (rustup) with target x86_64-pc-windows-gnu, mingw-w64, zip.
set -euo pipefail
root="$(cd "$(dirname "$0")" && pwd)"; ext="$root/external"; dist="$root/dist/SRCW Switch Porter"
get_dep() { local d="$ext/$1"
  [ -d "$d/.git" ] || { git init -q "$d"; git -C "$d" remote add origin "$2"; }
  git -C "$d" fetch -q --depth 1 origin "$3"; git -C "$d" checkout -q --force FETCH_HEAD; }
mkdir -p "$ext"
get_dep UAssetAPI https://github.com/atenfyr/UAssetAPI b4e8a012939295a6fd890ddc64e57ea78b709b95
get_dep CUE4Parse https://github.com/FabianFG/CUE4Parse d3f153e1aac51808b3d5e7d23c1451fcc70bae85
get_dep retoc     https://github.com/trumank/retoc      885a8dae740cb1ce1e41ff2e74f67f9f0c118237
git -C "$ext/retoc" apply "$root/patches/retoc-unresolved-ids.patch"

CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER=x86_64-w64-mingw32-gcc \
  cargo build --release --target x86_64-pc-windows-gnu --manifest-path "$ext/retoc/Cargo.toml" -p retoc_cli

rm -rf "$dist"
dotnet publish "$root/src/Gui/Gui.csproj" -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$dist"
find "$dist" \( -name '*.xml' -o -name '*.pdb' -o -name '*.so' \) -delete
mkdir -p "$dist/bin" "$dist/licenses"
x86_64-w64-mingw32-strip -o "$dist/bin/retoc.exe" "$ext/retoc/target/x86_64-pc-windows-gnu/release/retoc.exe"
cp "$root"/licenses/* "$dist/licenses/"; cp "$root/LICENSE" "$dist/licenses/SRCW-Switch-Porter-MIT.txt"
cp "$root/src/Gui/README.txt" "$dist/"
(cd "$root/dist" && rm -f "SRCW Switch Porter.zip" && zip -qr "SRCW Switch Porter.zip" "SRCW Switch Porter")
echo "Done: dist/SRCW Switch Porter.zip"
