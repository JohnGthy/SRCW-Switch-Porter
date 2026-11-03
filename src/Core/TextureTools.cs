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
