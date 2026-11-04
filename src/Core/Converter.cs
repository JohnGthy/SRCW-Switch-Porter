using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using static SwitchPorter.AssetUtil;
namespace SwitchPorter;

public class Options
{
    public string ModDir;            // folder with the PC mod (.utoc/.ucas/.pak)
    public string GlobalDir;         // folder with global.utoc + global.ucas
    public string OutputDir;         // where the SD-card layout is written
    public string RetocPath;         // retoc(.exe)
    public string NameDbPath;        // optional names.json built from a dump
    public int MaxTextureSize = 1024;   // 0 = keep
    public string TitleId = "01006E001823C000";
    public string WorkDir;           // temp; default under system temp
    public bool KeepWorkDir;
    public TransparencyMode Transparency = TransparencyMode.Auto;
    public bool IncludeSharedBlueprints;   // keep replacements of blueprints used by the whole game  // when to use M_Chara_AfroHair for textures with alpha
    // ---- character name ----
    public string NewName;                                   // name to give the character this mod replaces (null = none)
    public string NameKey;                                   // name slot to change (null = auto-detect)
    public Dictionary<string, string> SavedNames = new();    // names set in earlier conversions (key -> name), all kept in one pak
    public string BaseTextPath;                              // optional: .pak or folder with Game.locres files
    public string SavedBaseTextDir;                          // game text saved when the name database was built
    // ---- optional parts ----
    public List<string> InputFiles;                          // files to convert (may end in .disabled); null = every enabled file in ModDir
    public HashSet<string> PreferredContainers;              // containers of selected options: their copy of an asset wins
    public string OptionsSummary;                            // shown in the report
}

public enum TransparencyMode { Auto, Never, Always }

public class Result
{
    public bool Success;
    public string PaksDir;
    public List<string> Paks = new();
    public string ReportPath;
    public Dictionary<string, string> Names;   // all names written to the names pak (to save for next time)
}

public class Converter
{
    const string CharaBasic = "/Game/01_Union/Asset/Character/Master_Material/M_Chara_Basic";
    const string CharaAfroHair = "/Game/01_Union/Asset/Character/Master_Material/M_Chara_AfroHair";
    static readonly string[] AlphaFormats = { "PF_DXT3", "PF_DXT5", "PF_BC7", "PF_B8G8R8A8", "PF_R8G8B8A8", "PF_FloatRGBA", "PF_A8R8G8B8" };

    readonly Options o; readonly Log log; readonly CancellationToken ct;
    readonly Dictionary<string, ModAsset> byPackage = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> reparented = new(StringComparer.OrdinalIgnoreCase);   // mod MIC short names now on a game master
    NameDb db; string modTextPak;
    readonly Dictionary<string, List<string>> containersOf = new(StringComparer.OrdinalIgnoreCase); // package -> containers (stems) holding it
    readonly List<ModAsset> extraVersions = new();   // differing copies of a package that several containers contain

    public Converter(Options options, Log log, CancellationToken ct = default) { o = options; this.log = log; this.ct = ct; }

    public Result Run()
    {
        var res = new Result();
        o.ModDir = Path.GetFullPath(o.ModDir); o.OutputDir = Path.GetFullPath(o.OutputDir);
        if (!string.IsNullOrEmpty(o.GlobalDir)) o.GlobalDir = Path.GetFullPath(o.GlobalDir);
        if (o.WorkDir != null) o.WorkDir = Path.GetFullPath(o.WorkDir);
        if (o.RetocPath != null && File.Exists(o.RetocPath)) o.RetocPath = Path.GetFullPath(o.RetocPath);
        string work = o.WorkDir ?? Path.Combine(Path.GetTempPath(), "SwitchPorter", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(work);
            db = string.IsNullOrEmpty(o.NameDbPath) ? null : NameDb.Load(o.NameDbPath);
            log.Info(db != null ? $"Using game name database ({db.Packages.Count} packages, built {db.Built:d})." : "No game name database: missing references will be found by name guessing.");

            // ---------- 1. inputs ----------
            // files ending in ".disabled" are parts switched off in a PC mod manager; they're used only when picked in the options
            static string Plain(string f) => f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? f[..^".disabled".Length] : f;
            var all = o.InputFiles ?? Directory.GetFiles(o.ModDir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrEmpty(o.OptionsSummary)) log.Info("Mod options: " + o.OptionsSummary);
            string Sibling(string f, string ext)
            {
                var want = Path.ChangeExtension(Plain(f), ext);
                return all.FirstOrDefault(x => Plain(x).Equals(want, StringComparison.OrdinalIgnoreCase))
                       ?? (File.Exists(want) ? want : File.Exists(want + ".disabled") ? want + ".disabled" : null);
            }
            var utocs = all.Where(f => Plain(f).EndsWith(".utoc", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(Plain(f)).Equals("global.utoc", StringComparison.OrdinalIgnoreCase)).ToList();
            var paks = all.Where(f => Plain(f).EndsWith(".pak", StringComparison.OrdinalIgnoreCase)).ToList();
            var legacyPaks = paks.Where(p => Sibling(p, ".utoc") == null).ToList();
            modTexts = ReadJsonTexts(all.Where(f => Plain(f).EndsWith(".json", StringComparison.OrdinalIgnoreCase)));
            if (utocs.Count == 0 && legacyPaks.Count == 0) throw new Exception("No .utoc or .pak files found in the mod folder.");
            var gUtoc = Path.Combine(o.GlobalDir ?? "", "global.utoc"); var gUcas = Path.Combine(o.GlobalDir ?? "", "global.ucas");
            if (utocs.Count > 0 && (!File.Exists(gUtoc) || !File.Exists(gUcas))) throw new Exception("global.utoc / global.ucas not found in the selected global files folder.");
            log.Info($"Mod: {utocs.Count} IoStore container(s), {legacyPaks.Count} legacy .pak file(s).");

            var outPaks = Path.Combine(o.OutputDir, "atmosphere", "contents", o.TitleId, "romfs", "UNION", "Content", "Paks");
            Directory.CreateDirectory(outPaks); res.PaksDir = outPaks;

            // ---------- 2. convert IoStore -> legacy ----------
            var legacyRoot = Path.Combine(work, "legacy");
            var containerOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // package name -> pak name
            if (utocs.Count > 0)
            {
                var inDir = Path.Combine(work, "in"); Directory.CreateDirectory(inDir);
                File.Copy(gUtoc, Path.Combine(inDir, "global.utoc"), true); File.Copy(gUcas, Path.Combine(inDir, "global.ucas"), true);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var u in utocs)
                {
                    var stem = Path.GetFileNameWithoutExtension(Plain(u)); var name = stem; int k = 2;
                    while (!used.Add(name)) name = stem + "_" + k++;
                    File.Copy(u, Path.Combine(inDir, name + ".utoc"), true);
                    var ucas = Sibling(u, ".ucas");
                    if (ucas == null || !File.Exists(ucas)) throw new Exception($"{Path.GetFileName(Path.ChangeExtension(Plain(u), ".ucas"))} is missing next to {Path.GetFileName(u)}.");
                    File.Copy(ucas, Path.Combine(inDir, name + ".ucas"), true);
                    foreach (var pn in Manifest(Path.Combine(inDir, name + ".utoc"), work))
                    {
                        containerOf[pn] = PakName(name);
                        (containersOf.TryGetValue(pn, out var cl) ? cl : containersOf[pn] = new()).Add(name);
                    }
                }
                log.Info("Converting containers to the Switch (.pak) asset format ...");
                var outp = RunRetoc($"to-legacy \"{inDir}\" \"{legacyRoot}\"", work);
                foreach (var line in outp.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("Failed to convert"))) log.Error(line);
                var sob = Path.Combine(legacyRoot, "scriptobjects.bin"); if (File.Exists(sob)) File.Delete(sob);
            }

            // legacy paks: assets are processed, anything else is copied unchanged
            foreach (var p0 in legacyPaks)
            {
                var p = p0;
                if (p.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                {   // picked in the options: use it under its normal name
                    var dir = Path.Combine(work, "enabled"); Directory.CreateDirectory(dir);
                    p = Path.Combine(dir, Path.GetFileName(Plain(p0))); File.Copy(p0, p, true);
                }
                var name = PakName(Path.GetFileNameWithoutExtension(p));
                var files = LegacyPakFiles(p, work, out var extractDir);
                if (files == null) { log.Warn($"{Path.GetFileName(p)} could not be read; copied unchanged."); CopyOut(p, outPaks); res.Paks.Add(Path.GetFileName(p)); continue; }
                if (files.Any(f => f.EndsWith("/Game.locres", StringComparison.OrdinalIgnoreCase)) && !files.Any(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)))
                    modTextPak = p;
                if (!files.Any(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)))
                {
                    CopyOut(p, outPaks); res.Paks.Add(Path.GetFileName(p));
                    log.Info($"{Path.GetFileName(p)}: no models/materials inside (e.g. text or audio), copied unchanged.");
                    continue;
                }
                foreach (var f in files)
                {
                    var dst = Path.Combine(legacyRoot, f); Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(Path.Combine(extractDir, f), dst, true);
                    if (f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) containerOf[PackageNameFromRel(f)] = name;
                }
                log.Info($"{Path.GetFileName(p)}: legacy pak with assets, will be checked and repacked.");
            }

            // ---------- 3. load assets ----------
            if (Directory.Exists(legacyRoot))
                foreach (var f in Directory.GetFiles(legacyRoot, "*.uasset", SearchOption.AllDirectories).Concat(Directory.GetFiles(legacyRoot, "*.umap", SearchOption.AllDirectories)).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var rel = Path.GetRelativePath(legacyRoot, f).Replace('\\', '/');
                    var pn = pkgByFile.TryGetValue(rel, out var mpn) ? mpn : PackageNameFromRel(rel);
                    try
                    {
                        var a = new ModAsset { Path = f, RelPath = rel, PackageName = pn, Asset = new UAsset(f, EngineVersion.VER_UE5_4) };
                        a.Container = containerOf.TryGetValue(pn, out var c) ? c : PakName(Path.GetFileName(o.ModDir));
                        byPackage[pn] = a;
                    }
                    catch (Exception ex) { log.Error($"Could not read {rel}: {ex.Message}"); }
                }
            log.Info($"{byPackage.Count} asset(s) converted.");
            int moved = byPackage.Values.Count(a => !CanonicalRel(a.PackageName, ".uasset").Equals(a.RelPath, StringComparison.OrdinalIgnoreCase) && a.PackageName.StartsWith("/Game/"));
            ExcludeSharedBlueprints();
            if (moved > 0) log.Change($"{moved} asset(s) were stored under a different path than their real name in the PC mod; placed where the Switch version looks for them (by their real package name).");
            ct.ThrowIfCancellationRequested();

