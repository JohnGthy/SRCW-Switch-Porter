# Builds SRCW Switch Porter on Windows.  Needs: git, .NET 10 SDK, Rust (rustup).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot; $ext = Join-Path $root 'external'; $dist = Join-Path $root 'dist\SRCW Switch Porter'

function Get-Dep($name, $url, $sha) {
    $d = Join-Path $ext $name
    if (-not (Test-Path (Join-Path $d '.git'))) { git init -q $d; git -C $d remote add origin $url }
    git -C $d fetch -q --depth 1 origin $sha
    git -C $d checkout -q --force FETCH_HEAD
}
New-Item -ItemType Directory -Force $ext | Out-Null
Get-Dep 'UAssetAPI' 'https://github.com/atenfyr/UAssetAPI' 'b4e8a012939295a6fd890ddc64e57ea78b709b95'
Get-Dep 'CUE4Parse' 'https://github.com/FabianFG/CUE4Parse' 'd3f153e1aac51808b3d5e7d23c1451fcc70bae85'
Get-Dep 'retoc'     'https://github.com/trumank/retoc'      '885a8dae740cb1ce1e41ff2e74f67f9f0c118237'
git -C (Join-Path $ext 'retoc') apply (Join-Path $root 'patches\retoc-unresolved-ids.patch')

cargo build --release --manifest-path (Join-Path $ext 'retoc\Cargo.toml') -p retoc_cli
if ($LASTEXITCODE) { throw 'retoc build failed' }

if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
dotnet publish (Join-Path $root 'src\Gui\Gui.csproj') -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $dist
if ($LASTEXITCODE) { throw 'app build failed' }
Get-ChildItem $dist -Include *.xml,*.pdb,*.so -Recurse | Remove-Item -Force

New-Item -ItemType Directory -Force (Join-Path $dist 'bin'), (Join-Path $dist 'licenses') | Out-Null
Copy-Item (Join-Path $ext 'retoc\target\release\retoc.exe') (Join-Path $dist 'bin')
Copy-Item (Join-Path $root 'licenses\*') (Join-Path $dist 'licenses')
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $dist 'licenses\SRCW-Switch-Porter-MIT.txt')
Copy-Item (Join-Path $root 'src\Gui\README.txt') $dist
Compress-Archive -Force -Path $dist -DestinationPath (Join-Path $root 'dist\SRCW Switch Porter.zip')
Write-Host "Done: dist\SRCW Switch Porter.zip"
