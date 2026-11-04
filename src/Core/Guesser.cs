using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
namespace SwitchPorter;

/// <summary>
/// Without a dump, finds the names of game assets a mod references by generating likely names from the game's
/// folder/naming patterns and keeping only those whose hash matches the id stored in the mod (so a match is exact).
/// </summary>
public class Guesser
{
    static readonly string[] KnownCharacters = {
        "Sonic","Tails","Knuckles","Amy","Cream","Big","Shadow","Rouge","Omega","Vector","Espio","Charmy","Blaze","Silver",
        "Jet","Wave","Storm","Eggman","Metal","MetalSonic","Zavok","Sage","Chao","Cheese","Froggy","Infinite","Tangle","Whisper",
        "Classic","ClassicSonic","SuperSonic","Shade","Marine","Sticks","Mephiles","Chaos","EggPawn","Orbot","Cubot","Dodon","Shahra","Gemerl",
        "Hatsune","Miku","Joker","Ichiban","Kasuga","Goro","Majima","Hinata","Pacman","Steve","Alex","Creeper","SpongeBob","Patrick",
        "MegaMan","ProtoMan","Leonardo","Raphael","Donatello","Michelangelo","Aang","Katara","Red","Lisa","Momo","Ai"
    };
    static readonly string[] Parts = {
        "Eye","Eyes","Fur","Skin","Parts","Side","Body","Hair","Cloth","Clothes","Shoes","Shoe","Glove","Gloves","Mouth","Teeth","Tooth",
        "Face","Head","Metal","Glass","Lens","Emissive","Emi","Ribbon","Dress","Bag","Hammer","Ring","Band","Belt","Jacket","Pants","Socks",
        "Base","Main","Sub","Accessory","Acc","Bracelet","Tongue","Nose","Ear","Ears","Tail","Tails","Wing","Wings","Shell","Spike","Spikes",
        "Quill","Quills","Fin","Hat","Cape","Scarf","Mask","Visor","Light","Line","Outline","Shadow","Inner","Outer","Black","White","Fx","Effect"
    };
    static readonly string[] Sides = { "_L", "_R", "", "_L_R", "_LR", "_Low", "_High" };
    static readonly string[] Sub = { "Mesh", "Material", "Materials", "Texture", "Textures", "Physics", "Anim", "Animation", "" };
    const string CharRoot = "/Game/01_Union/Asset/Character";
    static readonly string[] CharRoots = { "/Game/01_Union/Asset/Character", "/Game/02_Union/Asset/Character" };
    static readonly System.Text.RegularExpressions.Regex CharRootRx = new("^/Game/0[0-9]_Union/Asset/Character/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    /// <summary>"Extnd11_Character11002" -> neighbouring costume numbers ("...11000".."...11019")</summary>
    static IEnumerable<string> NumberVariants(string s)
    {
        yield return s;
        var m = Regex.Match(s, "^(.*?)([0-9]{2,})$"); if (!m.Success) yield break;
        var head = m.Groups[1].Value; var num = m.Groups[2].Value; var stem = num[..^2];
        for (int i = 0; i < 20; i++) yield return head + stem + i.ToString("00");
    }

    readonly HashSet<string> chars = new(StringComparer.OrdinalIgnoreCase);
    // generic dirs/names are combined with everything; per-character ones only with the same character
    readonly HashSet<string> dirs = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, HashSet<string>> charDirs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, HashSet<string>> charNames = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> CD(string c) => charDirs.TryGetValue(c, out var h) ? h : charDirs[c] = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> CN(string c) => charNames.TryGetValue(c, out var h) ? h : charNames[c] = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="modPackages">package names contained in the mod</param>
    /// <param name="extraNames">e.g. material slot names read from the mod's meshes</param>
    public Guesser(IEnumerable<string> modPackages, IEnumerable<string> extraNames)
    {
        var modShort = new List<string>();
        foreach (var p in modPackages)
        {
            var d = p.Substring(0, p.LastIndexOf('/'));
            for (var x = d; x.Length > 5 && x.Contains('/'); x = x.Substring(0, x.LastIndexOf('/'))) dirs.Add(x);
            modShort.Add(p.Substring(p.LastIndexOf('/') + 1));
            var rm = CharRootRx.Match(p);
            if (rm.Success)
                foreach (var seg in p.Substring(rm.Length).Split('/').SkipLast(1)) if (!Sub.Contains(seg, StringComparer.OrdinalIgnoreCase) && seg != "Skin") foreach (var v in NumberVariants(seg)) chars.Add(v);
        }
        foreach (var c in KnownCharacters) chars.Add(c);
        foreach (var c in chars.ToList()) if (c.EndsWith("Skin")) chars.Add(c[..^4]);
        foreach (var c in chars)
            foreach (var s in Sub)
            {
                foreach (var root in CharRoots)
                {
                    CD(c).Add(Trim($"{root}/{c}/{s}")); CD(c).Add(Trim($"{root}/Skin/{c}/{s}")); CD(c).Add(Trim($"{root}/Skin/{c}Skin/{s}"));
                    if (c.EndsWith("Skin")) CD(c).Add(Trim($"{root}/{c[..^4]}/{s}"));
                }
            }
        foreach (var d in new[] { $"{CharRoot}/Master_Material", "/Game/02_Union/Asset/Character/Master_Material", "/Game/02_Union/Asset/Character/Common", $"{CharRoot}/Common", $"{CharRoot}/Common/Material", $"{CharRoot}/Common/Texture",
                                  $"{CharRoot}/Common/Mesh", "/Game/01_Union/Asset/Common/Texture", "/Game/01_Union/Asset/Common/Material", "/Game/01_Union/Asset/Common" })
            dirs.Add(d);

        // names
        var modTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in modShort) foreach (var t in n.Split('_')) if (t.Length >= 3 && !Parts.Contains(t, StringComparer.OrdinalIgnoreCase) && !Regex.IsMatch(t, "^(Inst|SK|M|MI|T|Bc|MRO|N|Mesh|Skeleton|AnimBP|PhysicsAsset)$", RegexOptions.IgnoreCase)) modTokens.Add(t);
        foreach (var n in modShort.Concat(extraNames ?? Enumerable.Empty<string>()))
        {
            names.Add(n); names.Add(n + "_Inst");
            foreach (var t in modTokens) if (n.Contains(t, StringComparison.OrdinalIgnoreCase)) foreach (var c in chars) CN(c).Add(Regex.Replace(n, Regex.Escape(t), c, RegexOptions.IgnoreCase));
        }
        foreach (var c in chars)
        {
            var names = CN(c);
            foreach (var pre in new[] { "SK_", "" }) foreach (var suf in new[] { "", "side", "_side", "Side" })
                {
                    var b = pre + c + suf;
                    names.Add(b); names.Add(b + "_PhysicsAsset"); names.Add(b + "_Physics"); names.Add(b + "_Skeleton"); names.Add(b + "_AnimBP"); names.Add("PA_" + c + suf);
                }
            foreach (var part in Parts) foreach (var side in Sides) foreach (var pre in new[] { "M_", "MI_", "Mat_" })
                    { var b = $"{pre}{c}_{part}{side}"; names.Add(b); names.Add(b + "_Inst"); }
            foreach (var t in new[] { "Bc", "MRO", "N", "Spt", "Fl", "E", "Mask" }) foreach (var part in Parts.Prepend("").Prepend(c)) foreach (var side in Sides)
                    { names.Add(part == "" ? $"T_{c}_{t}" : part == c ? $"T_{c}{side}_{t}" : $"T_{c}_{part}{side}_{t}"); }
        }
        // engine / plugin animation settings and the shared character skeletons
        foreach (var full in new[] { "/Engine/Animation/DefaultAnimBoneCompressionSettings", "/Engine/Animation/DefaultAnimCurveCompressionSettings",
                                     "/Engine/Animation/DefaultVariableFrameStrippingSettings", "/Engine/Animation/DefaultRecorderBoneCompression",
                                     "/ACLPlugin/ACLAnimBoneCompressionSettings", "/ACLPlugin/ACLAnimCurveCompressionSettings" })
        { dirs.Add(full[..full.LastIndexOf('/')]); names.Add(full[(full.LastIndexOf('/') + 1)..]); }
        dirs.Add($"{CharRoot}/Common/Skeleton");
        foreach (var sz in new[] { "Large", "Medium", "Small", "Middle", "Big", "Tall", "Short", "Normal", "Base", "Common", "Human", "Animal" }) names.Add($"{sz}_Skeleton");
        foreach (var n in new[] { "M_Chara_Basic", "M_Chara_AfroHair", "M_Chara_Eye", "M_Chara_Skin", "M_Chara_Hair", "M_Chara_Metal", "M_Chara_Glass", "M_Chara_Translucent",
                                  "T_Dummy_Bc", "T_Dummy_MRO", "T_Dummy_N", "T_Dummy_Black", "T_Dummy_Linear_White", "T_Dummy_White" }) names.Add(n);
    }
    static string Trim(string s) => s.TrimEnd('/');