            CheckDuplicates(Path.Combine(work, "in"), work);

            // ---------- 4. fix references to game assets ----------
            ResolveReferences();
            ct.ThrowIfCancellationRequested();

            // ---------- 5. materials ----------
            legacyRootForChecks = legacyRoot;
            FixMaterials();
            foreach (var a in byPackage.Values.Where(a => a.Dirty && !a.Removed)) { a.Asset.Write(a.Path); a.Dirty = false; }
            MakeSeeThroughInvisible();
            ConvertDirectMasters();
            RebuildParentless();

            // ---------- 6. morph targets ----------
            FixMorphs(legacyRoot);
            foreach (var a in byPackage.Values.Where(a => a.Dirty && !a.Removed)) { a.Asset.Write(a.Path); a.Dirty = false; }

            // ---------- 7. textures ----------
            if (o.MaxTextureSize > 0)
                foreach (var a in byPackage.Values.Where(a => !a.Removed && a.MainClass == "Texture2D" && !a.PackageName.Contains("/UI/", StringComparison.OrdinalIgnoreCase)))
                    try { TextureTools.Downscale(a.Path, o.MaxTextureSize, log); } catch (Exception ex) { log.Warn($"{a.ShortName}: texture not resized ({ex.Message})"); }

            ValidateLinks();

