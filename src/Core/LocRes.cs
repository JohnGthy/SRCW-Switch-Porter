using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
namespace SwitchPorter;

/// <summary>Reads/writes Unreal .locres files (version 2 and 3, "optimized" formats) without touching their hashes.</summary>
public class LocRes
{
    public class Entry { public string Namespace, Key; public uint NsHash, KeyHash, SourceHash; public int StringIndex; }
    byte[] magic; byte version;
    public List<(string ns, uint hash, List<Entry> keys)> Namespaces = new();
    public List<string> Strings = new(); public List<int> RefCounts = new();

    static string ReadFString(BinaryReader r)
    {
        int n = r.ReadInt32(); if (n == 0) return "";
        if (n < 0) { var b = r.ReadBytes(-n * 2); return Encoding.Unicode.GetString(b, 0, b.Length - 2); }
        var a = r.ReadBytes(n); return Encoding.Latin1.GetString(a, 0, a.Length - 1);
    }
    static void WriteFString(BinaryWriter w, string s)
    {
        if (s.Length == 0) { w.Write(0); return; }
        if (s.All(c => c < 128)) { w.Write(s.Length + 1); w.Write(Encoding.ASCII.GetBytes(s)); w.Write((byte)0); }
        else { w.Write(-(s.Length + 1)); w.Write(Encoding.Unicode.GetBytes(s)); w.Write((short)0); }
    }

    public static LocRes Read(byte[] data)
    {
        var l = new LocRes(); using var r = new BinaryReader(new MemoryStream(data));
        l.magic = r.ReadBytes(16); l.version = r.ReadByte();
        if (l.version < 2 || l.version > 3) throw new Exception($"Unsupported text file version {l.version}");
        long strOff = r.ReadInt64(); r.ReadUInt32(); // entry count
        uint nns = r.ReadUInt32();
        for (int i = 0; i < nns; i++)
        {
            uint nh = r.ReadUInt32(); var ns = ReadFString(r); uint nk = r.ReadUInt32(); var keys = new List<Entry>();
            for (int k = 0; k < nk; k++)
            {
                uint kh = r.ReadUInt32(); var key = ReadFString(r);
                keys.Add(new Entry { Namespace = ns, Key = key, NsHash = nh, KeyHash = kh, SourceHash = r.ReadUInt32(), StringIndex = r.ReadInt32() });
            }
            l.Namespaces.Add((ns, nh, keys));
        }
        r.BaseStream.Position = strOff; int cnt = r.ReadInt32();
        for (int i = 0; i < cnt; i++) { l.Strings.Add(ReadFString(r)); l.RefCounts.Add(r.ReadInt32()); }
        return l;
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(magic); w.Write(version); long offPos = ms.Position; w.Write(0L);
        w.Write((uint)Namespaces.Sum(n => n.keys.Count)); w.Write((uint)Namespaces.Count);
        foreach (var (ns, h, keys) in Namespaces)
        {
            w.Write(h); WriteFString(w, ns); w.Write((uint)keys.Count);
            foreach (var e in keys) { w.Write(e.KeyHash); WriteFString(w, e.Key); w.Write(e.SourceHash); w.Write(e.StringIndex); }
        }
        long strOff = ms.Position; w.Write(Strings.Count);
        for (int i = 0; i < Strings.Count; i++) { WriteFString(w, Strings[i]); w.Write(RefCounts[i]); }
        ms.Position = offPos; w.Write(strOff);
        return ms.ToArray();
    }

    public IEnumerable<Entry> Entries => Namespaces.SelectMany(n => n.keys);
    public Entry Find(string ns, string key) => Namespaces.FirstOrDefault(n => n.ns == ns).keys?.FirstOrDefault(e => e.Key == key);
    public string Get(string ns, string key) { var e = Find(ns, key); return e == null ? null : Strings[e.StringIndex]; }

    /// <summary>Changes one entry's text (other entries sharing the same string are not affected).</summary>
    public bool Set(string ns, string key, string text)
    {
        var e = Find(ns, key); if (e == null) return false;
        if (Strings[e.StringIndex] == text) return true;
        RefCounts[e.StringIndex] = Math.Max(0, RefCounts[e.StringIndex] - 1);
        int i = Strings.IndexOf(text);
        if (i >= 0) RefCounts[i]++; else { Strings.Add(text); RefCounts.Add(1); i = Strings.Count - 1; }
        e.StringIndex = i; return true;
    }
}
