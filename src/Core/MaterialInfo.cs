using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
namespace SwitchPorter;

/// <summary>Default parameter values of a mod's own (custom) master material, read from its cooked cached data.</summary>
public class MasterDefaults
{
    public Dictionary<string, float> Scalars = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> Switches = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, (float r, float g, float b)> Vectors = new(StringComparer.OrdinalIgnoreCase);
}

public class MaterialInfo
{
    readonly DefaultFileProvider prov;
    readonly Dictionary<string, MasterDefaults> cache = new(StringComparer.OrdinalIgnoreCase);
    public MaterialInfo(string legacyRoot)
    {
        prov = new DefaultFileProvider(legacyRoot, SearchOption.AllDirectories, new VersionContainer(EGame.GAME_UE5_4));
        prov.Initialize();
    }

    /// <summary>Defaults of the master material stored at this mod-relative path (…/Name.uasset); null if unreadable.</summary>
    public MasterDefaults Get(string relPath)
    {
        if (cache.TryGetValue(relPath, out var d)) return d;
        d = null;
        try
        {
            var key = prov.Files.Keys.FirstOrDefault(k => k.EndsWith(relPath, StringComparison.OrdinalIgnoreCase));
            if (key != null)
            {
                var name = Path.GetFileNameWithoutExtension(relPath);
                var m = prov.LoadPackageObject<UMaterialInterface>(key[..key.LastIndexOf('.')] + "." + name);
                var c = m.CachedExpressionData;
                if (c != null)
                {
                    d = new MasterDefaults();
                    // RuntimeEntries[type]: 0 = scalar, 1 = vector, 2 = double vector, 3 = texture, ..., 7 = static switch
                    List<string> Names(int type)
                    {
                        var tag = c.Properties.FirstOrDefault(p => p.Name.Text == "RuntimeEntries" && p.ArrayIndex == type);
                        if (tag?.Tag?.GenericValue is not FScriptStruct { StructType: FStructFallback e }) return new();
                        if (!e.TryGetValue(out FStructFallback[] set, "ParameterInfoSet") || set == null) return new();
                        return set.Select(s => s.GetOrDefault<FName>("Name").Text).ToList();
                    }
                    c.TryGetValue(out float[] sv, "ScalarValues"); c.TryGetValue(out bool[] sw, "StaticSwitchValues");
                    var sn = Names(0); for (int i = 0; i < sn.Count && sv != null && i < sv.Length; i++) d.Scalars[sn[i]] = sv[i];
                    if (c.TryGetValue(out CUE4Parse.UE4.Objects.Core.Math.FLinearColor[] vv, "VectorValues") && vv != null)
                    { var vn = Names(1); for (int i = 0; i < vn.Count && i < vv.Length; i++) d.Vectors[vn[i]] = (vv[i].R, vv[i].G, vv[i].B); }
                    var wn = Names(7); for (int i = 0; i < wn.Count && sw != null && i < sw.Length; i++) d.Switches[wn[i]] = sw[i];
                }
            }
        }
        catch { d = null; }
        cache[relPath] = d;
        return d;
    }
}