            // ---------- 8. pack ----------
            var allOut = byPackage.Values.Where(a => !a.Removed).Concat(extraVersions).ToList();
            foreach (var c in containersOf.Values.SelectMany(x => x).Distinct().Select(PakName).Where(c => !allOut.Any(a => a.Container == c)))
                log.Info($"{c}.pak not written: everything in it is already in another pak of this mod (identical copies).");
            foreach (var grp in allOut.GroupBy(a => a.Container))
            {
                var files = new Dictionary<string, string>();
                foreach (var a in grp)
                    foreach (var ext in new[] { ".uasset", ".umap", ".uexp", ".ubulk", ".uptnl" })
                    {
                        var f = Path.ChangeExtension(a.Path, ext);
                        if ((ext is ".uasset" or ".umap") && f != a.Path) continue;
                        if (File.Exists(f)) files[CanonicalRel(a.PackageName, ext)] = f;
                    }
                var pak = Path.Combine(outPaks, grp.Key + ".pak");
                PakWriter.Write(pak, files);
                res.Paks.Add(grp.Key + ".pak");
                log.Info($"Wrote {grp.Key}.pak ({grp.Count()} asset(s), {new FileInfo(pak).Length / 1024} KB)");
            }
            DoNames(res, outPaks, work);
            res.Success = log.Errors == 0;
        }
        catch (OperationCanceledException) { log.Error("Cancelled."); }
        catch (Exception ex) { log.Error(ex.Message); log.Info("Technical details (please include when reporting): " + ex.ToString().Replace("\r", "").Replace("\n", " | ")); }
        finally
        {
            if (!o.KeepWorkDir) try { Directory.Delete(work, true); } catch { }
            try
            {
                Directory.CreateDirectory(o.OutputDir);
                res.ReportPath = Path.Combine(o.OutputDir, "conversion report.txt");
                File.WriteAllText(res.ReportPath, $"SRCW Switch Porter report - {DateTime.Now}\nMod: {o.ModDir}\n\n" + log.ToText());
            }
            catch { }
        }
        return res;
    }

    /// <summary>Only (re)builds the names pak: no skin mod needed.</summary>
    public Result RunNamesOnly()
    {
        var res = new Result();
        o.OutputDir = Path.GetFullPath(o.OutputDir);
        string work = Path.Combine(Path.GetTempPath(), "SwitchPorter", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(work);
            var outPaks = Path.Combine(o.OutputDir, "atmosphere", "contents", o.TitleId, "romfs", "UNION", "Content", "Paks");
            Directory.CreateDirectory(outPaks); res.PaksDir = outPaks;
            if (!string.IsNullOrWhiteSpace(o.NewName) && string.IsNullOrWhiteSpace(o.NameKey))
                throw new Exception("Pick which name slot to change (there is no skin mod to detect it from).");
            if (string.IsNullOrWhiteSpace(o.NewName) && (o.SavedNames?.Count ?? 0) == 0)
                throw new Exception("Type a new name (and pick its name slot) first.");
            DoNames(res, outPaks, work);
            res.Success = log.Errors == 0 && res.Paks.Count > 0;
        }
        catch (Exception ex) { log.Error(ex.Message); }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
            try { res.ReportPath = Path.Combine(o.OutputDir, "conversion report.txt"); File.WriteAllText(res.ReportPath, $"SRCW Switch Porter report - {DateTime.Now}\nNames only\n\n" + log.ToText()); } catch { }
        }
        return res;
    }

    // ===================================================================== character names
    void DoNames(Result res, string outPaks, string work)
    {
        var names = new Dictionary<string, string>(o.SavedNames ?? new());
        foreach (var (ns, key, val) in modTexts)
        {
            var k = ns == Naming.Ns ? key : ns + "::" + key;
            names[k] = val;
        }
        if (modTexts.Count > 0) log.Info($"The mod's text files set {modTexts.Count} text(s): " + string.Join(", ", modTexts.Select(t => $"{t.key} = {t.value}")));
        bool wantName = !string.IsNullOrWhiteSpace(o.NewName);
        if (!wantName && names.Count == 0) return;           // nothing to do; text paks were copied as-is
        if (!wantName && modTexts.Count > 0 && o.SavedBaseTextDir == null && o.BaseTextPath == null && modTextPak == null)
        { log.Warn("The mod has text files (names), but no game text is available to put them in. Build the name database from your Switch dump (it saves your game's text) and convert again."); return; }
        // base game text: the mod's own text pak, then a user-picked one, then the text saved from the Switch dump
        Dictionary<string, byte[]> baseText = null; string from = null;
        foreach (var (src, label) in new[] { (modTextPak, "the mod's text pak"), (o.BaseTextPath, "the selected base text"), (o.SavedBaseTextDir, "your Switch dump") })
        {
            if (string.IsNullOrEmpty(src)) continue;
            try { var t = Naming.LoadBase(src, work); if (t.ContainsKey("en")) { baseText = t; from = label; break; } } catch (Exception ex) { log.Warn($"Could not read text from {src}: {ex.Message}"); }
        }
        if (baseText == null)
        {
            if (wantName) log.Error("Character name not changed: no game text available. Pick a text pak as base game text, or build the name database from your Switch dump (it saves the game's text).");
            return;
        }
        var en = LocRes.Read(baseText["en"]);
        if (wantName)
        {
            var key = string.IsNullOrWhiteSpace(o.NameKey) ? Naming.Detect(byPackage.Keys, en) : o.NameKey.Trim();
            if (key == null || en.Find(Naming.Ns, key) == null)
                log.Warn("Character name not changed: couldn't tell which character slot this mod replaces. Pick the name slot manually.");
            else
            {
                log.Change($"Name slot {key} (\"{en.Get(Naming.Ns, key)}\") will show \"{o.NewName.Trim()}\"");
                names[key] = o.NewName.Trim();
            }
        }
        if (names.Count == 0) return;
        var pak = Naming.WritePak(baseText, names, outPaks, work, log);
        if (modTextPak == null && from == "the selected base text") log.Info($"{Path.GetFileName(o.BaseTextPath)} is included in {Naming.PakName}.pak (all of its text plus your names) - don't also copy it to the SD card.");
        if (modTextPak != null)
        {
            var copied = Path.Combine(outPaks, Path.GetFileName(modTextPak));
            if (File.Exists(copied)) { File.Delete(copied); res.Paks.Remove(Path.GetFileName(modTextPak)); }
            log.Info($"{Path.GetFileName(modTextPak)} was merged into {Naming.PakName}.pak (both replace the same game text files).");
        }
        res.Paks.Add(Path.GetFileName(pak)); res.Names = names;
        log.Info($"Wrote {Naming.PakName}.pak from {from}, with {names.Count} custom name(s): " + string.Join(", ", names.Select(kv => $"{kv.Key} = {kv.Value}")));
        if (from != "your Switch dump")
            log.Warn("IMPORTANT: this names pak replaces ALL of the game's text with text that does not come from your Switch game. " +
                     "Characters added in later game updates will show code names (like chara18001) instead of their names, and other menu text can be outdated. " +
                     "Build the name database from your Switch dump (it saves your game's own text) and convert again to avoid this. If names look like chara12345 in game, delete SwitchPorter_Names_999_P.pak.");
        log.Info("This one pak holds all names you've set so far; copy it over the old one. Only one text pak can be active at a time.");
    }

    /// <summary>
    /// Several containers can hold the same package (e.g. a minimap icon in both a "UI" and a "Minimap" pak).
    /// Identical copies are kept once; different copies are kept in their own pak, like on PC.
    /// </summary>
    void CheckDuplicates(string inDir, string work)
    {
        string SepOut(string cont)
        {
            var dir = Path.Combine(work, "sep_" + cont); var sin = Path.Combine(dir, "in"); var sout = Path.Combine(dir, "out");
            if (Directory.Exists(sout)) return sout;
            Directory.CreateDirectory(sin);
            foreach (var f in new[] { "global.utoc", "global.ucas", cont + ".utoc", cont + ".ucas" }) File.Copy(Path.Combine(inDir, f), Path.Combine(sin, f), true);
            RunRetoc($"to-legacy \"{sin}\" \"{sout}\"", work);
            return sout;
        }
        static bool Same(string a0, string b0) => new[] { ".uasset", ".uexp", ".ubulk" }.All(ext =>
        {
            var a = Path.ChangeExtension(a0, ext); var b = Path.ChangeExtension(b0, ext);
            return File.Exists(a) == File.Exists(b) && (!File.Exists(a) || File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)));
        });
        foreach (var (pn, conts) in containersOf.Where(kv => kv.Value.Count > 1))
        {
            if (!byPackage.TryGetValue(pn, out var main)) continue;
            // each container's own copy
            var copies = new List<(string cont, string path)>();
            try { foreach (var c in conts) { var f = Path.Combine(SepOut(c), main.RelPath); if (File.Exists(f)) copies.Add((c, f)); } }
            catch (Exception ex) { log.Warn($"Could not compare the copies of {main.ShortName}: {ex.Message}"); continue; }
            if (copies.Count < 2) continue;
            // a selected option (outfit, accessory...) replaces the base mod's version of the same file
            var pref = o.PreferredContainers == null ? default : copies.LastOrDefault(c => o.PreferredContainers.Contains(c.cont));
            if (pref.cont != null)
            {
                foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk", ".uptnl" })
                {
                    var src = Path.ChangeExtension(pref.path, ext); var dst = Path.ChangeExtension(main.Path, ext);
                    if (File.Exists(src)) File.Copy(src, dst, true); else if (File.Exists(dst)) File.Delete(dst);
                }
                main.Asset = new UAsset(main.Path, EngineVersion.VER_UE5_4); main.Container = PakName(pref.cont);
                log.Info($"{main.ShortName}: using the version from the selected option {pref.cont}.");
                continue;
            }
            // the converted (and later fixed) asset is the copy it matches; every other differing copy stays in its own pak
            var mine = copies.FirstOrDefault(c => Same(main.Path, c.path));
            if (mine.cont != null) main.Container = PakName(mine.cont);
            var others = copies.Where(c => c.cont != mine.cont).ToList();
            var differing = others.Where(c => mine.cont == null || !Same(mine.path, c.path)).ToList();
            foreach (var c in others.Except(differing)) log.Info($"{main.ShortName} is in both {main.Container} and {PakName(c.cont)} (identical); included once.");
            foreach (var c in differing.Where(c => !differing.Take(differing.IndexOf(c)).Any(d => Same(d.path, c.path))))
            {
                extraVersions.Add(new ModAsset { Path = c.path, RelPath = main.RelPath, PackageName = pn, Asset = new UAsset(c.path, EngineVersion.VER_UE5_4), Container = PakName(c.cont) });
                log.Warn($"{main.ShortName} exists in both {main.Container} and {PakName(c.cont)} with DIFFERENT content. Both are kept in their own pak; like on PC, the game uses whichever pak loads last. Remove one pak if the wrong version shows.");
            }
        }
    }

    /// <summary>
    /// Blueprints shared by the whole game (e.g. the driver animation templates under /Blueprint/) affect every character.
    /// A mod built for another game version that replaces them can break all racers, so they are left out unless asked for.
    /// </summary>
    void ExcludeSharedBlueprints()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pn in byPackage.Keys)
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(pn, "Extnd[0-9]+_Character[0-9]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) ids.Add(m.Value);
            var cm = System.Text.RegularExpressions.Regex.Match(pn, "/Asset/Character/(?:Skin/)?([^/]+)/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (cm.Success && !cm.Groups[1].Value.Equals("Master_Material", StringComparison.OrdinalIgnoreCase) && !cm.Groups[1].Value.Equals("Common", StringComparison.OrdinalIgnoreCase)) ids.Add(cm.Groups[1].Value);
        }
        var shared = byPackage.Values.Where(a => System.Text.RegularExpressions.Regex.IsMatch(a.PackageName, "^/Game/[^/]+/Blueprint/", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                                             && !ids.Any(id => a.PackageName.Contains(id, StringComparison.OrdinalIgnoreCase))).ToList();
        if (shared.Count == 0) return;
        if (o.IncludeSharedBlueprints)
        {
            log.Warn($"This mod replaces {shared.Count} blueprint(s) shared by the whole game ({string.Join(", ", shared.Select(x => x.ShortName))}). Included as requested: if they were made for a different game version, other characters can break.");
            return;
        }
        foreach (var a in shared) a.Removed = true;
        log.Warn($"Left out {shared.Count} blueprint(s) that replace parts of the game used by EVERY character ({string.Join(", ", shared.Select(x => x.ShortName))}). " +
                 "Made for a different game version, they can break animations or placement for all racers. This character's own animations may not fully work without them; " +
                 "tick \"Include shared game blueprints\" to try them anyway (remove the pak again if other characters break).");
    }

    // ===================================================================== helpers
    /// <summary>Path inside the pak that the Switch version expects for a package, e.g. "UNION/Content/01_Union/X.uasset".</summary>
    static string CanonicalRel(string packageName, string ext)
    {
        if (packageName.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) return "UNION/Content/" + packageName[6..] + ext;
        if (packageName.StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase)) return "Engine/Content/" + packageName[8..] + ext;
        var rest = packageName.TrimStart('/'); int i = rest.IndexOf('/');
        return i < 0 ? rest + ext : "UNION/Plugins/" + rest[..i] + "/Content/" + rest[(i + 1)..] + ext;
    }

    static void CopyOut(string src, string dir)
    {
        var dst = Path.Combine(dir, Path.GetFileName(src));
        if (File.Exists(dst)) File.SetAttributes(dst, FileAttributes.Normal);
        File.Copy(src, dst, true); File.SetAttributes(dst, FileAttributes.Normal);
    }
    /// <summary>"_999_P" gives the pak a high patch priority so it overrides the game's own (update) paks.</summary>
    static string PakName(string stem)
    {
        var b = stem.EndsWith("_P", StringComparison.OrdinalIgnoreCase) ? stem[..^2] : stem;
        b = System.Text.RegularExpressions.Regex.Replace(b, "_[0-9]+$", "");
        return b + "_999_P";
    }

    static string PackageNameFromRel(string rel)
    {
        var noExt = rel.Substring(0, rel.LastIndexOf('.')); var parts = noExt.Split('/');
        int ci = Array.FindIndex(parts, s => s.Equals("Content", StringComparison.OrdinalIgnoreCase));
        if (ci == 0) return "/Game/" + string.Join("/", parts.Skip(1));   // some mods store "Content/..." without the project folder
        if (ci < 1) return "/" + noExt;
        string root = parts[0].Equals("Engine", StringComparison.OrdinalIgnoreCase) && ci == 1 ? "/Engine" : ci == 1 ? "/Game" : "/" + parts[ci - 1];
        return root + "/" + string.Join("/", parts.Skip(ci + 1));
    }

    string RunRetoc(string args, string cwd)
    {
        var psi = new ProcessStartInfo(o.RetocPath, args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.Environment["RUST_BACKTRACE"] = "0";
        using var p = Process.Start(psi);
        var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        var all = so.Result + "\n" + se.Result;
        if (p.ExitCode != 0)
        {
            var msg = all.Split('\n').FirstOrDefault(l => l.StartsWith("Error")) ?? all.Trim().Split('\n').LastOrDefault();
            throw new Exception("Container conversion failed: " + msg);
        }
        return all;
    }

    readonly Dictionary<string, string> pkgByFile = new(StringComparer.OrdinalIgnoreCase); // converted file (relative) -> real package name

    List<string> Manifest(string utoc, string work)
    {
        var dir = Path.Combine(work, "manifest"); Directory.CreateDirectory(dir);
        RunRetoc($"manifest \"{utoc}\"", dir);
        var list = new List<string>();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "pakstore.json")));
        foreach (var e in doc.RootElement.GetProperty("oplog").GetProperty("entries").EnumerateArray())
        {
            var pn = e.GetProperty("packagestoreentry").GetProperty("packagename").GetString(); list.Add(pn);
            if (e.TryGetProperty("packagedata", out var pd))
                foreach (var f in pd.EnumerateArray())
                {
                    var fn = f.GetProperty("filename").GetString().Replace('\\', '/');
                    while (fn.StartsWith("../")) fn = fn[3..];
                    pkgByFile[fn] = pn;
                }
        }
        return list;
    }

    List<string> LegacyPakFiles(string pak, string work, out string extractDir)
    {
        extractDir = Path.Combine(work, "legacy_" + Path.GetFileNameWithoutExtension(pak));
        try
        {
            var tmp = Path.Combine(work, "pakread_" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(tmp);
            File.Copy(pak, Path.Combine(tmp, Path.GetFileName(pak)));
            var prov = new CUE4Parse.FileProvider.DefaultFileProvider(tmp, SearchOption.TopDirectoryOnly, new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_UE5_4));
            prov.Initialize(); prov.Mount();
            var list = new List<string>();
            foreach (var f in prov.Files.Values)
            {
                var data = f.Read(); var dst = Path.Combine(extractDir, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)); File.WriteAllBytes(dst, data); list.Add(f.Path);
            }
            return list;
        }
        catch { return null; }
    }

    // ===================================================================== references
    class Unresolved { public ModAsset A; public Import Pkg, Obj; public ulong PkgId, Hash; public string Usage, KnownName; }

    void ResolveReferences()
    {
        var items = new List<Unresolved>();
        foreach (var a in byPackage.Values.Where(a => !a.Removed && a.MainClass != "Material")) // custom masters get dropped anyway
        {
            var usage = ReferenceUsage(a.Asset);
            for (int i = 0; i < a.Asset.Imports.Count; i++)
            {
                var imp = a.Asset.Imports[i]; var on = Str(imp.ObjectName);
                if (!on.StartsWith("UnresolvedExport_")) continue;
                var pk = Outer(a.Asset, imp); var pn = Str(pk?.ObjectName);
                if (pk == null || !pn.StartsWith("/")) continue;
                // the package name is either known (stored in the mod) or only its id is
                bool known = !pn.StartsWith("/Engine/Unresolved_");
                items.Add(new Unresolved
                {
                    A = a, Pkg = pk, Obj = imp, KnownName = known ? pn : null,
                    PkgId = known ? CityHash.PackageId(pn) : Convert.ToUInt64(pn["/Engine/Unresolved_".Length..], 16), Hash = Convert.ToUInt64(on["UnresolvedExport_".Length..], 16),
                    Usage = usage.TryGetValue(-(i + 1), out var us) ? us.First() : (a.MainClass == "SkeletalMesh" ? "SkeletalMaterials" : null)
                });
            }
            if (a.Asset.Imports.Any(im => Str(im.ObjectName) == "/Engine/UnknownPackage"))
                log.Warn($"{a.ShortName}: has a reference that could not be identified at all; it will load as empty.");
        }
        if (items.Count == 0) { log.Info("All references to game assets were resolved."); return; }
        int knownCount = items.Count(x => x.KnownName != null);
        log.Info($"{items.Count} reference(s) to game/engine assets to link{(knownCount > 0 ? $" ({knownCount} with names stored in the mod)" : "")} ...");

        var names = new Dictionary<ulong, string>();
        foreach (var it in items.Where(x => x.KnownName != null)) names[it.PkgId] = it.KnownName;
        if (db != null) foreach (var id in items.Select(x => x.PkgId).Distinct()) if (db.Packages.TryGetValue($"{id:x16}", out var n)) names[id] = n;
        var missing = items.Select(x => x.PkgId).Distinct().Where(id => !names.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var slots = MeshSlotNames();
            var g = new Guesser(byPackage.Keys, slots);
            log.Info($"Guessing {missing.Count} name(s) from game naming patterns ({g.CandidateCount / 1_000_000.0:0.#} million candidates) ...");
            foreach (var kv in g.Resolve(missing)) names[kv.Key] = kv.Value;
        }

        var linked = new Dictionary<(string, string, string), List<string>>();
        foreach (var it in items)
        {
            if (!names.TryGetValue(it.PkgId, out var pn))
            {
                log.Warn($"{it.A.ShortName}: could not identify a referenced game asset ({Describe(it.Usage)}). It will load as empty{(it.Usage?.EndsWith("Parent") == true ? "" : "")}. A name database from your Switch dump fixes this.");
                continue;
            }
            var shortName = pn[(pn.LastIndexOf('/') + 1)..];
            string objName = null, clsPkg = null, cls = null;
            if (db != null && db.Exports.TryGetValue(NameDb.Key(it.PkgId, it.Hash), out var ex)) { objName = ex.Name; clsPkg = ex.ClassPackage; cls = ex.ClassName; }
            if (objName == null && byPackage.TryGetValue(pn, out var own) && !own.Removed)
            {   // the package is part of this mod: compare against every object really inside it
                var ou = own.Asset;
                string PathOf(Export e)
                {
                    var parts = new List<string>(); var cur = e; int guard = 0;
                    while (cur != null && guard++ < 16) { parts.Insert(0, Str(cur.ObjectName)); cur = cur.OuterIndex.IsExport() ? cur.OuterIndex.ToExport(ou) : null; }
                    return string.Join("/", parts);
                }
                foreach (var e in ou.Exports)
                {
                    var path = PathOf(e);
                    if (CityHash.ExportHash(path) != it.Hash && CityHash.ExportHash(path.Replace('/', '.')) != it.Hash && CityHash.ExportHash(path.Replace('/', ':')) != it.Hash) continue;
                    if (e.OuterIndex.Index != 0) { log.Warn($"{it.A.ShortName}: refers to {path} inside {own.ShortName}, a sub-object that can't be linked here; left empty."); objName = ""; break; }
                    var ct = e.ClassIndex.IsImport() ? e.ClassIndex.ToImport(ou) : null;
                    objName = Str(e.ObjectName);
                    if (ct != null) { clsPkg = Str(Outer(ou, ct)?.ObjectName); cls = Str(ct.ObjectName); }
                    else if (e.ClassIndex.IsExport()) { clsPkg = pn; cls = Str(e.ClassIndex.ToExport(ou).ObjectName); }
                    break;
                }
                if (objName == "") continue;
            }
            if (objName == null)
                foreach (var cand in new[] { shortName, shortName + "_C", "Default__" + shortName + "_C" })
                    if (CityHash.ExportHash(cand) == it.Hash)
                    {
                        objName = cand;
                        if (cand.StartsWith("Default__")) { clsPkg = pn; cls = shortName + "_C"; }
                        else (clsPkg, cls) = Guesser.GuessClass(cand, it.Usage);
                        break;
                    }
            if (objName == null || cls == null || objName.Contains('.'))
            {
                log.Warn($"{it.A.ShortName}: found {pn} but could not identify the object inside it; left empty.");
                continue;
            }
            it.Pkg.ObjectName = N(it.A.Asset, pn);
            it.Obj.ObjectName = N(it.A.Asset, objName); it.Obj.ClassPackage = N(it.A.Asset, clsPkg); it.Obj.ClassName = N(it.A.Asset, cls);
            it.A.Dirty = true;
            var k = (Describe(it.Usage), pn, cls);
            (linked.TryGetValue(k, out var lst) ? lst : linked[k] = new()).Add(it.A.ShortName);
        }
        foreach (var ((what, pn2, cls2), who) in linked)
            log.Change(who.Count == 1 ? $"{who[0]}: linked {what} to {pn2} ({cls2})"
                                      : $"{who.Count} assets ({string.Join(", ", who.Take(3))}{(who.Count > 3 ? ", ..." : "")}): linked {what} to {pn2} ({cls2})");
    }

    static string Describe(string usage) => usage switch
    {
        null => "a reference",
        var u when u.EndsWith("Parent") => "parent material",
        var u when u.EndsWith("PhysicsAsset") => "physics asset",
        var u when u.StartsWith("SkeletalMaterials") => "a material slot",
        var u when u.Contains("TextureParameterValues") => "a texture parameter",
        var u when u.EndsWith("BoneCompressionSettings") => "animation compression settings",
        var u when u.EndsWith("CurveCompressionSettings") => "animation curve compression settings",
        var u when u.EndsWith("VariableFrameStrippingSettings") => "animation frame-stripping settings",
        var u when u.EndsWith("Skeleton") => "skeleton",
        var u => u
    };

    List<string> MeshSlotNames()
    {
        var list = new List<string>();
        foreach (var a in byPackage.Values.Where(a => a.MainClass == "SkeletalMesh"))
            foreach (var n in a.Asset.GetNameMapIndexList().Select(x => x.Value)) if (n.StartsWith("M_") || n.StartsWith("MI_") || n.StartsWith("Mat")) list.Add(n);
        return list;
    }

    // ===================================================================== materials
    readonly List<ModAsset> makeInvisible = new();
    readonly List<ModAsset> rebuildFromTextures = new();
    List<(string ns, string key, string value)> modTexts = new();

    /// <summary>Text replacements in the JSON format PC text loaders use: [{"Language","Namespace","Key","Value"}].</summary>
    List<(string ns, string key, string value)> ReadJsonTexts(IEnumerable<string> files)
    {
        var res = new List<(string, string, string)>();
        foreach (var f in files)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    string G(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    var lang = G("Language"); var ns = G("Namespace"); var key = G("Key"); var val = G("Value");
                    if (ns == null || key == null || val == null) continue;
                    if (lang != null && !lang.Equals("en", StringComparison.OrdinalIgnoreCase)) continue;
                    res.Add((ns, key, val));
                }
            }
            catch (Exception ex) { log.Warn($"{Path.GetFileName(f)}: text file not read ({ex.Message})."); }
        }
        return res;
    }
    readonly List<(ModAsset a, string target, string bc, string n, string mro, string why)> rebuildWithTextures = new();

    /// <summary>Packages of all textures a material instance sets, whatever the parameter names.</summary>
    List<string> TextureParamValues(ModAsset a)
    {
        var res = new List<string>(); var u = a.Asset;
        if (Prop(a.MainExport, "TextureParameterValues") is not ArrayPropertyData arr) return res;
        foreach (var st in arr.Value.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>())
        {
            var ti = Imp(u, (st.Value.FirstOrDefault(p => Str(p.Name) == "ParameterValue") as ObjectPropertyData)?.Value);
            var pk = ti == null ? null : ImportPackage(u, ti);
            if (pk != null && !pk.StartsWith("/Engine/")) res.Add(pk);
        }
        return res;
    }

    /// <summary>The material's main color setting (own value, else the custom master's default).</summary>
    (float, float, float)? PlainColor(ModAsset a, ModAsset master)
    {
        string[] names = { "Color", "BaseColor", "Base Color", "Albedo", "Tint", "DiffuseColor", "MainColor", "Color1", "Color2", "Color3" };
        static bool Lit((float r, float g, float b) v) => v.r + v.g + v.b > 0.05f;
        if (Prop(a.MainExport, "VectorParameterValues") is ArrayPropertyData arr)
            foreach (var st in arr.Value.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>())
            {
                var info = st.Value.FirstOrDefault(p => Str(p.Name) == "ParameterInfo") as UAssetAPI.PropertyTypes.Structs.StructPropertyData;
                var n = (info?.Value.FirstOrDefault(p => Str(p.Name) == "Name") as NamePropertyData)?.Value?.ToString();
                if (n == null || !names.Contains(n, StringComparer.OrdinalIgnoreCase)) continue;
                if (st.Value.FirstOrDefault(p => Str(p.Name) == "ParameterValue") is UAssetAPI.PropertyTypes.Structs.LinearColorPropertyData lc
                    && Lit((lc.Value.R, lc.Value.G, lc.Value.B)))
                    return (lc.Value.R, lc.Value.G, lc.Value.B);
            }
        try
        {
            matInfo ??= new MaterialInfo(legacyRootForChecks);
            var d = matInfo.Get(master.RelPath);
            if (d != null) foreach (var n in names) if (d.Vectors.TryGetValue(n, out var v) && Lit(v)) return v;
        }
        catch { }
        return null;
    }

    static readonly System.Text.RegularExpressions.Regex RxMask = new("(^|_)(a|alpha|mask|msk|opacity|opa|op|cutout|clip)([0-9_]*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Masked custom materials that take their shape from a black/white mask texture (clouds, leaves, flames on flat
    /// cards) and their color from a setting: the game's material would draw the mask itself on solid cards, so build a
    /// cut-out copy of the mask in the material's color. Returns the new texture, or null if this isn't such a material.
    /// </summary>
    string MaskCutout(ModAsset mat, ModAsset master, string bc)
    {
        if (bc == null || !RxMask.IsMatch(Short(bc)) || !byPackage.TryGetValue(bc, out var mask)) return null;
        var names = master.Asset.GetNameMapIndexList();
        if (!names.Any(n => n.Value is "BLEND_Masked" or "BLEND_Translucent")) return null;
        var fmt = TextureTools.PixelFormat(mask.Asset); if (fmt != "PF_DXT1" && fmt != "PF_DXT5") return null;
        if (!TextureTools.IsMaskTexture(mask.Path)) return null;
        var col = PlainColor(mat, master) ?? (1f, 1f, 1f);
        int cols = 1, rows = 1;
        try
        {
            matInfo ??= new MaterialInfo(legacyRootForChecks); var d = matInfo.Get(master.RelPath);
            if (d != null)
            {
                if (d.Scalars.TryGetValue("Columns", out var c) && c >= 1 && c <= 16) cols = (int)c;
                if (d.Scalars.TryGetValue("Rows", out var r) && r >= 1 && r <= 16) rows = (int)r;
            }
        }
        catch { }
        try
        {
            static byte S(float v) => (byte)Math.Clamp((int)Math.Round(255 * Math.Pow(Math.Clamp(v, 0, 1), 1 / 2.2)), 0, 255);
            var newShort = mask.ShortName + "_SwitchCutout"; var newPkg = mask.PackageName + "_SwitchCutout";
            var path = Path.Combine(Path.GetDirectoryName(mask.Path), newShort + ".uasset");
            foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk" })
            { var src = Path.ChangeExtension(mask.Path, ext); if (File.Exists(src)) File.Copy(src, Path.ChangeExtension(path, ext), true); }
            RenamePackage(path, mask.PackageName, mask.ShortName, newPkg, newShort);
            var u = new UAsset(path, EngineVersion.VER_UE5_4); u.FolderName = new FString(newPkg); u.Write(path);
            if (!TextureTools.MakeCutout(path, S(col.Item1), S(col.Item2), S(col.Item3), cols, rows)) return null;
            byPackage[newPkg] = new ModAsset { Path = path, RelPath = mask.RelPath[..(mask.RelPath.LastIndexOf('/') + 1)] + newShort + ".uasset", PackageName = newPkg, Asset = new UAsset(path, EngineVersion.VER_UE5_4), Container = mat.Container };
            log.Info($"{mat.ShortName}: its shape comes from the mask {mask.ShortName}{(cols * rows > 1 ? $" ({cols}x{rows} animation frames, first frame used)" : "")}; made a cut-out texture in its color.");
            return newPkg;
        }
        catch (Exception ex) { log.Warn($"{mat.ShortName}: couldn't make a cut-out texture from {mask.ShortName} ({ex.Message})."); return null; }
    }

    string SolidTexture(ModAsset near, (float r, float g, float b) col)
    {
        try
        {
            static byte S(float v) => (byte)Math.Clamp((int)Math.Round(255 * Math.Pow(Math.Clamp(v, 0, 1), 1 / 2.2)), 0, 255);
            var dir = near.PackageName[..near.PackageName.LastIndexOf('/')];
            var pkg = dir + "/T_SwitchPorter_" + near.ShortName + "_Color";
            var path = Path.Combine(Path.GetDirectoryName(near.Path), "T_SwitchPorter_" + near.ShortName + "_Color.uasset");
            ExtractTemplate("T_SwitchPorter_Clear", path, TplClear, pkg);
            if (!TextureTools.MakeSolid(path, S(col.r), S(col.g), S(col.b))) return null;
            byPackage[pkg] = new ModAsset { Path = path, RelPath = near.RelPath[..(near.RelPath.LastIndexOf('/') + 1)] + Path.GetFileName(path), PackageName = pkg, Asset = new UAsset(path, EngineVersion.VER_UE5_4), Container = near.Container };
            return pkg;
        }
        catch { return null; }
    }

    /// <summary>Textures a material references: typed texture imports, plus packages that are textures in this mod (links the converter couldn't type).</summary>
    List<string> TexturesUsedBy(ModAsset m)
    {
        var u = m.Asset;
        return u.Imports.Where(i => Str(i.ClassName) == "Texture2D" || (i.OuterIndex.Index == 0 && byPackage.TryGetValue(Str(i.ObjectName), out var t) && t.MainClass == "Texture2D"))
                        .Select(i => i.OuterIndex.Index == 0 ? Str(i.ObjectName) : ImportPackage(u, i))
                        .Where(x => x != null && !x.StartsWith("/Engine/")).Distinct().ToList();
    }

    bool TexHasAlpha(string texPkg) => byPackage.TryGetValue(texPkg, out var ta) && AlphaFormats.Contains(TextureTools.PixelFormat(ta.Asset));

    /// <summary>True when this texture's alpha really cuts holes in the parts drawn with the material.</summary>
    bool AlphaCuts(string materialName, string texPkg)
    {
        if (!byPackage.TryGetValue(texPkg, out var tex)) return false;
        try { alphaCheck ??= new AlphaCheck(legacyRootForChecks); } catch { return false; }
        var f = alphaCheck.TransparentFraction(materialName, tex.RelPath);
        if (f == null) return false;
        bool cuts = f.Value > 0.02;
        log.Info($"{materialName}: {f.Value:P1} of its model samples transparent alpha in {tex.ShortName} -> {(cuts ? "real cut-outs, using the transparency material" : "alpha is only padding, using the normal material")}");
        return cuts;
    }
    MaterialInfo matInfo;

    /// <summary>The mod's own master material at the root of an instance chain, or null.</summary>
    ModAsset RootMaster(ModAsset a)
    {
        for (int guard = 0; a != null && guard < 10; guard++)
        {
            if (a.MainClass == "Material") return a;
            var e = a.MainExport; if (e == null) return null;
            var pImp = Imp(a.Asset, (Prop(e, "Parent") as ObjectPropertyData)?.Value); if (pImp == null) return null;
            a = byPackage.TryGetValue(ImportPackage(a.Asset, pImp) ?? "", out var p) ? p : null;
        }
        return null;
    }

    /// <summary>Scalar and switch values that apply to a material instance: its own settings, then its parents', then the master's defaults.</summary>
    Dictionary<string, float> EffectiveParams(ModAsset a)
    {
        var res = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        for (int guard = 0; a != null && guard < 10; guard++)
        {
            if (a.MainClass == "Material")
            {
                try { matInfo ??= new MaterialInfo(legacyRootForChecks); } catch { break; }
                var d = matInfo.Get(a.RelPath);
                if (d != null)
                {
                    foreach (var kv in d.Scalars) res.TryAdd(kv.Key, kv.Value);
                    foreach (var kv in d.Switches) res.TryAdd(kv.Key, kv.Value ? 1 : 0);
                }
                break;
            }
            var e = a.MainExport; if (e == null) break;
            if (Prop(e, "ScalarParameterValues") is ArrayPropertyData sc)
                foreach (var st in sc.Value.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>())
                {
                    var info = st.Value.FirstOrDefault(p => Str(p.Name) == "ParameterInfo") as UAssetAPI.PropertyTypes.Structs.StructPropertyData;
                    var n = (info?.Value.FirstOrDefault(p => Str(p.Name) == "Name") as NamePropertyData)?.Value?.ToString();
                    if (n != null && st.Value.FirstOrDefault(p => Str(p.Name) == "ParameterValue") is FloatPropertyData f) res.TryAdd(n, f.Value);
                }
            foreach (var spName in new[] { "StaticParametersRuntime", "StaticParameters" })
                if (Prop(e, spName) is UAssetAPI.PropertyTypes.Structs.StructPropertyData sp
                    && sp.Value.FirstOrDefault(p => Str(p.Name) == "StaticSwitchParameters") is ArrayPropertyData sw)
                    foreach (var st in sw.Value.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>())
                    {
                        var info = st.Value.FirstOrDefault(p => Str(p.Name) == "ParameterInfo") as UAssetAPI.PropertyTypes.Structs.StructPropertyData;
                        var n = (info?.Value.FirstOrDefault(p => Str(p.Name) == "Name") as NamePropertyData)?.Value?.ToString();
                        if (n != null && st.Value.FirstOrDefault(p => Str(p.Name) == "Value") is BoolPropertyData b) res.TryAdd(n, b.Value ? 1 : 0);
                    }
            var pImp = Imp(a.Asset, (Prop(e, "Parent") as ObjectPropertyData)?.Value); if (pImp == null) break;
            a = byPackage.TryGetValue(ImportPackage(a.Asset, pImp) ?? "", out var p) ? p : null;
        }
        return res;
    }

    static readonly System.Text.RegularExpressions.Regex RxTransform = new(@"ssj|ssgss|(^|[^a-z])(transform|transformed|transformation|shielded)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static List<string> Tokens(string s) => System.Text.RegularExpressions.Regex.Matches(s ?? "", "[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+").Select(m => m.Value.ToLowerInvariant()).ToList();

    /// <summary>
    /// Why a part drawn with a custom material should be hidden on Switch, or null. Custom PC materials can hide parts
    /// with switches (an accessory toggled off), draw cartoon outlines, or show parts only while transformed; the game's
    /// own material would draw all of those as solid, overlapping geometry.
    /// </summary>
    string HideReason(ModAsset mi, ModAsset master, Dictionary<string, float> eff)
    {
        string names = mi.ShortName + " " + master.ShortName;
        if (names.Contains("outline", StringComparison.OrdinalIgnoreCase))
            return "cartoon outline (a slightly bigger copy of the model drawn inside-out); the game's materials would draw it as a solid gray shell over the character, so it is hidden";
        if (eff.Count == 0 && mi.MainExport != null) eff = EffectiveParams(mi);
        if (eff.Count == 0) { try { matInfo ??= new MaterialInfo(legacyRootForChecks); var d = matInfo.Get(master.RelPath); if (d != null) { foreach (var kv in d.Scalars) eff.TryAdd(kv.Key, kv.Value); foreach (var kv in d.Switches) eff.TryAdd(kv.Key, kv.Value ? 1 : 0); } } catch { } }
        var own = Tokens(mi.ShortName).Concat(Tokens(master.ShortName)).ToHashSet();
        foreach (var (n, v) in eff)
        {
            bool off = Math.Abs(v) < 0.001f;
            if (n.Length > 2 && n.EndsWith("On") && char.IsLower(n[^3]) is var _ && off)
            {
                var t = Tokens(n[..^2]);
                if (t.Count > 0 && t.All(own.Contains)) return $"switched off by its setting '{n}' (an optional part, e.g. an accessory not selected); hidden";
            }
            if (n.ToLowerInvariant() is "visible" or "visibility" or "show" or "enabled" or "enable" && off) return $"switched off by its setting '{n}'; hidden";
            if (n.ToLowerInvariant() is "hide" or "hidden" && !off) return $"hidden by its setting '{n}'";
        }
        if (RxTransform.IsMatch(names) && eff.Keys.Any(k => k.Contains("Shield", StringComparison.OrdinalIgnoreCase)))
            return "only shown while transformed (shield/invincibility). The game's materials can't swap parts, so it is hidden to avoid overlapping models";
        return null;
    }
    readonly Dictionary<ModAsset, string> invisibleWhy = new();
    readonly List<ModAsset> convertMasters = new();
    readonly List<ModAsset> rebuildParentless = new();

    /// <summary>Texture package referenced by a texture parameter of a material instance, or null.</summary>
    string ParamTexture(ModAsset a, string param)
    {
        var u = a.Asset; if (Prop(a.MainExport, "TextureParameterValues") is not ArrayPropertyData arr) return null;
        foreach (var st in arr.Value.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>())
        {
            var info = st.Value.FirstOrDefault(p => Str(p.Name) == "ParameterInfo") as UAssetAPI.PropertyTypes.Structs.StructPropertyData;
            var n = (info?.Value.FirstOrDefault(p => Str(p.Name) == "Name") as NamePropertyData)?.Value?.ToString();
            if (!string.Equals(n, param, StringComparison.OrdinalIgnoreCase)) continue;
            var ti = Imp(u, (st.Value.FirstOrDefault(p => Str(p.Name) == "ParameterValue") as ObjectPropertyData)?.Value);
            var pk = ti == null ? null : ImportPackage(u, ti);
            return pk == null || pk.StartsWith("/Engine/Unresolved_") ? null : pk;
        }
        return null;
    }

    void RebuildParentless()
    {
        foreach (var a in rebuildFromTextures)
        {
            try
            {
                var u = a.Asset;
                var texs = u.Imports.Where(i => Str(i.ClassName) == "Texture2D").Select(i => ImportPackage(u, i)).Where(p => p != null).Distinct().ToList();
                var (bc, nrm, mro) = ClassifyTextures(texs);
                MakeInstance(a, CharaBasic, new() { ["Bc"] = bc, ["N"] = nrm, ["MRO"] = mro });
                reparented.Add(a.ShortName);
                log.Change($"{a.ShortName}: material instance of a custom PC material (stored in a compact format whose settings can't be read) -> rebuilt as an instance of M_Chara_Basic from its textures (Bc: {Short(bc) ?? "default"}, N: {Short(nrm) ?? "default"}, MRO: {Short(mro) ?? "default"})");
                if (bc == null) log.Warn($"{a.ShortName}: no color texture recognised among {string.Join(", ", texs.Select(Short))}; it will look plain.");
            }
            catch (Exception ex) { log.Warn($"{a.ShortName}: could not rebuild ({ex.Message}); it may render with the wrong textures."); }
        }
        foreach (var a in rebuildParentless)
        {
            try
            {
                var tex = new Dictionary<string, string> { ["Bc"] = ParamTexture(a, "Bc"), ["N"] = ParamTexture(a, "N"), ["MRO"] = ParamTexture(a, "MRO") };
                MakeInstance(a, CharaBasic, tex);
                reparented.Add(a.ShortName);
                log.Change($"{a.ShortName}: had no usable parent material -> rebuilt as an instance of M_Chara_Basic (Bc: {Short(tex["Bc"]) ?? "default"}, N: {Short(tex["N"]) ?? "default"}, MRO: {Short(tex["MRO"]) ?? "default"})");
            }
            catch (Exception ex) { log.Warn($"{a.ShortName}: could not rebuild ({ex.Message}); it may render with a default material."); }
        }
    }

    /// <summary>Value of a scalar material parameter whose name contains all the given words, or null.</summary>
    static float? ScalarParam(ModAsset a, params string[] words)
    {
        if (Prop(a.MainExport, "ScalarParameterValues") is not ArrayPropertyData arr) return null;
        foreach (var st in arr.Value.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>())
        {
            var info = st.Value.FirstOrDefault(p => Str(p.Name) == "ParameterInfo") as UAssetAPI.PropertyTypes.Structs.StructPropertyData;
            var n = (info?.Value.FirstOrDefault(p => Str(p.Name) == "Name") as NamePropertyData)?.Value?.ToString() ?? "";
            if (!words.All(w => n.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            if (st.Value.FirstOrDefault(p => Str(p.Name) == "ParameterValue") is FloatPropertyData f) return f.Value;
        }
        return null;
    }
    static bool IsSeeThrough(ModAsset master) => master.Asset.GetNameMapIndexList().Any(n => n.Value is "BLEND_Translucent" or "BLEND_Additive" or "BLEND_Modulate");

    void FixMaterials()
    {
        var customMasters = byPackage.Values.Where(a => a.MainClass == "Material").ToList();
        foreach (var m in customMasters) log.Info($"{m.ShortName}: custom master material (PC-only shaders) - parts using it will be moved to the game's materials.");

        // read the instance chains before anything is changed (fixing a parent rewrites its settings)
        var mics = byPackage.Values.Where(a => a.MainClass == "MaterialInstanceConstant").ToList();
        var rootOf = mics.ToDictionary(a => a, RootMaster);
        var effOf = mics.ToDictionary(a => a, a => a.MainExport == null ? new Dictionary<string, float>() : EffectiveParams(a));
        foreach (var a in mics)
        {
            var u = a.Asset; var e = a.MainExport;
            if (e == null)
            {   // properties can't be read (unversioned asset): rebuild from the textures it references if its parent is custom
                var parents = u.Imports.Where(i => Str(i.ClassName) is "MaterialInstanceConstant" or "Material" && !Str(i.ObjectName).StartsWith("Default__"))
                                       .Select(i => ImportPackage(u, i)).Where(p => p != null && p != a.PackageName).ToList();
                var cm = parents.Select(p => byPackage.TryGetValue(p, out var x) ? x : null).FirstOrDefault(x => x != null && x.MainClass == "Material");
                if (cm != null || parents.Any(p => p.StartsWith("/Engine/Unresolved_")))
                {
                    if (cm != null && IsSeeThrough(cm)) { makeInvisible.Add(a); invisibleWhy[a] = "see-through (glass) material can't be reproduced on Switch; made invisible instead of solid"; }
                    else if (cm != null && HideReason(a, cm, new()) is string hr) { makeInvisible.Add(a); invisibleWhy[a] = hr; }
                    else rebuildFromTextures.Add(a);
                }
                continue;
            }
            var parentProp = Prop(e, "Parent") as ObjectPropertyData;
            var pImp = Imp(u, parentProp?.Value); var pPkg = pImp == null ? null : ImportPackage(u, pImp);
            bool reparent = false; string why = null;
            if (pImp == null)
            {
                bool used = byPackage.Values.Any(x => x != a && x.Asset.Imports.Any(i => Str(i.ObjectName).Equals(a.PackageName, StringComparison.OrdinalIgnoreCase)));
                if (!used) { log.Info($"{a.ShortName}: material instance without a parent that nothing in the mod uses (leftover); left unchanged"); continue; }
                rebuildParentless.Add(a); continue;   // rebuilt from the built-in template after this pass
            }
            else if (pPkg.StartsWith("/Engine/Unresolved_") || pPkg == "/Engine/UnknownPackage") { reparent = true; why = "parent material is missing (probably a custom PC material)"; }
            else if (byPackage.TryGetValue(pPkg, out var pa) && pa.MainClass == "Material")
            {
                reparent = true; why = $"used custom material {pa.ShortName}";
                if (IsSeeThrough(pa)) { makeInvisible.Add(a); invisibleWhy[a] = "see-through (glass) material can't be reproduced on Switch; made invisible instead of solid"; }
                else if (ScalarParam(a, "Hide", "Shield") is float hs && hs < 0)
                {
                    makeInvisible.Add(a);
                    invisibleWhy[a] = "only appears while shielded (a transformation/alternate model). The game's materials can't swap parts on shield, so it is hidden permanently to avoid overlapping models";
                }
                else if (HideReason(a, pa, effOf[a]) is string hr) { makeInvisible.Add(a); invisibleWhy[a] = hr; }
            }
            else if (byPackage.TryGetValue(pPkg, out var pmi) && pmi.MainClass == "MaterialInstanceConstant" && rootOf[a] is ModAsset rm
                     && HideReason(a, rm, effOf[a]) is string hr2)
            { makeInvisible.Add(a); invisibleWhy[a] = hr2; }
            bool perm = (Prop(e, "bHasStaticPermutationResource") as BoolPropertyData)?.Value == true;
            if (!reparent && !perm) continue;

            // strip static permutation (PC-compiled shaders inside the instance)
            int keep = MaterialInstanceCachedDataLength(u, e.Extras);
            if (keep < 0) { log.Error($"{a.ShortName}: material data layout not recognised; this material was not fixed and may not render on Switch."); continue; }
            e.Data.RemoveAll(p => Str(p.Name) is "bHasStaticPermutationResource" or "StaticParametersRuntime" or "StaticParameters" or "BasePropertyOverrides");
            int before = e.Extras.Length; e.Extras = e.Extras.Take(keep).ToArray();

            if (reparent && !makeInvisible.Contains(a) && ParamTexture(a, "Bc") == null
                && byPackage.TryGetValue(pPkg ?? "", out var cmaster) && cmaster.MainClass == "Material")
            {   // its color texture comes from the custom master (or uses another parameter name): rebuild with the right textures
                var own = TextureParamValues(a);
                var mtex = TexturesUsedBy(cmaster);
                var (bc, nrm, mro) = ClassifyTextures(own.Concat(mtex).Distinct().ToList());
                if (bc == null && PlainColor(a, cmaster) is (float, float, float) col)
                {   // no texture at all (e.g. a glowing halo with just a color): make a small texture of that color
                    bc = SolidTexture(a, col);
                    if (bc != null) log.Info($"{a.ShortName}: has no texture, only a color; using a solid-color texture.");
                }
                if (MaskCutout(a, cmaster, bc) is string cut)
                {
                    rebuildWithTextures.Add((a, CharaAfroHair, cut, null, null, why + " (shape from a mask: cut-out)"));
                    continue;
                }
                if (bc != null)
                {
                    bool alpha2 = o.Transparency == TransparencyMode.Always
                        || (o.Transparency == TransparencyMode.Auto && (u.GetNameMapIndexList().Any(n => n.Value is "BLEND_Masked" or "BLEND_Translucent") || cmaster.Asset.GetNameMapIndexList().Any(n => n.Value is "BLEND_Masked" or "BLEND_Translucent"))
                            && TexHasAlpha(bc) && AlphaCuts(a.ShortName, bc));
                    rebuildWithTextures.Add((a, alpha2 ? CharaAfroHair : CharaBasic, bc, nrm, mro, why));
                    continue;
                }
            }
            if (reparent)
            {
                bool alpha = o.Transparency == TransparencyMode.Always
                    || (o.Transparency == TransparencyMode.Auto && u.GetNameMapIndexList().Any(n => n.Value is "BLEND_Masked" or "BLEND_Translucent") && BcHasAlpha(a) && AlphaCutsModel(a));
                var target = alpha ? CharaAfroHair : CharaBasic; var tShort = target[(target.LastIndexOf('/') + 1)..];
                if (pImp != null && pImp.OuterIndex.IsImport() && Outer(u, pImp).OuterIndex.Index == 0)
                {
                    var pk = Outer(u, pImp); pk.ObjectName = N(u, target);
                    pImp.ObjectName = N(u, tShort); pImp.ClassPackage = N(u, "/Script/Engine"); pImp.ClassName = N(u, "Material");
                }
                else { rebuildParentless.Add(a); continue; }   // unusual parent layout: rebuild from the template instead
                reparented.Add(a.ShortName);
                log.Change($"{a.ShortName}: {why} -> now uses the game's {tShort}{(alpha ? " (transparency)" : "")}{(before - keep > 0 ? $"; removed {before - keep:N0} bytes of PC shader data" : "")}");
            }
            else log.Change($"{a.ShortName}: removed PC-only shader permutation ({before - keep:N0} bytes); keeps its game parent material");
            a.Dirty = true;
        }

        foreach (var (a, target, bc, nrm, mro, why) in rebuildWithTextures)
        {
            try
            {
                MakeInstance(a, target, new() { ["Bc"] = bc, ["N"] = nrm, ["MRO"] = mro });
                reparented.Add(a.ShortName);
                log.Change($"{a.ShortName}: {why}; its textures came from that material -> rebuilt as an instance of the game's {Short(target)}{(target == CharaAfroHair ? " (transparency)" : "")} (Bc: {Short(bc)}, N: {Short(nrm) ?? "default"}, MRO: {Short(mro) ?? "default"})");
            }
            catch (Exception ex) { log.Warn($"{a.ShortName}: could not rebuild ({ex.Message}); it may look plain."); }
        }

        // custom masters: rebuilt in place if a mesh (or other non-instance asset) uses them directly, otherwise dropped
        foreach (var m in customMasters)
        {
            var direct = byPackage.Values.Where(x => x != m && x.MainClass != "MaterialInstanceConstant" && x.MainClass != "Material"
                                                && x.Asset.Imports.Any(i => Str(i.ObjectName).Equals(m.PackageName, StringComparison.OrdinalIgnoreCase))).ToList();
            if (direct.Count > 0) { convertMasters.Add(m); continue; }
            m.Removed = true; log.Change($"{m.ShortName}: not included in the Switch pak (custom shaders can't run on Switch)");
        }
    }

    /// <summary>
    /// See-through custom materials (glass, eye refraction layers...) would turn into solid shells on the game's opaque
    /// material. Replace them with a copy of an already-fixed instance that uses M_Chara_AfroHair and a fully transparent texture.
    /// </summary>
    // ---- built-in templates (generic material instance on M_Chara_Basic, fully transparent texture) ----
    const string TplMic = "/Game/SwitchPorter/MI_Template", TplClear = "/Game/SwitchPorter/T_SwitchPorter_Clear";
    static void ExtractTemplate(string res, string targetUasset, string tplPkg, string newPkg)
    {
        var asm = typeof(Converter).Assembly;
        foreach (var ext in new[] { ".uasset", ".uexp" })
        {
            using var st = asm.GetManifestResourceStream(res + ext) ?? throw new Exception("missing built-in template " + res + ext);
            using var fs = File.Create(Path.ChangeExtension(targetUasset, ext)); st.CopyTo(fs);
        }
        foreach (var ext in new[] { ".ubulk", ".uptnl" }) { var f = Path.ChangeExtension(targetUasset, ext); if (File.Exists(f)) File.Delete(f); }
        RenamePackage(targetUasset, tplPkg, tplPkg[(tplPkg.LastIndexOf('/') + 1)..], newPkg, newPkg[(newPkg.LastIndexOf('/') + 1)..]);
        var u = new UAsset(targetUasset, EngineVersion.VER_UE5_4); u.FolderName = new FString(newPkg); u.Write(targetUasset);
    }

    string clearPkg;
    string EnsureClearTexture(ModAsset near)
    {
        if (clearPkg != null) return clearPkg;
        var dir = near.PackageName[..near.PackageName.LastIndexOf('/')];
        clearPkg = dir + "/T_SwitchPorter_Clear";
        var path = Path.Combine(Path.GetDirectoryName(near.Path), "T_SwitchPorter_Clear.uasset");
        ExtractTemplate("T_SwitchPorter_Clear", path, TplClear, clearPkg);
        byPackage[clearPkg] = new ModAsset { Path = path, RelPath = near.RelPath[..(near.RelPath.LastIndexOf('/') + 1)] + "T_SwitchPorter_Clear.uasset",
                                             PackageName = clearPkg, Asset = new UAsset(path, EngineVersion.VER_UE5_4), Container = near.Container };
        return clearPkg;
    }

    /// <summary>Turns an asset into a material instance of a game master with the given textures (param -> texture package).</summary>
    void MakeInstance(ModAsset a, string parentPkg, Dictionary<string, string> textures)
    {
        ExtractTemplate("MI_Template", a.Path, TplMic, a.PackageName);
        var u = new UAsset(a.Path, EngineVersion.VER_UE5_4); var e = u.Exports.OfType<NormalExport>().First();
        var pImp = Imp(u, (Prop(e, "Parent") as ObjectPropertyData).Value);
        Outer(u, pImp).ObjectName = N(u, parentPkg); pImp.ObjectName = N(u, parentPkg[(parentPkg.LastIndexOf('/') + 1)..]);
        foreach (var st in ((ArrayPropertyData)Prop(e, "TextureParameterValues")).Value.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>())
        {
            var info = st.Value.First(p => Str(p.Name) == "ParameterInfo") as UAssetAPI.PropertyTypes.Structs.StructPropertyData;
            var pname = (info.Value.First(p => Str(p.Name) == "Name") as NamePropertyData).Value.ToString();
            if (!textures.TryGetValue(pname, out var tex) || tex == null) continue;
            var ti = Imp(u, (st.Value.First(p => Str(p.Name) == "ParameterValue") as ObjectPropertyData).Value);
            Outer(u, ti).ObjectName = N(u, tex); ti.ObjectName = N(u, tex[(tex.LastIndexOf('/') + 1)..]);
        }
        u.Write(a.Path); a.Asset = u; a.Removed = false; a.Dirty = false;
    }

    /// <summary>
    /// Parts that can't be reproduced on Switch (see-through glass, shield-only parts) become instances of
    /// M_Chara_AfroHair with a fully transparent texture, i.e. invisible.
    /// </summary>
    void MakeSeeThroughInvisible()
    {
        foreach (var a in makeInvisible)
        {
            try
            {
                MakeInstance(a, CharaAfroHair, new() { ["Bc"] = EnsureClearTexture(a) });
                reparented.Add(a.ShortName);
                log.Change($"{a.ShortName}: {(invisibleWhy.TryGetValue(a, out var w) ? w : "made invisible")}");
            }
            catch (Exception ex) { log.Warn($"{a.ShortName}: should be hidden on Switch but couldn't be ({ex.Message}); it will stay visible."); }
        }
    }

    static readonly System.Text.RegularExpressions.Regex RxNormal = new("(^|_)(n|nrm|nm|norm|normal|normalmap)([0-9_]*)$|normal", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex RxMro = new("(^|_)(mro|orm)([0-9_]*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex RxColor = new("(^|_)(alb|albedo|abd|diffuse|dif|diff|di|dm|col|color|colour|basecolor|base|bc|d|c|tex|texture)([0-9_]*)$|albedo|diffuse|basecolor|base_color", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex RxNotColor = new("(^|_)(mask|msk|alpha|opac|opacity|ao|rough|roughness|metal|metallic|spec|spc|specular|prm|pbr|emis|emissive|ems|e|e1|fal|nra|height|disp|sss|rim|matcap|lut|ramp|noise|flow|id|oc|occ|occlusion|li|line|lines|outline|pattern|cel|spm)([0-9_]*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Sorts a material's textures into color / normal / MRO using names and, for the mod's own textures, their compression format.</summary>
    (string bc, string n, string mro) ClassifyTextures(List<string> texs)
    {
        string Fmt(string pk) => byPackage.TryGetValue(pk, out var t) ? TextureTools.PixelFormat(t.Asset) : null;
        bool IsNormal(string pk) { var nm = Short(pk); return Fmt(pk) == "PF_BC5" || RxNormal.IsMatch(nm); }
        var normals = texs.Where(IsNormal).ToList();
        var mros = texs.Except(normals).Where(t => RxMro.IsMatch(Short(t))).ToList();
        var rest = texs.Except(normals).Except(mros).ToList();
        var bc = rest.FirstOrDefault(t => RxColor.IsMatch(Short(t)))
              ?? rest.Where(t => !RxNotColor.IsMatch(Short(t)))
                     .OrderByDescending(t => byPackage.TryGetValue(t, out var a) ? new FileInfo(Path.ChangeExtension(a.Path, ".ubulk")).Exists ? new FileInfo(Path.ChangeExtension(a.Path, ".ubulk")).Length : new FileInfo(Path.ChangeExtension(a.Path, ".uexp")).Length : 0)
                     .FirstOrDefault();
        // normal / MRO maps: prefer the ones named like the color texture (goku_cloth_di -> goku_cloth_N, goku_cloth_MRO)
        int Common(string x) { if (bc == null || x == null) return 0; var p = Short(bc).ToLowerInvariant(); var q = Short(x).ToLowerInvariant(); int i = 0; while (i < p.Length && i < q.Length && p[i] == q[i]) i++; return i; }
        string Best(List<string> l) => l.OrderByDescending(Common).FirstOrDefault();
        return (bc, Best(normals), Best(mros));
    }

    /// <summary>Custom master materials used directly by a mesh: rebuilt in place as instances of the game's materials.</summary>
    void ConvertDirectMasters()
    {
        foreach (var m in convertMasters)
        {
            try
            {
                var u = m.Asset;
                var texs = TexturesUsedBy(m);
                if (IsSeeThrough(m))
                {
                    MakeInstance(m, CharaAfroHair, new() { ["Bc"] = EnsureClearTexture(m) });
                    log.Change($"{m.ShortName}: see-through custom material used directly by a mesh -> made invisible (glass can't be reproduced on Switch)");
                }
                else if (HideReason(m, m, new()) is string hr)
                {
                    MakeInstance(m, CharaAfroHair, new() { ["Bc"] = EnsureClearTexture(m) });
                    log.Change($"{m.ShortName}: {hr}");
                }
                else
                {
                    var (bc, nrm, mro) = ClassifyTextures(texs);
                    var cut = MaskCutout(m, m, bc); var target = cut != null ? CharaAfroHair : CharaBasic;
                    if (cut != null) { bc = cut; nrm = null; mro = null; }
                    MakeInstance(m, target, new() { ["Bc"] = bc, ["N"] = nrm, ["MRO"] = mro });
                    log.Change($"{m.ShortName}: custom material used directly by a mesh -> rebuilt as an instance of {Short(target)}{(cut != null ? " (cut-out)" : "")}" +
                               $" (Bc: {Short(bc) ?? "default"}, N: {Short(nrm) ?? "default"}, MRO: {Short(mro) ?? "default"})");
                    if (bc == null) log.Warn($"{m.ShortName}: no color texture recognised among {string.Join(", ", texs.Select(Short))}; it will look plain.");
                }
                reparented.Add(m.ShortName);
                // whoever references it must now expect a material instance, not a material
                foreach (var other in byPackage.Values.Where(x => x != m && !x.Removed))
                {
                    var ou = other.Asset; bool changed = false;
                    foreach (var imp in ou.Imports.Where(i => Str(i.ObjectName) == m.ShortName && Str(i.ClassName) == "Material"
                                                         && Str(Outer(ou, i)?.ObjectName).Equals(m.PackageName, StringComparison.OrdinalIgnoreCase)))
                    { imp.ClassName = N(ou, "MaterialInstanceConstant"); changed = true; }
                    if (changed) { ou.Write(other.Path); }
                }
            }
            catch (Exception ex) { log.Warn($"{m.ShortName}: could not rebuild ({ex.Message}); parts using it may look wrong."); m.Removed = true; }
        }
    }
    static string Short(string p) => p == null ? null : p[(p.LastIndexOf('/') + 1)..];

    (Import pkg, Import obj) BcTextureImport(ModAsset a)
    {
        var u = a.Asset; var e = a.MainExport;
        if (Prop(e, "TextureParameterValues") is not ArrayPropertyData arr) return (null, null);
        foreach (var s in arr.Value.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>())
        {
            var info = s.Value.FirstOrDefault(p => Str(p.Name) == "ParameterInfo") as UAssetAPI.PropertyTypes.Structs.StructPropertyData;
            var pname = (info?.Value.FirstOrDefault(p => Str(p.Name) == "Name") as NamePropertyData)?.Value?.ToString();
            if (!string.Equals(pname, "Bc", StringComparison.OrdinalIgnoreCase)) continue;
            var ti = Imp(u, (s.Value.FirstOrDefault(p => Str(p.Name) == "ParameterValue") as ObjectPropertyData)?.Value);
            var tp = Outer(u, ti); if (ti == null || tp == null || tp.OuterIndex.Index != 0) return (null, null);
            return (tp, ti);
        }
        return (null, null);
    }

    /// <summary>Renames a copied package: its own path and main object name in the name map.</summary>
    static void RenamePackage(string path, string oldPkg, string oldName, string newPkg, string newName)
    {
        var u = new UAsset(path, EngineVersion.VER_UE5_4); var names = u.GetNameMapIndexList();
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i].Value == oldPkg) u.SetNameReference(i, new FString(newPkg));
            else if (names[i].Value == oldName) u.SetNameReference(i, new FString(newName));
        }
        // Unreal stores names ending in _<number> as "base + number" (4_t01_0 -> "4_t01" #0): references to the
        // renamed object from other packages use that split form, so the object's own name must use it too
        var canon = N(u, newName);
        if (canon.Number != 0)
        {
            foreach (var e in u.Exports) if (e.ObjectName?.Value?.Value == newName && e.ObjectName.Number == 0) e.ObjectName = N(u, newName);
            foreach (var i in u.Imports) if (i.ObjectName?.Value?.Value == newName && i.ObjectName.Number == 0) i.ObjectName = N(u, newName);
        }
        u.Write(path);
    }

    AlphaCheck alphaCheck; string legacyRootForChecks;
    /// <summary>True when the color texture's alpha really cuts holes in the model (not just padding around the paint).</summary>
    bool AlphaCutsModel(ModAsset a)
    {
        var (tp, _) = BcTextureImport(a);
        if (tp == null || !byPackage.TryGetValue(Str(tp.ObjectName), out var tex)) return true;
        try { alphaCheck ??= new AlphaCheck(legacyRootForChecks); } catch { return true; }
        var f = alphaCheck.TransparentFraction(a.ShortName, tex.RelPath);
        if (f == null)
        {   // couldn't measure: the transparency material darkens skin/clothes on Switch, so only hair-like materials get it
            bool hairy = System.Text.RegularExpressions.Regex.IsMatch(a.ShortName, "hair|lash|brow|fur|lace|feather", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            log.Info($"{a.ShortName}: couldn't measure the alpha of {tex.ShortName} -> {(hairy ? "looks like hair, using the transparency material" : "using the normal material (set Transparency to Always if parts should be see-through)")}");
            return hairy;
        }
        bool cuts = f.Value > 0.02;
        log.Info($"{a.ShortName}: {f.Value:P1} of its model samples transparent alpha in {tex.ShortName} -> {(cuts ? "real cut-outs, using the transparency material" : "alpha is only padding, using the normal material")}");
        return cuts;
    }

    bool BcHasAlpha(ModAsset a)
    {
        var u = a.Asset; var e = a.MainExport;
        if (Prop(e, "TextureParameterValues") is not ArrayPropertyData arr) return false;
        foreach (var s in arr.Value.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>())
        {
            var info = s.Value.FirstOrDefault(p => Str(p.Name) == "ParameterInfo") as UAssetAPI.PropertyTypes.Structs.StructPropertyData;
            var pname = (info?.Value.FirstOrDefault(p => Str(p.Name) == "Name") as NamePropertyData)?.Value?.ToString();
            if (!string.Equals(pname, "Bc", StringComparison.OrdinalIgnoreCase)) continue;
            var tex = (s.Value.FirstOrDefault(p => Str(p.Name) == "ParameterValue") as ObjectPropertyData)?.Value;
            var ti = Imp(u, tex); if (ti == null) return false;
            if (byPackage.TryGetValue(ImportPackage(u, ti), out var ta)) return AlphaFormats.Contains(TextureTools.PixelFormat(ta.Asset));
        }
        return false;
    }

    // ===================================================================== morph targets
    void FixMorphs(string legacyRoot)
    {
        var meshes = byPackage.Values.Where(a => !a.Removed && a.MainClass == "SkeletalMesh").ToList();
        if (meshes.Count == 0 || reparented.Count == 0) return;
        var animBps = byPackage.Values.Where(a => !a.Removed && a.Asset.Exports.Any(x => x.GetExportClassType()?.Value?.Value == "AnimBlueprintGeneratedClass")).ToList();
        CUE4Parse.FileProvider.DefaultFileProvider prov;
        try
        {
            prov = new CUE4Parse.FileProvider.DefaultFileProvider(legacyRoot, SearchOption.AllDirectories, new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_UE5_4));
            prov.Initialize();
        }
        catch (Exception ex) { log.Warn("Could not inspect meshes for morph targets: " + ex.Message); return; }
        var top = new DirectoryInfo(legacyRoot).Name;
        foreach (var m in meshes)
        {
            try
            {
                var key = prov.Files.Keys.FirstOrDefault(k => k.EndsWith(m.RelPath, StringComparison.OrdinalIgnoreCase));
                if (key == null) continue;
                var objPath = key[..key.LastIndexOf('.')] + "." + m.ShortName;
                var mesh = prov.LoadPackageObject<CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh>(objPath);
                if (mesh.MorphTargets.Length == 0 || mesh.LODModels == null || mesh.LODModels.Length == 0) continue;
                var lod = mesh.LODModels[0];
                foreach (var mtp in mesh.MorphTargets)
                {
                    var mt = mtp.Load<CUE4Parse.UE4.Assets.Exports.Animation.UMorphTarget>(); if (mt == null) continue;
                    var secs = mt.MorphLODModels.Length > 0 ? mt.MorphLODModels[0].SectionIndices ?? Array.Empty<int>() : Array.Empty<int>();
                    var mats = secs.Where(s => s >= 0 && s < lod.Sections.Length).Select(s => lod.Sections[s].MaterialIndex)
                                   .Where(i => i >= 0 && i < mesh.SkeletalMaterials.Length).Select(i => mesh.SkeletalMaterials[i].MaterialInterface?.Name).ToList();
                    var hit = mats.Where(n => n != null && reparented.Contains(n)).Distinct().ToList();
                    if (hit.Count == 0) continue;
                    // rename the morph inside the mesh, so nothing (mod AnimBP, game animation curves) can drive it
                    bool inMesh = RenameMorphInMesh(m, mt.Name);
                    // and disable it in any AnimBP of the mod that drives it
                    int renamed = 0;
                    foreach (var bp in animBps)
                    {
                        var names = bp.Asset.GetNameMapIndexList();
                        for (int i = 0; i < names.Count; i++)
                            if (names[i].Value == mt.Name) { bp.Asset.SetNameReference(i, new FString(mt.Name + "_Off")); bp.Dirty = true; renamed++; log.Change($"{bp.ShortName}: disabled morph target '{mt.Name}'"); }
                    }
                    log.Warn(renamed > 0 || inMesh
                        ? $"{m.ShortName}: morph '{mt.Name}' moves {string.Join(", ", hit)}, which now uses a game material without morph support (it would render gray while active). The morph was switched off, so it no longer animates (e.g. a facial expression, or a shape that hides/reshapes part of the model)."
                        : $"{m.ShortName}: morph '{mt.Name}' moves {string.Join(", ", hit)}, which now uses a game material without morph support and couldn't be switched off, so if that part looks gray in game, this is why.");
                }
            }
            catch (Exception ex)
            {
                // can't read the mesh in detail (e.g. unversioned properties): if it uses any material that now runs on a
                // game material, switch all its morphs off to be safe - a gray body part is worse than a lost expression
                var morphs = m.Asset.Exports.Where(x => x.GetExportClassType()?.Value?.Value == "MorphTarget").Select(x => x.ObjectName.ToString()).ToList();
                var usesReparented = m.Asset.Imports.Any(i => reparented.Contains(Str(i.ObjectName)));
                if (morphs.Count == 0 || !usesReparented) { log.Info($"{m.ShortName}: morph targets not checked in detail ({ex.Message}); none affect converted materials"); continue; }
                int n = morphs.Count(mn => RenameMorphInMesh(m, mn));
                log.Warn($"{m.ShortName}: couldn't check which parts its {morphs.Count} morph target(s) move ({ex.Message}). Because it uses materials that now run on the game's material (which renders morphing parts gray), {n} morph(s) were switched off: {string.Join(", ", morphs.Take(12))}{(morphs.Count > 12 ? ", ..." : "")}.");
            }
        }
    }

    /// <summary>Checks that every reference between the mod's own packages points to an object that really exists
    /// (same name and number, as Unreal compares them); a broken link makes the game use a default (gray) asset.</summary>
    void ValidateLinks()
    {
        var live = byPackage.Values.Where(a => !a.Removed).ToDictionary(a => a.PackageName, StringComparer.OrdinalIgnoreCase);
        int bad = 0;
        foreach (var a in live.Values)
        {
            UAsset u;
            try { u = new UAsset(a.Path, EngineVersion.VER_UE5_4); } catch { continue; }
            foreach (var imp in u.Imports.Where(i => i.OuterIndex.IsImport() && !Str(i.ObjectName).StartsWith("UnresolvedExport_")))
            {
                var outer = Outer(u, imp); if (outer == null || outer.OuterIndex.Index != 0) continue;   // only objects directly inside a package
                var pkg = Str(outer.ObjectName); if (!live.TryGetValue(pkg, out var target) || target == a) continue;
                UAsset tu; try { tu = new UAsset(target.Path, EngineVersion.VER_UE5_4); } catch { continue; }
                bool found = tu.Exports.Any(e => e.OuterIndex.Index == 0 && e.ObjectName?.Value?.Value == imp.ObjectName?.Value?.Value && e.ObjectName.Number == imp.ObjectName.Number);
                if (!found && bad++ < 30) log.Warn($"{a.ShortName}: links to {Str(imp.ObjectName)} in {target.ShortName}, but that object isn't there; the game will use a default asset instead.");
            }
        }
        if (bad == 0) log.Info("Checked links between the mod's own files: all found.");
    }

    /// <summary>Renames a MorphTarget export of a mesh (Name -> Name_Off) so no curve or AnimBP can find and drive it.</summary>
    bool RenameMorphInMesh(ModAsset mesh, string morph)
    {
        var u = mesh.Asset;
        var e = u.Exports.FirstOrDefault(x => x.GetExportClassType()?.Value?.Value == "MorphTarget" && x.ObjectName.ToString() == morph);
        if (e == null) return false;
        u.AddNameReference(new FString(morph + "_Off"));
        e.ObjectName = N(u, morph + "_Off");
        mesh.Dirty = true;
        return true;
    }
}
