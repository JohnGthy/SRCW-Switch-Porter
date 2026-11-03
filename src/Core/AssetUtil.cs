using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
namespace SwitchPorter;

/// <summary>A converted (legacy) package of the mod.</summary>
public class ModAsset
{
    public string Path;          // .uasset on disk
    public string RelPath;       // "UNION/Content/....uasset"
    public string PackageName;   // "/Game/..."
    public UAsset Asset;
    public bool Dirty, Removed;
    public string Container;     // output pak name
    public string ShortName => PackageName.Substring(PackageName.LastIndexOf('/') + 1);
    public string MainClass => Asset.Exports.FirstOrDefault(e => e.ObjectName.Value.Value == ShortName)?.GetExportClassType()?.Value?.Value
                               ?? Asset.Exports.LastOrDefault()?.GetExportClassType()?.Value?.Value;
    public NormalExport MainExport => Asset.Exports.OfType<NormalExport>().FirstOrDefault(e => e.ObjectName.Value.Value == ShortName);
}

public static class AssetUtil
{
    public static FName N(UAsset u, string s) => FName.FromString(u, s);
    public static string Str(FName n) => n?.ToString() ?? "";

    public static Import Imp(UAsset u, FPackageIndex i) => i != null && i.IsImport() ? u.Imports[-i.Index - 1] : null;
    public static Import Outer(UAsset u, Import imp) => Imp(u, imp?.OuterIndex);

    /// <summary>Package name an import ultimately lives in.</summary>
    public static string ImportPackage(UAsset u, Import imp)
    {
        var cur = imp; int guard = 0;
        while (cur != null && cur.OuterIndex != null && cur.OuterIndex.Index != 0 && guard++ < 20) cur = Outer(u, cur);
        return cur == null ? null : Str(cur.ObjectName);
    }

    /// <summary>Map of import index (negative package index) -> property paths that reference it.</summary>
    public static Dictionary<int, List<string>> ReferenceUsage(UAsset u)
    {
        var map = new Dictionary<int, List<string>>();
        void Visit(PropertyData p, string path)
        {
            var here = path.Length == 0 ? Str(p.Name) : path + "." + Str(p.Name);
            switch (p)
            {
                case ObjectPropertyData o when o.Value != null && o.Value.IsImport():
                    (map.TryGetValue(o.Value.Index, out var l) ? l : map[o.Value.Index] = new()).Add(here); break;
                case ArrayPropertyData a when a.Value != null: foreach (var x in a.Value) Visit(x, here); break;
                case StructPropertyData s when s.Value != null: foreach (var x in s.Value) Visit(x, here); break;
                case MapPropertyData m when m.Value != null: foreach (var kv in m.Value) { Visit(kv.Key, here); Visit(kv.Value, here); } break;
            }
        }
        foreach (var e in u.Exports.OfType<NormalExport>()) if (e.Data != null) foreach (var p in e.Data) Visit(p, "");
        return map;
    }

    public static PropertyData Prop(NormalExport e, string name) => e?.Data?.FirstOrDefault(p => Str(p.Name) == name);

    /// <summary>
    /// Length of the non-shader part that follows a MaterialInstance's tagged properties
    /// (UObject guid flag + optional FMaterialInstanceCachedData), or -1 if not recognised.
    /// </summary>
    public static int MaterialInstanceCachedDataLength(UAsset u, byte[] extras)
    {
        if (extras == null || extras.Length < 8) return -1;
        if (BitConverter.ToInt32(extras, 0) != 0) return -1;
        int saved = BitConverter.ToInt32(extras, 4);
        if (saved == 0) return 8;
        if (saved != 1) return -1;
        try
        {
            using var ms = new MemoryStream(extras); using var r = new AssetBinaryReader(ms, u);
            ms.Position = 8;
            for (int guard = 0; guard < 1000; guard++)
            {
                var prop = MainSerializer.Read(r, null, null, null, null, true);
                if (prop == null) return (int)ms.Position;
            }
        }
        catch { }
        return -1;
    }
}
