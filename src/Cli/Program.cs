using System.Linq;
using System;
using SwitchPorter;
// usage: SwitchPorterCli <modDir> <globalDir> <outDir> <retoc> [maxTex] [namesDb] [--keep]
//        SwitchPorterCli --build-db <paksDir> <namesDb> [aesKey]
//        SwitchPorterCli --hash <string>
if (args[0] == "--locres-roundtrip") { foreach (var f in args.Skip(1)) { var d = System.IO.File.ReadAllBytes(f); var l = LocRes.Read(d); Console.WriteLine($"{(l.Write().SequenceEqual(d) ? "IDENTICAL" : "DIFF")} {f} {l.Entries.Count()} entries"); } return; }
if (args[0] == "--hash") { Console.WriteLine($"pkg {CityHash.PackageId(args[1]):x16}  export {CityHash.ExportHash(args[1]):x16}"); return; }
var log = new Log(); log.OnLine += (l, m) => Console.WriteLine($"{l,-6} {m}");
if (args[0] == "--build-db")
{
    var db = NameDb.BuildFromDump(args[1], args.Length > 3 ? args[3] : null, AppContext.BaseDirectory, log, (d, t) => Console.Write($"\r{d}/{t}   "), default);
    db.Save(args[2]); return;
}
if (args[0] == "--names-only")
{   // --names-only <outDir> <baseText> <key> <name> [--saved k=v,...]
    var on = new Options { OutputDir = args[1], BaseTextPath = args[2], NameKey = args[3], NewName = args[4] };
    int s2 = Array.IndexOf(args, "--saved"); if (s2 >= 0) foreach (var kv in args[s2 + 1].Split(',')) { var p3 = kv.Split('='); on.SavedNames[p3[0]] = p3[1]; }
    var rn = new Converter(on, log).RunNamesOnly(); Console.WriteLine($"success={rn.Success}"); return;
}
var o = new Options { ModDir = args[0], GlobalDir = args[1], OutputDir = args[2], RetocPath = args[3] };
if (args.Length > 4) o.MaxTextureSize = int.Parse(args[4]);
if (args.Length > 5 && args[5] != "-") o.NameDbPath = args[5];
o.KeepWorkDir = Array.IndexOf(args, "--keep") >= 0;
int ti = Array.IndexOf(args, "--transparency"); if (ti >= 0) o.Transparency = Enum.Parse<TransparencyMode>(args[ti + 1], true);
int ni = Array.IndexOf(args, "--name"); if (ni >= 0) o.NewName = args[ni + 1];
int ki = Array.IndexOf(args, "--name-key"); if (ki >= 0) o.NameKey = args[ki + 1];
int bi = Array.IndexOf(args, "--base-text"); if (bi >= 0) o.BaseTextPath = args[bi + 1];
int si = Array.IndexOf(args, "--saved"); if (si >= 0) foreach (var kv in args[si + 1].Split(',')) { var p2 = kv.Split('='); o.SavedNames[p2[0]] = p2[1]; }
if (o.KeepWorkDir) o.WorkDir = System.IO.Path.Combine(o.OutputDir, "_work");
var r = new Converter(o, log).Run();
Console.WriteLine($"success={r.Success} warnings={log.Warnings} errors={log.Errors}");
