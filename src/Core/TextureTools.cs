using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
namespace SwitchPorter;

/// <summary>Lowers a cooked Texture2D's resolution by dropping its largest mips (no re-encoding).</summary>
public static class TextureTools
{
    static int I32(byte[] b, int o) => BitConverter.ToInt32(b, o);
    static void W32(byte[] b, int o, int v) => BitConverter.GetBytes(v).CopyTo(b, o);
    const uint ForceInline = 0x40, SeparateFile = 0x100, Optional = 0x800;

    public static string PixelFormat(UAsset u)
    {
        foreach (var n in u.GetNameMapIndexList()) { var s = n.Value; if (s.StartsWith("PF_")) return s; }
        return null;
    }

    /// <summary>Overwrites every mip of the texture with zeros (= fully transparent for alpha formats).</summary>
    public static bool MakeClear(string uassetPath)
    {
        var u = new UAsset(uassetPath, EngineVersion.VER_UE5_4);
        var e = u.Exports.OfType<NormalExport>().LastOrDefault(x => x.GetExportClassType()?.Value?.Value == "Texture2D");
        if (e?.Extras == null) return false;
        var x = e.Extras; int s = -1;
        for (int i = 0; i + 8 < x.Length && i < 512; i++)
        { int L = I32(x, i); if (L >= 5 && L <= 40 && i + 4 + L <= x.Length && x[i + 4] == 'P' && x[i + 5] == 'F' && x[i + 6] == '_' && x[i + 4 + L - 1] == 0) { s = i; break; } }
        if (s < 12) return false;
        bool hasOpt = (I32(x, s - 4) & 0x40000000) != 0;
        int after = s + 4 + I32(x, s) + (hasOpt ? 8 : 0); int nm = I32(x, after + 4), p = after + 8;
        for (int k = 0; k < nm; k++)
        {
            int idx = I32(x, p); if (idx < 0 || idx >= u.DataResources.Count) return false; var r = u.DataResources[idx]; p += 4;
            if ((r.LegacyBulkDataFlags & ForceInline) != 0) { Array.Clear(x, p, (int)r.SerialSize); p += (int)r.SerialSize; }
            p += 12;
        }
        e.Extras = x; u.Write(uassetPath);
        var ub = Path.ChangeExtension(uassetPath, ".ubulk");
        if (File.Exists(ub)) File.WriteAllBytes(ub, new byte[new FileInfo(ub).Length]);
        return true;
    }

    /// <summary>Fills a (DXT5) texture's inline mips with one solid color.</summary>
    public static bool MakeSolid(string uassetPath, byte r, byte g, byte b)
    {
        var u = new UAsset(uassetPath, EngineVersion.VER_UE5_4);
        var e = u.Exports.OfType<NormalExport>().LastOrDefault(x => x.GetExportClassType()?.Value?.Value == "Texture2D");
        if (e?.Extras == null) return false;
        var x = e.Extras; int s = -1;
        for (int i = 0; i + 8 < x.Length && i < 512; i++)
        { int L = I32(x, i); if (L >= 5 && L <= 40 && i + 4 + L <= x.Length && x[i + 4] == 'P' && x[i + 5] == 'F' && x[i + 6] == '_' && x[i + 4 + L - 1] == 0) { s = i; break; } }
        if (s < 12) return false;
        bool hasOpt = (I32(x, s - 4) & 0x40000000) != 0;
        int after = s + 4 + I32(x, s) + (hasOpt ? 8 : 0); int nm = I32(x, after + 4), p = after + 8;
        ushort c = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
        var block = new byte[] { 255, 255, 0, 0, 0, 0, 0, 0, (byte)c, (byte)(c >> 8), (byte)c, (byte)(c >> 8), 0, 0, 0, 0 };
        for (int k = 0; k < nm; k++)
        {
            int idx = I32(x, p); if (idx < 0 || idx >= u.DataResources.Count) return false; var res = u.DataResources[idx]; p += 4;
            if ((res.LegacyBulkDataFlags & ForceInline) != 0) { for (int q = 0; q < res.SerialSize; q++) x[p + q] = block[q % 16]; p += (int)res.SerialSize; }
            p += 12;
        }
        e.Extras = x; u.Write(uassetPath);
        return true;
    }

