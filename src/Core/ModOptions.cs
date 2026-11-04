using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace SwitchPorter;

/// <summary>
/// Optional parts of a mod (outfits, accessories, add-ons). Read from the mod's mod.ini when it has one
/// ([Config:Name] groups + a [Files] list, as used by PC mod managers), otherwise every .pak/.utoc container is an
/// add-on that can be switched on or off; a ".disabled" extension marks parts that are currently off.
/// </summary>
public class ModOptions
{
    public class Group
    {
        public string Name, Description;
        public bool Multiple;                 // SelectMultiple (checkboxes) vs SelectOne (one choice)
        public List<string> Choices = new();
        public HashSet<string> Selected = new(StringComparer.OrdinalIgnoreCase);
    }

    public string ModDir, Title;
    public bool FromIni;
    public List<Group> Groups = new();
    // file (relative to the mod folder, without .disabled) -> "Group.Choice" it belongs to; files not listed are always included
    readonly Dictionary<string, List<string>> fileRules = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> allFiles = new();   // relative paths as they are on disk (may end in .disabled)

    static string Norm(string p) => p.Replace('\\', '/').Trim().TrimStart('/');
    static string StripDisabled(string p) => p.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? p[..^".disabled".Length] : p;
    static readonly string[] ModExts = { ".pak", ".utoc", ".ucas", ".json" };

