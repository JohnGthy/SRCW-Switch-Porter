using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
namespace SwitchPorter;

/// <summary>
/// CRI ADX2 sound bank (.acb) tools. An .acb is a tree of "@UTF" tables (big-endian): column definitions, rows,
/// a string pool and a data pool that can hold nested tables. <see cref="Rebuild"/> rewrites every string and keeps
/// everything else (an unchanged rebuild is byte-identical to the original).
/// </summary>
public static class AcbTools
{
    static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
    static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));
    static void W32(List<byte> l, int o, uint v) { l[o] = (byte)(v >> 24); l[o + 1] = (byte)(v >> 16); l[o + 2] = (byte)(v >> 8); l[o + 3] = (byte)v; }
    static int LowBit(long x, int cap = 32) => x == 0 ? cap : (int)Math.Min(cap, x & -x);

    public static byte[] Rebuild(byte[] d, int off, Func<string, string> fn)
    {
        if (Encoding.ASCII.GetString(d, off, 4) != "@UTF") throw new InvalidDataException("not an @UTF table");
        int size = (int)U32(d, off + 4), b = off + 8, end = b + size;
        int rowsOff = U16(d, b + 2); int strOff = (int)U32(d, b + 4), dataOff = (int)U32(d, b + 8), nameOff = (int)U32(d, b + 12);
        int ncol = U16(d, b + 16), rowLen = U16(d, b + 18); int nrows = (int)U32(d, b + 20);
        bool hasData = dataOff > strOff;
        int poolStart = b + strOff, poolEnd = hasData ? b + dataOff : end;

        // string pool, split at zero bytes (padding zeros become empty strings so offsets into them stay valid)
        var strs = new List<(int off, string s)>();
        for (int o = 0; o < poolEnd - poolStart;)
        {
            int e = Array.IndexOf(d, (byte)0, poolStart + o, poolEnd - poolStart - o); if (e < 0) e = poolEnd;
            strs.Add((o, Encoding.UTF8.GetString(d, poolStart + o, e - poolStart - o))); o = e - poolStart + 1;
        }
        var newPool = new List<byte>(); var smap = new Dictionary<int, int>();
        foreach (var (o, s) in strs) { smap[o] = newPool.Count; newPool.AddRange(Encoding.UTF8.GetBytes(fn(s))); newPool.Add(0); }
        int StrRef(int o0)
        {
            if (smap.TryGetValue(o0, out var n)) return n;
            foreach (var (so, s) in strs)   // reference into the middle of a string (shared suffix)
            {
                int len = Encoding.UTF8.GetByteCount(s);
                if (so < o0 && o0 <= so + len)
                {
                    var suf = Encoding.UTF8.GetString(d, poolStart + o0, so + len - o0);
                    n = newPool.Count; newPool.AddRange(Encoding.UTF8.GetBytes(fn(suf))); newPool.Add(0); smap[o0] = n; return n;
                }
            }
            throw new InvalidDataException($"bad string offset {o0}");
        }

        var head = d.Skip(b).Take(rowsOff).ToList();
        var rows = d.Skip(b + rowsOff).Take(rowLen * nrows).ToList();
        var gap = d.Skip(b + rowsOff + rowLen * nrows).Take(strOff - rowsOff - rowLen * nrows).ToArray();
        var patches = new List<(List<byte> buf, int pos, char kind)>();
        int Val(List<byte> buf, int p, int ty)
        {
            switch (ty)
            {
                case 0: case 1: return p + 1;
                case 2: case 3: return p + 2;
                case 4: case 5: case 8: return p + 4;
                case 6: case 7: return p + 8;
                case 0xA: patches.Add((buf, p, 's')); return p + 4;
                case 0xB: patches.Add((buf, p, 'd')); return p + 8;
                default: throw new InvalidDataException($"unknown column type {ty:x}");
            }
        }
        int pp = 24; var cols = new List<(int st, int ty)>();
        for (int c = 0; c < ncol; c++)
        {
            int fl = head[pp]; pp++; patches.Add((head, pp, 's')); pp += 4;
            int st = fl & 0xF0, ty = fl & 0x0F;
            if (st == 0x30) pp = Val(head, pp, ty);
            cols.Add((st, ty));
        }
        for (int r = 0; r < nrows; r++)
        {
            int q = r * rowLen;
            foreach (var (st, ty) in cols) if (st == 0x50) q = Val(rows, q, ty);
        }
        uint R32(List<byte> l, int o) => (uint)(l[o] << 24 | l[o + 1] << 16 | l[o + 2] << 8 | l[o + 3]);

        // data pool: nested tables rebuilt, blobs kept, original order and alignment
        int dataStart = b + dataOff;
        var items = patches.Where(x => x.kind == 'd').Select(x => (o: (int)R32(x.buf, x.pos), l: (int)R32(x.buf, x.pos + 4))).Where(x => x.l > 0).Distinct().OrderBy(x => x.o).ToList();
        int balign = 32; foreach (var (o, _) in items) if (o != 0) balign = Math.Min(balign, LowBit(o));
        var newData = new List<byte>(); var dmap = new Dictionary<(int, int), (int, int)>();
        foreach (var (o, l) in items)
        {
            while (newData.Count % balign != 0) newData.Add(0);
            var blob = d.Skip(dataStart + o).Take(l).ToArray();
            if (blob.Length >= 4 && Encoding.ASCII.GetString(blob, 0, 4) == "@UTF") blob = Rebuild(blob, 0, fn);
            dmap[(o, l)] = (newData.Count, blob.Length); newData.AddRange(blob);
        }
        int newName = StrRef(nameOff);
        foreach (var (buf, pos, kind) in patches)
        {
            if (kind == 's') W32(buf, pos, (uint)StrRef((int)R32(buf, pos)));
            else
            {
                int o = (int)R32(buf, pos), l = (int)R32(buf, pos + 4);
                if (l > 0) { var (no, nl) = dmap[(o, l)]; W32(buf, pos, (uint)no); W32(buf, pos + 4, (uint)nl); }
            }
        }
        W32(head, 12, (uint)newName);
        var body = new List<byte>(head); body.AddRange(rows); body.AddRange(gap);
        int newStr = body.Count; body.AddRange(newPool);
        int newDataOff;
        if (hasData)
        {
            int dalign = LowBit(8 + dataOff);
            while ((8 + body.Count) % dalign != 0) body.Add(0);
            newDataOff = body.Count; body.AddRange(newData);
        }
        else newDataOff = body.Count;
        int ealign = LowBit(8 + size);
        while ((8 + body.Count) % ealign != 0) body.Add(0);
        W32(body, 4, (uint)newStr); W32(body, 8, (uint)newDataOff);
        var res = new List<byte>(Encoding.ASCII.GetBytes("@UTF")); res.AddRange(new byte[4]); W32(res, 4, (uint)body.Count); res.AddRange(body);
        return res.ToArray();
    }

    static readonly Regex RxVoiceBank = new(@"^VOICE_([A-Z]+?)(AI)?$");

    /// <summary>
    /// For a main-cast voice bank name (VOICE_EGG / VOICE_EGGAI), the bank name for the other slot, or null.
    /// </summary>
    public static string OtherSlotBank(string bank, bool toAlt)
    {
        var m = RxVoiceBank.Match(bank); if (!m.Success) return null;
        bool isAlt = m.Groups[2].Success;
        if (toAlt == isAlt) return null;
        return toAlt ? bank + "AI" : bank[..^2];
    }

    /// <summary>
    /// Rewrites a voice bank for the other character slot: the bank name (VOICE_EGG -> VOICE_EGGAI) and every cue name
    /// (voice_egg_boost_1 -> voice_eggAI_boost_1). The audio itself (.awb) is the same file under the new name.
    /// </summary>
    public static byte[] RetargetVoiceBank(byte[] acb, string fromBank, string toBank)
    {
        string fromAbbr = fromBank["VOICE_".Length..], toAbbr = toBank["VOICE_".Length..];
        // cue names use the abbreviation in lower case followed by "AI" for the alternate slot: voice_eggAI_...
        string Cue(string a) => a.EndsWith("AI") ? a[..^2].ToLowerInvariant() + "AI" : a.ToLowerInvariant();
        var rx = new Regex("(?<=voice_)" + Regex.Escape(Cue(fromAbbr)) + "(?=_)");
        string to = Cue(toAbbr);
        return Rebuild(acb, 0, s => s == fromBank ? toBank : rx.Replace(s, to));
    }
}
