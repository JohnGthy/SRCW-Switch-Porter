using System;
using System.Collections.Generic;
using System.Text;
namespace SwitchPorter;

public enum Level { Info, Change, Warn, Error }

public class Log
{
    public event Action<Level, string> OnLine;
    public readonly List<(Level, string)> Lines = new();
    public int Warnings, Errors;
    public void Info(string m) => Add(Level.Info, m);
    public void Change(string m) => Add(Level.Change, m);
    public void Warn(string m) { Warnings++; Add(Level.Warn, m); }
    public void Error(string m) { Errors++; Add(Level.Error, m); }
    void Add(Level l, string m) { lock (Lines) Lines.Add((l, m)); OnLine?.Invoke(l, m); }
    public string ToText()
    {
        var sb = new StringBuilder();
        lock (Lines) foreach (var (l, m) in Lines) sb.AppendLine((l switch { Level.Change => "[changed] ", Level.Warn => "[WARNING] ", Level.Error => "[ERROR]   ", _ => "          " }) + m);
        return sb.ToString();
    }
}
