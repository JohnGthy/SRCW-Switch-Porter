using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
namespace SwitchPorter;

/// <summary>
/// Decides whether a material's color texture alpha actually cuts holes in the model (hair cards, lace...)
/// or is only padding around the painted areas of the texture.
/// </summary>
public class AlphaCheck
{
    readonly DefaultFileProvider prov; readonly List<USkeletalMesh> meshes = new();
    public AlphaCheck(string legacyRoot)
    {
        prov = new DefaultFileProvider(legacyRoot, SearchOption.AllDirectories, new VersionContainer(EGame.GAME_UE5_4)); prov.Initialize();
        foreach (var k in prov.Files.Keys.Where(k => k.EndsWith(".uasset")))
            try
            {
                var pkg = prov.LoadPackage(k);
                foreach (var ex in pkg.GetExports()) if (ex is USkeletalMesh m) meshes.Add(m);
            }
            catch { }
    }

    /// <summary>Fraction (0..1) of the vertices drawn with this material whose alpha is below half; null if unknown.</summary>
    public double? TransparentFraction(string materialName, string texturePackageRel)
    {
        byte[,] alpha = LoadAlpha(texturePackageRel, out int w, out int h);
        if (alpha == null) return null;
        long total = 0, clear = 0;
        foreach (var m in meshes)
        {
            if (m.LODModels == null || m.LODModels.Length == 0) continue;
            var slots = m.SkeletalMaterials.Select((s, i) => (s.MaterialInterface?.Name, i)).Where(x => x.Name == materialName).Select(x => x.i).ToHashSet();
            if (slots.Count == 0) continue;
            var lod = m.LODModels[0]; var vb = lod.VertexBufferGPUSkin;
            FSkelMeshVertexBase[] vs = (FSkelMeshVertexBase[])vb.VertsFloat ?? vb.VertsHalf; if (vs == null) continue;
            foreach (var s in lod.Sections.Where(s => slots.Contains(s.MaterialIndex)))
                for (long i = s.BaseVertexIndex; i < s.BaseVertexIndex + s.NumVertices; i++)
                {
                    var uv = vs[i].UVs[0]; double u = uv.U - Math.Floor(uv.U), v = uv.V - Math.Floor(uv.V);
                    int x = Math.Clamp((int)(u * w), 0, w - 1), y = Math.Clamp((int)(v * h), 0, h - 1);
                    total++; if (alpha[x, y] < 128) clear++;
                }
        }
        return total == 0 ? null : (double)clear / total;
    }

    byte[,] LoadAlpha(string rel, out int w, out int h)
    {
        w = h = 0;
        try
        {
            var key = prov.Files.Keys.FirstOrDefault(k => k.EndsWith(rel, StringComparison.OrdinalIgnoreCase)); if (key == null) return null;
            var name = Path.GetFileNameWithoutExtension(rel);
            var t = prov.LoadPackageObject<UTexture2D>(key[..key.LastIndexOf('.')] + "." + name);
            var pd = t.PlatformData; var mip = pd.Mips.FirstOrDefault(mm => mm.SizeX <= 256 && mm.BulkData?.Data != null) ?? pd.Mips.Last(mm => mm.BulkData?.Data != null);
            w = mip.SizeX; h = mip.SizeY; var d = mip.BulkData.Data; var a = new byte[w, h];
            var fmt = pd.PixelFormat.ToString();
            if (fmt.Contains("DXT5") || fmt.Contains("BC3"))
            {
                int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
                for (int by = 0; by < bh; by++)
                    for (int bx = 0; bx < bw; bx++)
                    {
                        int o = (by * bw + bx) * 16; if (o + 8 > d.Length) continue;
                        byte a0 = d[o], a1 = d[o + 1]; ulong bits = 0; for (int i = 0; i < 6; i++) bits |= (ulong)d[o + 2 + i] << (8 * i);
                        var pal = new byte[8]; pal[0] = a0; pal[1] = a1;
                        if (a0 > a1) for (int i = 1; i < 7; i++) pal[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
                        else { for (int i = 1; i < 5; i++) pal[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5); pal[6] = 0; pal[7] = 255; }
                        for (int py = 0; py < 4; py++) for (int px = 0; px < 4; px++)
                            {
                                int x = bx * 4 + px, y = by * 4 + py; if (x >= w || y >= h) continue;
                                a[x, y] = pal[(int)((bits >> (3 * (py * 4 + px))) & 7)];
                            }
                    }
                return a;
            }
            if (fmt.Contains("B8G8R8A8") || fmt.Contains("R8G8B8A8"))
            { for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) a[x, y] = d[(y * w + x) * 4 + 3]; return a; }
            if (fmt.Contains("BC7"))
            {
                int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4); Span<uint> px = stackalloc uint[16];
                for (int by = 0; by < bh; by++)
                    for (int bx = 0; bx < bw; bx++)
                    {
                        int o = (by * bw + bx) * 16; if (o + 16 > d.Length) continue;
                        ThirdParty.Bc7Decoder.DecodeBC7Block(System.Runtime.InteropServices.MemoryMarshal.Read<UInt128>(d.AsSpan(o, 16)), px);
                        for (int py = 0; py < 4; py++) for (int pxx = 0; pxx < 4; pxx++)
                            {
                                int x = bx * 4 + pxx, y = by * 4 + py; if (x >= w || y >= h) continue;
                                a[x, y] = (byte)(px[py * 4 + pxx] >> 24);
                            }
                    }
                return a;
            }
            return null; // other formats: unknown
        }
        catch { return null; }
    }
}
