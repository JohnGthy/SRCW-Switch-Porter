using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
namespace SwitchPorter;

public class ExportInfo
{
    public string Name { get; set; }          // object path relative to package, e.g. "M_Foo" or "Outer.Inner"
    public string ClassPackage { get; set; }  // e.g. "/Script/Engine"
    public string ClassName { get; set; }     // e.g. "MaterialInstanceConstant"
}

/// <summary>Game asset names built once from a Switch dump. Lets every conversion resolve references exactly.</summary>
public class NameDb
{
    public string Source { get; set; }
    public DateTime Built { get; set; }
    public Dictionary<string, string> Packages { get; set; } = new();       // package id (hex) -> package name
    public Dictionary<string, ExportInfo> Exports { get; set; } = new();    // "pkgid:exporthash" -> export
    public static string Key(ulong pkg, ulong hash) => $"{pkg:x16}:{hash:x16}";

    public void Save(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, JsonSerializer.Serialize(this)); }
    public static NameDb Load(string path) => File.Exists(path) ? JsonSerializer.Deserialize<NameDb>(File.ReadAllText(path)) : null;

    static string PackageNameFromPath(string path)
    {
        // "UNION/Content/01_Union/X.uasset" -> "/Game/01_Union/X"
        var p = path.Replace('\\', '/'); var noExt = p.Substring(0, p.LastIndexOf('.'));
        var parts = noExt.Split('/');
        int ci = Array.FindIndex(parts, s => s.Equals("Content", StringComparison.OrdinalIgnoreCase));
        if (ci < 1) return null;
        string root = parts[0].Equals("Engine", StringComparison.OrdinalIgnoreCase) && ci == 1 ? "/Engine"
                    : ci == 1 ? "/Game" : "/" + parts[ci - 1]; // plugin content
        return root + "/" + string.Join("/", parts.Skip(ci + 1));
    }

    /// <param name="exportPrefixes">package-name prefixes whose export tables are also recorded</param>
    public static NameDb BuildFromDump(string paksDir, string aesKey, string nativeDir, Log log, Action<int, int> progress, CancellationToken ct,
                                      string[] exportPrefixes = null, string baseTextOut = null)
    {
        exportPrefixes ??= new[] { "/Game/01_Union/Asset/", "/Game/01_Union/UI/" };
        log.Info($"Reading game file list from {paksDir} ...");
        try
        {
            if (nativeDir != null)
            {
                Directory.CreateDirectory(nativeDir);
                var oodle = Path.Combine(nativeDir, CUE4Parse.Compression.OodleHelper.OodleFileName);
                if (!File.Exists(oodle)) CUE4Parse.Compression.OodleHelper.DownloadOodleDllAsync(ref oodle).GetAwaiter().GetResult();
                if (File.Exists(oodle)) CUE4Parse.Compression.OodleHelper.Initialize(oodle);
                var zlib = Path.Combine(nativeDir, CUE4Parse.Compression.ZlibHelper.DllName);
                if (!File.Exists(zlib)) CUE4Parse.Compression.ZlibHelper.DownloadDll(zlib);
                if (File.Exists(zlib)) CUE4Parse.Compression.ZlibHelper.Initialize(zlib);
            }
        }
        catch (Exception ex) { log.Warn("Could not prepare decompression libraries: " + ex.Message); }

        var prov = new DefaultFileProvider(paksDir, SearchOption.AllDirectories, new VersionContainer(EGame.GAME_UE5_4));
        prov.Initialize();
        int mounted = prov.Mount();
        if (!string.IsNullOrWhiteSpace(aesKey))
        {
            var key = new FAesKey(aesKey.Trim().StartsWith("0x") ? aesKey.Trim() : "0x" + aesKey.Trim());
            mounted += prov.SubmitKey(new FGuid(), key);
            foreach (var g in prov.RequiredKeys.ToList()) mounted += prov.SubmitKey(g, key);
        }
        if (prov.RequiredKeys.Count > 0)
            log.Warn($"{prov.RequiredKeys.Count} archive(s) in the dump are encrypted and were skipped (an AES key is needed for them).");
        log.Info($"Mounted {mounted} archive(s), {prov.Files.Count} files.");
        if (prov.Files.Count == 0) throw new Exception("No game files could be read from that folder. Pick the dump's UNION\\Content\\Paks folder.");

        var db = new NameDb { Source = paksDir, Built = DateTime.Now };
        if (baseTextOut != null)
        {
            int n = 0;
            foreach (var f in prov.Files.Values.Where(f => f.Path.EndsWith("/Game.locres", StringComparison.OrdinalIgnoreCase) && f.Path.Contains("Localization/Game/", StringComparison.OrdinalIgnoreCase)))
            {
                try { var lang = f.Path.Split('/')[^2]; var dst = Path.Combine(baseTextOut, lang, "Game.locres"); Directory.CreateDirectory(Path.GetDirectoryName(dst)); File.WriteAllBytes(dst, f.Read()); n++; }
                catch (Exception ex) { log.Warn("Could not save game text " + f.Path + ": " + ex.Message); }
            }
            log.Info(n > 0 ? $"Saved the game's text ({n} languages) for character names." : "No game text found in the dump (character names will need a base text pak).");
        }
        var assets = prov.Files.Values.Where(f => f.Extension is "uasset" or "umap").ToList();
        var toScan = new List<(CUE4Parse.FileProvider.Objects.GameFile file, ulong id, string name)>();
        foreach (var f in assets)
        {
            var pn = PackageNameFromPath(f.Path); if (pn == null) continue;
            var id = CityHash.PackageId(pn); db.Packages[$"{id:x16}"] = pn;
            if (exportPrefixes.Any(pfx => pn.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))) toScan.Add((f, id, pn));
        }
        log.Info($"Found {db.Packages.Count} game packages; reading object tables of {toScan.Count} character/UI packages ...");
        int done = 0, failed = 0;
        foreach (var (file, id, pn) in toScan)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (prov.LoadPackage(file) is Package pkg)
                    for (int i = 0; i < pkg.ExportMap.Length; i++)
                    {
                        var ex = pkg.ExportMap[i];
                        var ro = pkg.ResolvePackageIndex(new CUE4Parse.UE4.Objects.UObject.FPackageIndex((CUE4Parse.UE4.Assets.IPackage)pkg, i + 1));
                        // build path relative to the package: Outer.Inner
                        var names = new List<string>(); var cur = ro;
                        while (cur != null && cur.ExportIndex >= 0) { names.Insert(0, cur.Name.Text); cur = cur.Outer; }
                        var rel = string.Join("/", names);
                        var cls = ro?.Class; var clsPkg = cls?.Outer?.Name.Text;
                        db.Exports[Key(id, CityHash.ExportHash(rel))] = new ExportInfo
                        { Name = string.Join(".", names.Take(1).Concat(names.Skip(1)).ToArray()).Replace("/", "."), ClassName = cls?.Name.Text, ClassPackage = clsPkg };
                    }
            }
            catch { failed++; }
            if (++done % 50 == 0 || done == toScan.Count) progress?.Invoke(done, toScan.Count);
        }
        if (failed > 0) log.Warn($"{failed} package(s) could not be read from the dump.");
        log.Info($"Name database built: {db.Packages.Count} packages, {db.Exports.Count} objects.");
        return db;
    }
}