    public static ModOptions Load(string modDir)
    {
        var m = new ModOptions { ModDir = modDir };
        foreach (var f in Directory.GetFiles(modDir, "*", SearchOption.AllDirectories))
        {
            var rel = Norm(Path.GetRelativePath(modDir, f));
            if (ModExts.Contains(Path.GetExtension(StripDisabled(rel)).ToLowerInvariant())) m.allFiles.Add(rel);
        }
        var ini = Directory.GetFiles(modDir, "mod.ini", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (ini != null) m.ParseIni(File.ReadAllLines(ini));
        if (m.Groups.Count == 0) m.SimpleAddOns();
        return m;
    }

    /// <summary>True when the mod has anything to choose.</summary>
    public bool HasChoices => Groups.Any(g => g.Choices.Count > 1 || g.Multiple);

    void ParseIni(string[] lines)
    {
        string section = null; Group cur = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line[1..^1].Trim(); cur = null;
                if (section.StartsWith("Config:", StringComparison.OrdinalIgnoreCase)) { cur = new Group { Name = section[7..].Trim() }; Groups.Add(cur); }
                continue;
            }
            int eq = line.IndexOf('='); if (eq < 0) continue;
            var k = line[..eq].Trim(); var v = line[(eq + 1)..].Trim();
            if (section != null && section.Equals("Main", StringComparison.OrdinalIgnoreCase) && k.Equals("Name", StringComparison.OrdinalIgnoreCase)) Title = v;
            else if (cur != null)
            {
                if (k.Equals("Type", StringComparison.OrdinalIgnoreCase)) cur.Multiple = v.Contains("Multiple", StringComparison.OrdinalIgnoreCase);
                else if (k.Equals("Description", StringComparison.OrdinalIgnoreCase)) cur.Description = v;
                else if (k.Equals("Options", StringComparison.OrdinalIgnoreCase)) cur.Choices = v.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            }
            else if (section != null && section.Equals("Files", StringComparison.OrdinalIgnoreCase))
                (fileRules.TryGetValue(Norm(k), out var l) ? l : fileRules[Norm(k)] = new()).Add(v);
        }
        if (Groups.Count == 0) return;
        FromIni = true;
        Groups.RemoveAll(g => g.Choices.Count == 0);
        foreach (var g in Groups) g.Choices.RemoveAll(c => c == "(Blank)");
        // current state: a choice is selected when its files are present without ".disabled"
        foreach (var g in Groups)
        {
            foreach (var c in g.Choices)
            {
                var files = FilesOf(g.Name, c);
                if (files.Count > 0 && files.All(f => !f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))) g.Selected.Add(c);
            }
            if (!g.Multiple)
            {
                // choices without files (e.g. the default outfit) are selected when no other choice is
                if (g.Selected.Count != 1)
                {
                    var keep = g.Selected.FirstOrDefault() ?? g.Choices.FirstOrDefault(c => FilesOf(g.Name, c).Count == 0) ?? g.Choices.First();
                    g.Selected.Clear(); g.Selected.Add(keep);
                }
            }
        }
    }

    void SimpleAddOns()
    {
        var g = new Group { Name = "Parts", Multiple = true, Description = "Parts of the mod (each .pak / .utoc file). Files ending in .disabled are switched off." };
        foreach (var stem in allFiles.Where(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".json.disabled", StringComparison.OrdinalIgnoreCase))
                                     .Select(f => { var s = StripDisabled(f); return s[..^Path.GetExtension(s).Length]; })
                                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
        {
            g.Choices.Add(stem);
            var files = allFiles.Where(f => SameStem(f, stem)).ToList();
            if (files.Any(f => !f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))) g.Selected.Add(stem);
            foreach (var f in files) fileRules[StripDisabled(f)] = new() { "Parts." + stem };
        }
        if (g.Choices.Count > 0) Groups.Add(g);
    }

    static bool SameStem(string file, string stem)
    {
        var s = StripDisabled(file); return s[..^Path.GetExtension(s).Length].Equals(stem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Files on disk (relative) that belong to one choice of a group.</summary>
    List<string> FilesOf(string group, string choice)
    {
        var tag = group + "." + choice; var res = new List<string>();
        foreach (var (rule, tags) in fileRules)
        {
            if (!tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var f in allFiles)
            {
                var s = StripDisabled(f);
                // a rule names a file, or a file without extension (all its .pak/.ucas/.utoc)
                if (s.Equals(rule, StringComparison.OrdinalIgnoreCase) || (Path.GetExtension(rule) == "" && SameStem(f, rule)) ||
                    (Path.GetExtension(rule) == "" && s[..^Path.GetExtension(s).Length].Equals(rule, StringComparison.OrdinalIgnoreCase)))
                    res.Add(f);
            }
        }
        return res.Distinct().ToList();
    }

    bool Covered(string file) => fileRules.Keys.Any(rule =>
    {
        var s = StripDisabled(file);
        return s.Equals(rule, StringComparison.OrdinalIgnoreCase) || (Path.GetExtension(rule) == "" && s[..^Path.GetExtension(s).Length].Equals(rule, StringComparison.OrdinalIgnoreCase));
    });

    /// <summary>Full paths of the files to convert for the current selection.</summary>
    public List<string> SelectedFiles()
    {
        var res = new List<string>();
        foreach (var f in allFiles)
        {
            if (!Covered(f)) { if (!f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) res.Add(f); continue; }
            foreach (var g in Groups) foreach (var c in g.Selected) if (FilesOf(g.Name, c).Contains(f)) res.Add(f);
        }
        return res.Distinct().Select(f => Path.Combine(ModDir, f.Replace('/', Path.DirectorySeparatorChar))).ToList();
    }

    /// <summary>Containers (file stems) that belong to an optional choice; their copy of an asset wins over the base one.</summary>
    public HashSet<string> OptionContainers()
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!FromIni) return res;   // plain add-ons: no part outranks another
        foreach (var g in Groups)
        {
            // an "Enabled/Disabled" switch for the main part isn't an override
            if (!g.Multiple && g.Choices.Count == 2 && g.Choices.Any(c => c.Equals("Enabled", StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var c in g.Selected) foreach (var f in FilesOf(g.Name, c))
                { var s = Path.GetFileName(StripDisabled(f)); res.Add(s[..^Path.GetExtension(s).Length]); }
        }
        return res;
    }

    /// <summary>Applies a selection like "Outfit=GT;Accessories=Tail,Halo" (unknown names are ignored).</summary>
    public void Apply(string spec)
    {
        foreach (var part in (spec ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('='); if (eq < 0) continue;
            var g = Groups.FirstOrDefault(x => x.Name.Equals(part[..eq].Trim(), StringComparison.OrdinalIgnoreCase)); if (g == null) continue;
            var picks = part[(eq + 1)..].Split(',').Select(x => x.Trim()).Where(x => g.Choices.Contains(x, StringComparer.OrdinalIgnoreCase))
                                                  .Select(x => g.Choices.First(c => c.Equals(x, StringComparison.OrdinalIgnoreCase))).ToList();
            if (!g.Multiple && picks.Count == 0) continue;
            g.Selected.Clear(); foreach (var p in g.Multiple ? picks : picks.Take(1)) g.Selected.Add(p);
        }
    }

    public string Describe() => string.Join("; ", Groups.Select(g => $"{g.Name}: {(g.Selected.Count == 0 ? "none" : string.Join(", ", g.Selected))}"));
}
