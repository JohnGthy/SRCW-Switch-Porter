using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
namespace SwitchPorter;

/// <summary>Writes an uncompressed, unencrypted legacy .pak (version 3), the format UnrealPak from the Switch tutorial produces.</summary>
public static class PakWriter
{
    const uint Magic = 0x5A6F12E1;
    static void WriteFString(BinaryWriter w, string s)
    {
        bool ascii = s.All(c => c < 128);
        if (ascii) { var b = Encoding.ASCII.GetBytes(s + "\0"); w.Write(b.Length); w.Write(b); }
        else { var b = Encoding.Unicode.GetBytes(s + "\0"); w.Write(-(b.Length / 2)); w.Write(b); }
    }
    static void WriteEntry(BinaryWriter w, long offset, long size, byte[] sha1)
    {
        w.Write(offset); w.Write(size); w.Write(size); w.Write(0); // compression none
        w.Write(sha1); w.Write((byte)0); w.Write(0u);              // not encrypted, block size 0
    }
    /// <param name="files">relative path (e.g. "UNION/Content/...uasset") -> bytes on disk</param>
    public static void Write(string pakPath, IDictionary<string, string> files, string mountPoint = "../../../")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pakPath));
        using var fs = File.Create(pakPath); using var w = new BinaryWriter(fs);
        var index = new List<(string name, long off, long size, byte[] hash)>();
        foreach (var kv in files.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var data = File.ReadAllBytes(kv.Value); var hash = SHA1.HashData(data); long off = fs.Position;
            WriteEntry(w, 0, data.Length, hash); w.Write(data);
            index.Add((kv.Key.Replace('\\', '/'), off, data.Length, hash));
        }
        long indexOff = fs.Position;
        using var ims = new MemoryStream(); using (var iw = new BinaryWriter(ims, Encoding.UTF8, true))
        {
            WriteFString(iw, mountPoint); iw.Write(index.Count);
            foreach (var e in index) { WriteFString(iw, e.name); WriteEntry(iw, e.off, e.size, e.hash); }
        }
        var idx = ims.ToArray(); w.Write(idx);
        w.Write(Magic); w.Write(3); w.Write(indexOff); w.Write((long)idx.Length); w.Write(SHA1.HashData(idx));
    }
}
