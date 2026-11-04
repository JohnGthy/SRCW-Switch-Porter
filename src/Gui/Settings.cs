using System;
using System.IO;
using System.Text.Json;
namespace SwitchPorter.Gui;
public class Settings
{
    public string GlobalDir { get; set; }
    public string LastModDir { get; set; }
    public string DumpDir { get; set; }
    public string AesKey { get; set; }
    public int MaxTexture { get; set; } = 1024;
    public string BaseTextPath { get; set; }
    public int Transparency { get; set; } = 0;
    public bool IncludeSharedBlueprints { get; set; }
    public System.Collections.Generic.Dictionary<string, string> Names { get; set; } = new();
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SRCW Switch Porter");
    public static string FilePath => Path.Combine(Dir, "settings.json");
    public static string NamesPath => Path.Combine(Dir, "game names.json");
    public static string BaseTextDir => Path.Combine(Dir, "game text");
    public static Settings Load() { try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); } catch { return new(); } }
    public void Save() { try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); } catch { } }
}
