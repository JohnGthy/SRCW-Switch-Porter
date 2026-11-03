using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
namespace SwitchPorter;

/// <summary>Character display names (the game's "DB_CharacterName" text table).</summary>
public static class Naming
{
    public const string Ns = "DB_CharacterName";
    public const string PakName = "SwitchPorter_Names_999_P";

    /// <summary>All name slots: key -> current text.</summary>
    public static List<(string key, string text)> Slots(LocRes en) =>
        en.Namespaces.FirstOrDefault(n => n.ns == Ns).keys?.Select(e => (e.Key, en.Strings[e.StringIndex])).OrderBy(x => x.Key, StringComparer.Ordinal).ToList() ?? new();

    /// <summary>Works out which name slot a mod's skin replaces from its UI / folder names.</summary>
    public static string Detect(IEnumerable<string> modPackages, LocRes en)
    {
        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // "sha" -> "006"
        foreach (var (ns, _, _) in en.Namespaces) { var m = Regex.Match(ns, "^DB_CV([0-9]{3})_([a-z]+)$", RegexOptions.IgnoreCase); if (m.Success) codes[m.Groups[2].Value] = m.Groups[1].Value; }
        bool Has(string k) => en.Find(Ns, k) != null;
        var shorts = modPackages.Select(p => p[(p.LastIndexOf('/') + 1)..]).ToList();
        foreach (var s in shorts)
        {
            var g = Regex.Match(s, "^UI_.*_Extnd([0-9]{2})_Character([0-9]{3})$", RegexOptions.IgnoreCase);
            if (g.Success) { var k = $"chara{g.Groups[1].Value}{g.Groups[2].Value}"; if (Has(k)) return k; if (Has("chara_extnd" + g.Groups[1].Value)) return "chara_extnd" + g.Groups[1].Value; }
            var m = Regex.Match(s, "^UI_.*?_([a-z]{3,5})_([0-9]{2})$", RegexOptions.IgnoreCase);
            if (m.Success && codes.TryGetValue(m.Groups[1].Value, out var idx))
            {
                int slot = int.Parse(m.Groups[2].Value);
                var k = slot <= 1 ? $"chara{idx}" : $"chara{slot - 1}{idx[1..]}";
                if (Has(k)) return k;
            }
        }
        // character folder named after the character, e.g. .../Character/Shadow/...
        foreach (var p in modPackages)
        {
            var m = Regex.Match(p, "/Asset/Character/([^/]+)/", RegexOptions.IgnoreCase); if (!m.Success) continue;
            var hit = Slots(en).FirstOrDefault(x => string.Equals(x.text, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            if (hit.key != null) return hit.key;
        }
        return null;
    }

    /// <summary>Loads Game.locres per language from a .pak, or from a folder (…/&lt;lang&gt;/Game.locres).</summary>
    public static Dictionary<string, byte[]> LoadBase(string source, string work)
    {
        var res = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(source)) return res;
        if (Directory.Exists(source))
        {
            foreach (var f in Directory.GetFiles(source, "Game.locres", SearchOption.AllDirectories))
                res[new DirectoryInfo(Path.GetDirectoryName(f)).Name] = File.ReadAllBytes(f);
            return res;
        }
        if (!File.Exists(source)) return res;
        var tmp = Path.Combine(work, "textread_" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(tmp);
        File.Copy(source, Path.Combine(tmp, Path.GetFileName(source)));
        foreach (var ext in new[] { ".utoc", ".ucas" }) { var s2 = Path.ChangeExtension(source, ext); if (File.Exists(s2)) File.Copy(s2, Path.Combine(tmp, Path.GetFileName(s2))); }
        var prov = new CUE4Parse.FileProvider.DefaultFileProvider(tmp, SearchOption.TopDirectoryOnly, new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_UE5_4));
        prov.Initialize(); prov.Mount();
        foreach (var f in prov.Files.Values.Where(f => f.Path.EndsWith("/Game.locres", StringComparison.OrdinalIgnoreCase) && f.Path.Contains("Localization/Game/", StringComparison.OrdinalIgnoreCase)))
        {
            var parts = f.Path.Split('/'); res[parts[^2]] = f.Read();
        }
        return res;
    }

    /// <summary>Writes the names pak: base text with every saved name applied, for all languages.</summary>
    public static string WritePak(Dictionary<string, byte[]> baseText, IDictionary<string, string> names, string outPaks, string work, Log log)
    {
        var dir = Path.Combine(work, "names"); var files = new Dictionary<string, string>();
        foreach (var (lang, data) in baseText.OrderBy(k => k.Key))
        {
            var l = LocRes.Read(data); int n = 0;
            foreach (var (key, text) in names) if (l.Set(Ns, key, text)) n++;
            var rel = $"UNION/Content/Localization/Game/{lang}/Game.locres"; var f = Path.Combine(dir, lang, "Game.locres");
            Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllBytes(f, l.Write()); files[rel] = f;
        }
        var pak = Path.Combine(outPaks, PakName + ".pak");
        PakWriter.Write(pak, files);
        return pak;
    }
}
