using System;
using System.Text;
namespace SwitchPorter;

/// <summary>CityHash64 (v1.1) as used by Unreal for package ids and public export hashes.</summary>
public static class CityHash
{
    const ulong k0 = 0xc3a5c85c97cb3127UL, k1 = 0xb492b66fbe98f273UL, k2 = 0x9ae16a3b2f90404fUL, kMul = 0x9ddfea08eb382d69UL;
    static ulong F64(byte[] s, int p) => BitConverter.ToUInt64(s, p);
    static ulong F32(byte[] s, int p) => BitConverter.ToUInt32(s, p);
    static ulong Rot(ulong v, int s) => s == 0 ? v : (v >> s) | (v << (64 - s));
    static ulong SM(ulong v) => v ^ (v >> 47);
    static ulong Swap(ulong v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    static ulong H16(ulong u, ulong v) { ulong a = (u ^ v) * kMul; a ^= a >> 47; ulong b = (v ^ a) * kMul; b ^= b >> 47; return b * kMul; }
    static ulong H16(ulong u, ulong v, ulong mul) { ulong a = (u ^ v) * mul; a ^= a >> 47; ulong b = (v ^ a) * mul; b ^= b >> 47; return b * mul; }
    static ulong L0to16(byte[] s, int o, int len)
    {
        if (len >= 8) { ulong mul = k2 + (ulong)len * 2, a = F64(s, o) + k2, b = F64(s, o + len - 8), c = Rot(b, 37) * mul + a, d = (Rot(a, 25) + b) * mul; return H16(c, d, mul); }
        if (len >= 4) { ulong mul = k2 + (ulong)len * 2, a = F32(s, o); return H16((ulong)len + (a << 3), F32(s, o + len - 4), mul); }
        if (len > 0) { byte a = s[o], b = s[o + (len >> 1)], c = s[o + len - 1]; uint y = a + ((uint)b << 8); uint z = (uint)len + ((uint)c << 2); return SM(y * k2 ^ z * k0) * k2; }
        return k2;
    }
    static ulong L17to32(byte[] s, int o, int len)
    {
        ulong mul = k2 + (ulong)len * 2, a = F64(s, o) * k1, b = F64(s, o + 8), c = F64(s, o + len - 8) * mul, d = F64(s, o + len - 16) * k2;
        return H16(Rot(a + b, 43) + Rot(c, 30) + d, a + Rot(b + k2, 18) + c, mul);
    }
    static ulong L33to64(byte[] s, int o, int len)
    {
        ulong mul = k2 + (ulong)len * 2, a = F64(s, o) * k2, b = F64(s, o + 8), c = F64(s, o + len - 24), d = F64(s, o + len - 32),
              e = F64(s, o + 16) * k2, f = F64(s, o + 24) * 9, g = F64(s, o + len - 8), h = F64(s, o + len - 16) * mul;
        ulong u = Rot(a + g, 43) + (Rot(b, 30) + c) * 9, v = ((a + g) ^ d) + f + 1, w = Swap((u + v) * mul) + h, x = Rot(e + f, 42) + c,
              y = (Swap((v + w) * mul) + g) * mul, z = e + f + c;
        a = Swap((x + z) * mul + y) + b; b = SM((z + a) * mul + d + h) * mul; return b + x;
    }
    static (ulong, ulong) Weak(ulong w, ulong x, ulong y, ulong z, ulong a, ulong b)
    { a += w; b = Rot(b + a + z, 21); ulong c = a; a += x; a += y; b += Rot(a, 44); return (a + z, b + c); }
    static (ulong, ulong) Weak(byte[] s, int o, ulong a, ulong b) => Weak(F64(s, o), F64(s, o + 8), F64(s, o + 16), F64(s, o + 24), a, b);

    public static ulong Hash64(byte[] s)
    {
        int len = s.Length, o = 0;
        if (len <= 32) return len <= 16 ? L0to16(s, 0, len) : L17to32(s, 0, len);
        if (len <= 64) return L33to64(s, 0, len);
        ulong x = F64(s, len - 40), y = F64(s, len - 16) + F64(s, len - 56), z = H16(F64(s, len - 48) + (ulong)len, F64(s, len - 24));
        var v = Weak(s, len - 64, (ulong)len, z); var w = Weak(s, len - 32, y + k1, x);
        x = x * k1 + F64(s, 0);
        int n = (len - 1) & ~63;
        do
        {
            x = Rot(x + y + v.Item1 + F64(s, o + 8), 37) * k1; y = Rot(y + v.Item2 + F64(s, o + 48), 42) * k1;
            x ^= w.Item2; y += v.Item1 + F64(s, o + 40); z = Rot(z + w.Item1, 33) * k1;
            v = Weak(s, o, v.Item2 * k1, x + w.Item1); w = Weak(s, o + 32, z + w.Item2, y + F64(s, o + 16));
            (z, x) = (x, z); o += 64; n -= 64;
        } while (n != 0);
        return H16(H16(v.Item1, w.Item1) + SM(y) * k1 + z, H16(v.Item2, w.Item2) + x);
    }

    static ulong LowerUtf16(string s) => Hash64(Encoding.Unicode.GetBytes(s.ToLowerInvariant()));
    /// <summary>FPackageId::FromName</summary>
    public static ulong PackageId(string packageName) => LowerUtf16(packageName);
    /// <summary>Public export hash; relativePath is e.g. "M_Foo" or "M_Foo/SubObject"</summary>
    public static ulong ExportHash(string relativePath) => LowerUtf16(relativePath);
}