    public long CandidateCount => (long)dirs.Count * (names.Count + charNames.Values.Sum(h => h.Count))
        + charDirs.Sum(kv => (long)kv.Value.Count * (names.Count + (charNames.TryGetValue(kv.Key, out var n) ? n.Count : 0)));

    /// <summary>Returns package id -> package name for every target id that one of the candidates matches.</summary>
    public Dictionary<ulong, string> Resolve(ICollection<ulong> targets)
    {
        var want = new HashSet<ulong>(targets); var found = new Dictionary<ulong, string>();
        if (want.Count == 0) return found;
        var allNames = names.Concat(charNames.Values.SelectMany(h => h)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var jobs = new List<(string dir, string[] names)>();
        foreach (var d in dirs) jobs.Add((d, allNames));
        foreach (var (c, ds) in charDirs)
        {
            var ns = names.Concat(charNames.TryGetValue(c, out var h) ? h : Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var d in ds) if (!dirs.Contains(d)) jobs.Add((d, ns));
        }
        var lk = new object();
        System.Threading.Tasks.Parallel.ForEach(jobs, (job, state) =>
        {
            foreach (var n in job.names)
            {
                var full = job.dir + "/" + n; var id = CityHash.PackageId(full);
                if (want.Contains(id)) lock (lk) { if (!found.ContainsKey(id)) found[id] = full; if (found.Count == want.Count) state.Stop(); }
            }
        });
        return found;
    }

    /// <summary>Best guess of an asset's class from how it is used and its name.</summary>
    public static (string pkg, string cls) GuessClass(string objectName, string usage)
    {
        var n = objectName; var u = usage ?? "";
        if (n.EndsWith("_C"))
            return ("/Script/Engine", n.StartsWith("ABP", StringComparison.OrdinalIgnoreCase) || n.Contains("AnimBlueprint", StringComparison.OrdinalIgnoreCase)
                                      || n.Contains("AnimLayer", StringComparison.OrdinalIgnoreCase) || n.Contains("LayerInterface", StringComparison.OrdinalIgnoreCase)
                                      || u.Contains("AnimClass") || u.Contains("AnimBlueprint") ? "AnimBlueprintGeneratedClass" : "BlueprintGeneratedClass");
        if (n.StartsWith("AM_") || u.Contains("Montage")) return ("/Script/Engine", "AnimMontage");
        if (n.StartsWith("BS_") || n.StartsWith("BS1D_")) return ("/Script/Engine", n.Contains("1D") ? "BlendSpace1D" : "BlendSpace");
        if (n.StartsWith("AO_")) return ("/Script/Engine", "AimOffsetBlendSpace");
        if (n.StartsWith("AS_") || n.StartsWith("A_") || n.StartsWith("Anim_")) return ("/Script/Engine", "AnimSequence");
        if (n.StartsWith("PA_") && !n.Contains("PhysicsAsset") && u.Contains("Physics")) return ("/Script/Engine", "PhysicsAsset");
        if (u.EndsWith("PhysicsAsset") || n.Contains("PhysicsAsset") || n.StartsWith("PA_")) return ("/Script/Engine", "PhysicsAsset");
        if (u.EndsWith("BoneCompressionSettings") || n.EndsWith("BoneCompressionSettings") || n.EndsWith("BoneCompression")) return ("/Script/Engine", "AnimBoneCompressionSettings");
        if (u.EndsWith("CurveCompressionSettings") || n.EndsWith("CurveCompressionSettings")) return ("/Script/Engine", "AnimCurveCompressionSettings");
        if (u.EndsWith("VariableFrameStrippingSettings") || n.EndsWith("VariableFrameStrippingSettings")) return ("/Script/Engine", "VariableFrameStrippingSettings");
        if (u.EndsWith("SkeletalMesh") || u.EndsWith("SkinnedAsset")) return ("/Script/Engine", "SkeletalMesh");
        if (u.EndsWith("Skeleton") || n.EndsWith("_Skeleton")) return ("/Script/Engine", "Skeleton");
        if (u.Contains("TextureParameterValues") || n.StartsWith("T_")) return ("/Script/Engine", "Texture2D");
        if (u.EndsWith("Parent") || u.Contains("Material") || n.StartsWith("M_") || n.StartsWith("MI_"))
            return ("/Script/Engine", n.StartsWith("MI_") || n.EndsWith("_Inst") || n.Contains("_Inst_") ? "MaterialInstanceConstant" : "Material");
        if (n.StartsWith("SK_")) return ("/Script/Engine", "SkeletalMesh");
        if (n.StartsWith("SM_")) return ("/Script/Engine", "StaticMesh");
        return (null, null);
    }
}