    /// <summary>
    /// Turns a black/white mask texture (DXT1 or DXT5) into a cut-out texture of one color: white areas keep the color,
    /// black areas become fully transparent (1-bit alpha). Same size and format, so the file layout doesn't change.
    /// With a flipbook (cols x rows frames) only the first frame is used, stretched over the whole texture.
    /// </summary>
    public static bool MakeCutout(string uassetPath, byte cr, byte cg, byte cb, int cols, int rows)
    {
        var u = new UAsset(uassetPath, EngineVersion.VER_UE5_4);
        var e = u.Exports.OfType<NormalExport>().LastOrDefault(x => x.GetExportClassType()?.Value?.Value == "Texture2D");
        if (e?.Extras == null) return false;
        var fmt = PixelFormat(u); bool dxt5 = fmt == "PF_DXT5"; if (fmt != "PF_DXT1" && !dxt5) return false;
        int bs = dxt5 ? 16 : 8;
        var x = e.Extras; int s = -1;
        for (int i = 0; i + 8 < x.Length && i < 512; i++)
        { int L = I32(x, i); if (L >= 5 && L <= 40 && i + 4 + L <= x.Length && x[i + 4] == 'P' && x[i + 5] == 'F' && x[i + 6] == '_' && x[i + 4 + L - 1] == 0) { s = i; break; } }
        if (s < 12) return false;
        bool hasOpt = (I32(x, s - 4) & 0x40000000) != 0;
        int after = s + 4 + I32(x, s) + (hasOpt ? 8 : 0); int nm = I32(x, after + 4), p = after + 8;
        var ubPath = Path.ChangeExtension(uassetPath, ".ubulk"); byte[] ub = File.Exists(ubPath) ? File.ReadAllBytes(ubPath) : Array.Empty<byte>();
        var mips = new List<(byte[] buf, int off, int w, int h)>();
        for (int k = 0; k < nm; k++)
        {
            int idx = I32(x, p); if (idx < 0 || idx >= u.DataResources.Count) return false; var r = u.DataResources[idx]; p += 4;
            int w, h;
            if ((r.LegacyBulkDataFlags & ForceInline) != 0) { int off = p; p += (int)r.SerialSize; w = I32(x, p); h = I32(x, p + 4); mips.Add((x, off, w, h)); }
            else if ((r.LegacyBulkDataFlags & SeparateFile) != 0) { w = I32(x, p); h = I32(x, p + 4); if (r.SerialOffset + r.SerialSize > ub.Length) return false; mips.Add((ub, (int)r.SerialOffset, w, h)); }
            else return false;
            p += 12;
        }
        if (mips.Count == 0) return false;
        // mask from the largest mip (brightness of the color part)
        var (b0, o0, W, H) = mips[0];
        var mask = new byte[W * H];
        int bw0 = Math.Max(1, (W + 3) / 4);
        for (int by = 0; by < (H + 3) / 4; by++)
            for (int bx = 0; bx < bw0; bx++)
            {
                int o = o0 + (by * bw0 + bx) * bs + (dxt5 ? 8 : 0);
                ushort c0 = (ushort)(b0[o] | b0[o + 1] << 8), c1 = (ushort)(b0[o + 2] | b0[o + 3] << 8);
                int L0 = Lum(c0), L1 = Lum(c1);
                var pal = c0 > c1 || dxt5 ? new[] { L0, L1, (2 * L0 + L1) / 3, (L0 + 2 * L1) / 3 } : new[] { L0, L1, (L0 + L1) / 2, 0 };
                uint bits = (uint)(b0[o + 4] | b0[o + 5] << 8 | b0[o + 6] << 16 | b0[o + 7] << 24);
                for (int py = 0; py < 4; py++) for (int px = 0; px < 4; px++)
                    {
                        int X = bx * 4 + px, Y = by * 4 + py; if (X >= W || Y >= H) continue;
                        mask[Y * W + X] = (byte)pal[(bits >> (2 * (py * 4 + px))) & 3];
                    }
            }
        cols = Math.Max(1, cols); rows = Math.Max(1, rows);
        ushort c = (ushort)(((cr >> 3) << 11) | ((cg >> 2) << 5) | (cb >> 3));
        foreach (var (buf, off, w, h) in mips)
        {
            int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    uint idxBits = 0; ulong aBits = 0;
                    for (int py = 0; py < 4; py++) for (int px = 0; px < 4; px++)
                        {
                            // sample the first flipbook frame, stretched over this mip
                            int sx = Math.Clamp((int)(((bx * 4 + px) + 0.5) / w * (W / (double)cols)), 0, W - 1);
                            int sy = Math.Clamp((int)(((by * 4 + py) + 0.5) / h * (H / (double)rows)), 0, H - 1);
                            bool on = mask[sy * W + sx] >= 128; int i = py * 4 + px;
                            if (!dxt5 && !on) idxBits |= 3u << (2 * i);
                            if (dxt5 && !on) aBits |= 1ul << (3 * i);
                        }
                    int o = off + (by * bw + bx) * bs;
                    if (dxt5) { buf[o] = 255; buf[o + 1] = 0; for (int i = 0; i < 6; i++) buf[o + 2 + i] = (byte)(aBits >> (8 * i)); o += 8; }
                    buf[o] = (byte)c; buf[o + 1] = (byte)(c >> 8); buf[o + 2] = (byte)c; buf[o + 3] = (byte)(c >> 8);   // c0 == c1: 3-color + transparent mode
                    buf[o + 4] = (byte)idxBits; buf[o + 5] = (byte)(idxBits >> 8); buf[o + 6] = (byte)(idxBits >> 16); buf[o + 7] = (byte)(idxBits >> 24);
                }
        }
        e.Extras = x; u.Write(uassetPath);
        if (ub.Length > 0) File.WriteAllBytes(ubPath, ub);
        return true;
    }
    /// <summary>True for a black/white mask: (almost) no color, with clearly black and clearly white areas.</summary>
    public static bool IsMaskTexture(string uassetPath)
    {
        try
        {
            var u = new UAsset(uassetPath, EngineVersion.VER_UE5_4);
            var e = u.Exports.OfType<NormalExport>().LastOrDefault(x => x.GetExportClassType()?.Value?.Value == "Texture2D");
            var fmt = PixelFormat(u); bool dxt5 = fmt == "PF_DXT5"; if (e?.Extras == null || (fmt != "PF_DXT1" && !dxt5)) return false;
            // use the smallest mip stored inline or the first one in the .ubulk
            var x = e.Extras; int s = -1;
            for (int i = 0; i + 8 < x.Length && i < 512; i++)
            { int L = I32(x, i); if (L >= 5 && L <= 40 && i + 4 + L <= x.Length && x[i + 4] == 'P' && x[i + 5] == 'F' && x[i + 6] == '_' && x[i + 4 + L - 1] == 0) { s = i; break; } }
            if (s < 12) return false;
            bool hasOpt = (I32(x, s - 4) & 0x40000000) != 0;
            int after = s + 4 + I32(x, s) + (hasOpt ? 8 : 0); int nm = I32(x, after + 4), p = after + 8;
            var ubPath = Path.ChangeExtension(uassetPath, ".ubulk"); byte[] ub = File.Exists(ubPath) ? File.ReadAllBytes(ubPath) : Array.Empty<byte>();
            byte[] buf = null; int off = 0, size = 0;
            for (int k = 0; k < nm; k++)
            {
                int idx = I32(x, p); var r = u.DataResources[idx]; p += 4;
                if ((r.LegacyBulkDataFlags & ForceInline) != 0) { if (buf == null) { buf = x; off = p; size = (int)r.SerialSize; } p += (int)r.SerialSize; }
                else if ((r.LegacyBulkDataFlags & SeparateFile) != 0 && buf == null && r.SerialOffset + r.SerialSize <= ub.Length) { buf = ub; off = (int)r.SerialOffset; size = (int)r.SerialSize; }
                p += 12;
            }
            if (buf == null) return false;
            int bs = dxt5 ? 16 : 8, n = size / bs, colored = 0, dark = 0, bright = 0;
            for (int b = 0; b < n; b++)
            {
                int o = off + b * bs + (dxt5 ? 8 : 0);
                foreach (var c in new[] { (ushort)(buf[o] | buf[o + 1] << 8), (ushort)(buf[o + 2] | buf[o + 3] << 8) })
                {
                    int r8 = (c >> 11) * 255 / 31, g8 = ((c >> 5) & 63) * 255 / 63, b8 = (c & 31) * 255 / 31;
                    if (Math.Max(r8, Math.Max(g8, b8)) - Math.Min(r8, Math.Min(g8, b8)) > 30) colored++;
                    int l = Lum(c); if (l < 40) dark++; else if (l > 200) bright++;
                }
            }
            int total = n * 2;
            return total > 0 && colored < total * 0.03 && dark > total * 0.08 && bright > total * 0.08;
        }
        catch { return false; }
    }

    static int Lum(ushort c) { int r = (c >> 11) * 255 / 31, g = ((c >> 5) & 63) * 255 / 63, b = (c & 31) * 255 / 31; return (r * 3 + g * 6 + b) / 10; }

    /// <returns>new size, or 0 if unchanged</returns>
    public static int Downscale(string uassetPath, int maxSize, Log log)
    {
        var name = Path.GetFileNameWithoutExtension(uassetPath);
        var u = new UAsset(uassetPath, EngineVersion.VER_UE5_4);
        var e = u.Exports.OfType<NormalExport>().LastOrDefault(x => x.GetExportClassType()?.Value?.Value == "Texture2D");
        if (e == null || e.Extras == null || e.Extras.Length < 64) return 0;
        var x = e.Extras;
        // locate "PF_xxx" FString of the platform data
        int s = -1;
        for (int i = 0; i + 8 < x.Length && i < 512; i++)
        {
            int L = I32(x, i);
            if (L >= 5 && L <= 40 && i + 4 + L <= x.Length && x[i + 4] == 'P' && x[i + 5] == 'F' && x[i + 6] == '_' && x[i + 4 + L - 1] == 0) { s = i; break; }
        }
        if (s < 12) { log.Warn($"{name}: texture layout not recognised, left as is"); return 0; }
        int sizeOff = s - 12, sx = I32(x, sizeOff), sy = I32(x, sizeOff + 4), packed = I32(x, sizeOff + 8);
        if (Math.Max(sx, sy) <= maxSize) return 0;
        bool hasOpt = (packed & 0x40000000) != 0, cube = (packed & unchecked((int)0x80000000)) != 0; int slices = packed & 0x3FFFFFFF;
        if (cube || slices != 1) { log.Warn($"{name}: not a plain 2D texture, left as is"); return 0; }
        int after = s + 4 + I32(x, s) + (hasOpt ? 8 : 0);
        int numMipsOff = after + 4, nm = I32(x, numMipsOff), m0 = numMipsOff + 4;
        // skip offset (int64) that points to the end of the platform data
        int skipPos = -1;
        for (int i = sizeOff - 8; i >= 8; i--) if (BitConverter.ToInt64(x, i) == x.Length - (i + 8)) { skipPos = i; break; }
        if (skipPos < 0) { log.Warn($"{name}: could not find platform data size field, left as is"); return 0; }
        var res = u.DataResources;
        // walk mip entries
        var mips = new List<(int start, int end, int idx, int w, int h)>(); int p = m0;
        for (int k = 0; k < nm; k++)
        {
            int idx = I32(x, p); if (idx < 0 || idx >= res.Count) { log.Warn($"{name}: unexpected mip data, left as is"); return 0; }
            var r = res[idx]; int st = p; p += 4;
            if ((r.LegacyBulkDataFlags & Optional) != 0) { log.Warn($"{name}: uses optional bulk data, left as is"); return 0; }
            if ((r.LegacyBulkDataFlags & ForceInline) != 0) p += (int)r.SerialSize;
            else if ((r.LegacyBulkDataFlags & SeparateFile) == 0) { log.Warn($"{name}: mip data stored at end of file, left as is"); return 0; }
            int w = I32(x, p), h = I32(x, p + 4); p += 12;
            mips.Add((st, p, idx, w, h));
        }
        if (p != x.Length - 12) { log.Warn($"{name}: mip table did not end where expected, left as is"); return 0; }
        int drop = mips.TakeWhile(m => Math.Max(m.w, m.h) > maxSize).Count();
        if (drop == 0 || drop >= mips.Count - 1) return 0;
        int removedBytes = mips[drop - 1].end - m0;
        var droppedIdx = new HashSet<int>(mips.Take(drop).Select(m => m.idx));
        var nx = x.Take(m0).Concat(x.Skip(mips[drop - 1].end)).ToArray();
        W32(nx, sizeOff, mips[drop].w); W32(nx, sizeOff + 4, mips[drop].h); W32(nx, numMipsOff, nm - drop);
        BitConverter.GetBytes(BitConverter.ToInt64(x, skipPos) - removedBytes).CopyTo(nx, skipPos);
        // renumber resource indices of remaining mips
        int q = m0;
        for (int k = drop; k < nm; k++)
        {
            int idx = I32(nx, q); W32(nx, q, idx - droppedIdx.Count(d => d < idx)); var r = res[idx]; q += 4;
            if ((r.LegacyBulkDataFlags & ForceInline) != 0) q += (int)r.SerialSize; q += 12;
        }
        // rebuild ubulk without the dropped ranges and fix offsets
        string ubulkPath = Path.ChangeExtension(uassetPath, ".ubulk");
        byte[] ub = File.Exists(ubulkPath) ? File.ReadAllBytes(ubulkPath) : Array.Empty<byte>();
        var removedRanges = droppedIdx.Select(i => res[i]).Where(r => (r.LegacyBulkDataFlags & SeparateFile) != 0).Select(r => (r.SerialOffset, r.SerialSize)).OrderBy(r => r.SerialOffset).ToList();
        var newRes = new List<FObjectDataResource>();
        for (int i = 0; i < res.Count; i++)
        {
            if (droppedIdx.Contains(i)) continue; var r = res[i];
            if ((r.LegacyBulkDataFlags & SeparateFile) != 0) r.SerialOffset -= removedRanges.Where(rr => rr.SerialOffset < r.SerialOffset).Sum(rr => rr.SerialSize);
            else if ((r.LegacyBulkDataFlags & ForceInline) != 0) r.SerialOffset -= removedBytes;
            newRes.Add(r);
        }
        if (removedRanges.Count > 0)
        {
            var nub = new List<byte>(ub.Length); long pos = 0;
            foreach (var (off, size) in removedRanges) { nub.AddRange(ub.Skip((int)pos).Take((int)(off - pos))); pos = off + size; }
            nub.AddRange(ub.Skip((int)pos));
            if (nub.Count == 0) File.Delete(ubulkPath); else File.WriteAllBytes(ubulkPath, nub.ToArray());
        }
        u.DataResources = newRes; e.Extras = nx; u.Write(uassetPath);
        log.Change($"{name}: {sx}x{sy} -> {mips[drop].w}x{mips[drop].h}");
        return mips[drop].w;
    }
}
