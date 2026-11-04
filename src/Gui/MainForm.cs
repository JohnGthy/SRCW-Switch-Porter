using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace SwitchPorter.Gui;

public class MainForm : Form
{
    readonly Settings s = Settings.Load();
    readonly TextBox tMod = new(), tGlobal = new(), tOut = new(), tDump = new(), tAes = new(), tName = new() { PlaceholderText = "leave empty to keep the game's name" }, tBaseText = new();
    readonly ComboBox cSlot = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Label lNames = new() { AutoSize = true, ForeColor = Color.DimGray };
    readonly LinkLabel lClear = new() { Text = "Clear saved names", AutoSize = true };
    readonly ComboBox cTex = new() { DropDownStyle = ComboBoxStyle.DropDownList }, cAlpha = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Button bConvert = new() { Text = "Convert for Switch", Height = 36 }, bOpen = new() { Text = "Open output folder", Enabled = false, Height = 36 },
                    bBuild = new() { Text = "Build database", AutoSize = true }, bCancel = new() { Text = "Cancel", Enabled = false, Height = 36 },
                    bNames = new() { Text = "Make names pak only", Height = 36 };
    readonly Label lOpts = new() { AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3), MaximumSize = new Size(560, 0) };
    readonly Button bOpts = new() { Text = "Choose...", AutoSize = true, Enabled = false };
    ModOptions mopts;
    readonly CheckBox chkShared = new() { Text = "Include shared game blueprints (risky: can break other characters)", AutoSize = true };
    readonly Label lDb = new() { AutoSize = true, ForeColor = Color.DimGray };
    readonly RichTextBox log = new() { ReadOnly = true, BackColor = Color.White, Font = new Font("Consolas", 9f), WordWrap = true, BorderStyle = BorderStyle.FixedSingle };
    readonly ProgressBar bar = new() { Style = ProgressBarStyle.Continuous, Height = 8 };
    CancellationTokenSource cts; string lastOutput;

    static string AppDir => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    static string Retoc => Path.Combine(AppDir, "bin", "retoc.exe");

    public MainForm()
    {
        Text = "SRCW Switch Porter"; Width = 900; Height = 900; MinimumSize = new Size(760, 600); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f); AllowDrop = true;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14), AutoSize = false };
        Controls.Add(root);

        var title = new Label { Text = "Port Sonic Racing CrossWorlds PC skin mods to Nintendo Switch", AutoSize = true, Font = new Font("Segoe UI Semibold", 12f), Margin = new Padding(0, 0, 0, 2) };
        var sub = new Label { Text = "Drop a mod folder on this window, or pick it below. Your PC mod files are never changed.", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 10) };
        root.Controls.Add(title); root.Controls.Add(sub);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(grid, "PC mod folder", tMod, () => PickFolder(tMod, "Folder containing the PC mod (.utoc / .ucas / .pak)"));
        AddRow(grid, "global.utoc / .ucas folder", tGlobal, () => PickFolder(tGlobal, "Folder that contains global.utoc and global.ucas"));
        AddRow(grid, "Output folder", tOut, () => PickFolder(tOut, "Where to write the Switch files"));
        grid.Controls.Add(new Label { Text = "Mod parts / outfits", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3) });
        grid.Controls.Add(lOpts); grid.Controls.Add(bOpts);
        grid.Controls.Add(new Label { Text = "Max texture size", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3) });
        cTex.Items.AddRange(new object[] { "Keep original", "2048", "1024 (recommended)", "512 (smallest, least stutter)" }); cTex.Width = 230;
        grid.Controls.Add(cTex); grid.Controls.Add(new Label());
        grid.Controls.Add(new Label { Text = "Transparency material", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3) });
        cAlpha.Items.AddRange(new object[] { "Auto (recommended)", "Never - always the normal material", "Always - transparency material for textures with alpha" }); cAlpha.Width = 380;
        grid.Controls.Add(cAlpha); grid.Controls.Add(new Label());
        grid.Controls.Add(new Label()); grid.Controls.Add(chkShared); grid.Controls.Add(new Label());
        root.Controls.Add(grid);

        var ng = new GroupBox { Text = "Character name (optional)", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10), Margin = new Padding(3, 10, 3, 0) };
        var ngt = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        ngt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); ngt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); ngt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ngt.Controls.Add(new Label { Text = "New name", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3) });
        tName.Dock = DockStyle.Fill; tName.Margin = new Padding(3, 5, 3, 3); ngt.Controls.Add(tName); ngt.Controls.Add(new Label());
        ngt.Controls.Add(new Label { Text = "Name slot", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3) });
        cSlot.Dock = DockStyle.Fill; cSlot.Items.Add("Auto-detect from the mod"); cSlot.SelectedIndex = 0; ngt.Controls.Add(cSlot); ngt.Controls.Add(new Label());
        AddRow(ngt, "Base game text", tBaseText, () => PickFile(tBaseText, "Text pak (*.pak)|*.pak|All files|*.*"));
        var nh = new Label { AutoSize = true, MaximumSize = new Size(820, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 2, 3, 2),
            Text = "Best: build the name database from your Switch dump; it saves your game's own text. Text from a PC text pak is older than your Switch game, so newer characters will show code names like chara18001. To rename characters of a text mod, pick the text mod's .pak here, choose a name slot and press \"Make names pak only\" (no skin mod needed). Every name you set is kept and written into one SwitchPorter_Names_999_P.pak." };
        ngt.Controls.Add(nh); ngt.SetColumnSpan(nh, 3);
        var nrow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) }; nrow.Controls.Add(lNames); nrow.Controls.Add(lClear);
        ngt.Controls.Add(nrow); ngt.SetColumnSpan(nrow, 3);
        ng.Controls.Add(ngt); root.Controls.Add(ng);

        var db = new GroupBox { Text = "Game name database (optional)", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10), Margin = new Padding(3, 10, 3, 6) };
        var dbg = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        dbg.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); dbg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); dbg.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var info = new Label { AutoSize = true, MaximumSize = new Size(820, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 0, 3, 6),
            Text = "Without it, references to the game's own assets are found by guessing names (works for most mods). Build it once from your Switch dump's UNION\\Content\\Paks folder and every conversion resolves exactly. Only file indexes are read; nothing is extracted." };
        dbg.Controls.Add(info, 0, 0); dbg.SetColumnSpan(info, 3);
        AddRow(dbg, "Switch dump Paks folder", tDump, () => PickFolder(tDump, "Your Switch dump's UNION\\Content\\Paks folder"));
        dbg.Controls.Add(new Label { Text = "AES key (only if needed)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3) });
        tAes.Dock = DockStyle.Fill; dbg.Controls.Add(tAes); dbg.Controls.Add(bBuild);
        dbg.Controls.Add(lDb, 0, 3); dbg.SetColumnSpan(lDb, 3);
        db.Controls.Add(dbg); root.Controls.Add(db);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
        bConvert.Width = 190; bOpen.Width = 170; bCancel.Width = 100; bNames.Width = 180;
        bConvert.Font = new Font("Segoe UI Semibold", 10f);
        buttons.Controls.AddRange(new Control[] { bConvert, bNames, bCancel, bOpen }); root.Controls.Add(buttons);
        bar.Dock = DockStyle.Top; root.Controls.Add(bar);
        log.Dock = DockStyle.Fill; root.RowStyles.Clear();
        for (int i = 0; i < 7; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.Controls.Add(log);

        // state
        tGlobal.Text = s.GlobalDir ?? ""; tDump.Text = s.DumpDir ?? ""; tAes.Text = s.AesKey ?? ""; tBaseText.Text = s.BaseTextPath ?? "";
        RefreshNames();
        lClear.LinkClicked += (_, _) =>
        {
            if (MessageBox.Show(this, "Forget all saved character names? The next names pak will only contain names you set from now on.", Text, MessageBoxButtons.YesNo) == DialogResult.Yes)
            { s.Names.Clear(); s.Save(); RefreshNames(); }
        };
        cSlot.DropDown += (_, _) => FillSlots();
        cAlpha.SelectedIndex = Math.Clamp(s.Transparency, 0, 2); chkShared.Checked = s.IncludeSharedBlueprints;
        cTex.SelectedIndex = s.MaxTexture switch { 0 => 0, 2048 => 1, 512 => 3, _ => 2 };
        if (!string.IsNullOrEmpty(s.LastModDir) && Directory.Exists(s.LastModDir)) SetMod(s.LastModDir);
        tMod.TextChanged += (_, _) => { if (Directory.Exists(tMod.Text) && string.IsNullOrWhiteSpace(tOut.Text)) tOut.Text = Path.Combine(tMod.Text, "Switch port"); LoadOpts(); };
        RefreshDbLabel();

        bOpts.Click += (_, _) => { if (mopts != null && new OptionsDialog(mopts).ShowDialog(this) == DialogResult.OK) RefreshOpts(); };
        bConvert.Click += async (_, _) => await Convert();
        bNames.Click += async (_, _) => await NamesOnly();
        bCancel.Click += (_, _) => cts?.Cancel();
        bOpen.Click += (_, _) => { if (lastOutput != null && Directory.Exists(lastOutput)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{lastOutput}\"") { UseShellExecute = true }); };
        bBuild.Click += async (_, _) => await BuildDb();
        DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) =>
        {
            var p = ((string[])e.Data.GetData(DataFormats.FileDrop)).FirstOrDefault(); if (p == null) return;
            if (File.Exists(p)) p = Path.GetDirectoryName(p);
            if (Directory.GetFiles(p, "global.utoc").Length > 0 && Directory.GetFiles(p, "*.utoc").Length == 1) tGlobal.Text = p; else SetMod(p);
        };
        FormClosing += (_, _) => SaveSettings();
        Line(Level.Info, "Ready. 1) Pick the PC mod folder  2) Pick the folder with global.utoc + global.ucas  3) Convert.");
        if (!File.Exists(Retoc)) Line(Level.Error, $"bin\\retoc.exe is missing next to the program ({Retoc}). Re-extract the zip.");
    }

    void SetMod(string p) { tMod.Text = p; tOut.Text = Path.Combine(p, "Switch port"); slotsLoadedFor = null; LoadOpts(); }

    void LoadOpts()
    {
        mopts = null;
        try { if (Directory.Exists(tMod.Text)) mopts = ModOptions.Load(tMod.Text); } catch { mopts = null; }
        RefreshOpts();
    }

    void RefreshOpts()
    {
        bOpts.Enabled = mopts != null && mopts.Groups.Count > 0;
        lOpts.Text = mopts == null || mopts.Groups.Count == 0 ? "(nothing to choose)"
                   : (mopts.FromIni ? "From the mod's options: " : "Enabled parts: ") + mopts.Describe();
    }

    void PickFile(TextBox t, string filter)
    {
        using var d = new OpenFileDialog { Filter = filter };
        if (d.ShowDialog(this) == DialogResult.OK) { t.Text = d.FileName; slotsLoadedFor = null; }
    }

    void RefreshNames()
    {
        lNames.Text = s.Names.Count == 0 ? "Saved names: none" : "Saved names: " + string.Join(", ", s.Names.Select(kv => $"{kv.Key} = {kv.Value}"));
        lClear.Visible = s.Names.Count > 0;
    }

    string slotsLoadedFor;
    void FillSlots()
    {
        // base text: the mod's own text pak, a picked one, or the text saved from the dump
        var modText = Directory.Exists(tMod.Text) ? Directory.GetFiles(tMod.Text, "*.pak", SearchOption.AllDirectories).FirstOrDefault(p => !File.Exists(Path.ChangeExtension(p, ".utoc")) && Path.GetFileName(p).Contains("Text", StringComparison.OrdinalIgnoreCase)) : null;
        var src = new[] { modText, tBaseText.Text, Settings.BaseTextDir }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && (File.Exists(x) || Directory.Exists(x)));
        if (src == null || src == slotsLoadedFor) return;
        try
        {
            var work = Path.Combine(Path.GetTempPath(), "SwitchPorter", "slots"); Directory.CreateDirectory(work);
            var t = Naming.LoadBase(src, work); if (!t.TryGetValue("en", out var en)) return;
            var sel = cSlot.SelectedItem as string; cSlot.Items.Clear(); cSlot.Items.Add("Auto-detect from the mod");
            foreach (var (k, v) in Naming.Slots(LocRes.Read(en))) cSlot.Items.Add($"{k}  —  {v}");
            cSlot.SelectedItem = sel != null && cSlot.Items.Contains(sel) ? sel : cSlot.Items[0];
            slotsLoadedFor = src;
        }
        catch (Exception ex) { Line(Level.Warn, "Could not read name slots: " + ex.Message); }
    }

    static void AddRow(TableLayoutPanel g, string label, TextBox t, Action browse)
    {
        g.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3) });
        t.Dock = DockStyle.Fill; t.Margin = new Padding(3, 5, 3, 3); g.Controls.Add(t);
        var b = new Button { Text = "Browse...", AutoSize = true }; b.Click += (_, _) => browse(); g.Controls.Add(b);
    }

    void PickFolder(TextBox t, string desc)
    {
        using var d = new FolderBrowserDialog { Description = desc, UseDescriptionForTitle = true, SelectedPath = Directory.Exists(t.Text) ? t.Text : "" };
        if (d.ShowDialog(this) == DialogResult.OK) { if (t == tMod) SetMod(d.SelectedPath); else t.Text = d.SelectedPath; }
    }

    int MaxTex => cTex.SelectedIndex switch { 0 => 0, 1 => 2048, 3 => 512, _ => 1024 };

    void SaveSettings() { s.GlobalDir = tGlobal.Text; s.DumpDir = tDump.Text; s.AesKey = tAes.Text; s.MaxTexture = MaxTex; s.LastModDir = tMod.Text; s.BaseTextPath = tBaseText.Text; s.Transparency = cAlpha.SelectedIndex; s.IncludeSharedBlueprints = chkShared.Checked; s.Save(); }

    void RefreshDbLabel()
    {
        try
        {
            var db = File.Exists(Settings.NamesPath) ? NameDb.Load(Settings.NamesPath) : null;
            lDb.Text = db == null ? "Status: not built - conversions use name guessing." : $"Status: built {db.Built:g} from {db.Source} ({db.Packages.Count:N0} packages, {db.Exports.Count:N0} objects). Used automatically.";
            lDb.ForeColor = db == null ? Color.DimGray : Color.DarkGreen;
        }
        catch { lDb.Text = "Status: database file is unreadable; rebuild it."; }
    }

    void Line(Level l, string m)
    {
        if (InvokeRequired) { BeginInvoke(() => Line(l, m)); return; }
        log.SelectionStart = log.TextLength;
        log.SelectionColor = l switch { Level.Change => Color.FromArgb(0, 110, 40), Level.Warn => Color.FromArgb(180, 100, 0), Level.Error => Color.Firebrick, _ => Color.FromArgb(40, 40, 40) };
        log.AppendText((l switch { Level.Change => "+ ", Level.Warn => "! ", Level.Error => "X ", _ => "  " }) + m + Environment.NewLine);
        log.ScrollToCaret();
    }

    void Busy(bool b) { bConvert.Enabled = bBuild.Enabled = bNames.Enabled = !b; bCancel.Enabled = b; bar.Style = b ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous; if (!b) bar.Value = 0; UseWaitCursor = b; }

    async Task Convert()
    {
        if (!Directory.Exists(tMod.Text)) { MessageBox.Show(this, "Pick the PC mod folder first.", Text); return; }
        if (string.IsNullOrWhiteSpace(tOut.Text)) tOut.Text = Path.Combine(tMod.Text, "Switch port");
        SaveSettings(); log.Clear(); Busy(true); cts = new CancellationTokenSource(); bOpen.Enabled = false;
        var l = new Log(); l.OnLine += Line;
        var o = new Options { ModDir = tMod.Text, GlobalDir = tGlobal.Text, OutputDir = tOut.Text, RetocPath = Retoc, MaxTextureSize = MaxTex,
                              Transparency = (TransparencyMode)Math.Max(0, cAlpha.SelectedIndex), IncludeSharedBlueprints = chkShared.Checked,
                              NameDbPath = File.Exists(Settings.NamesPath) ? Settings.NamesPath : null,
                              NewName = string.IsNullOrWhiteSpace(tName.Text) ? null : tName.Text.Trim(),
                              NameKey = cSlot.SelectedIndex > 0 ? ((string)cSlot.SelectedItem).Split(' ')[0] : null,
                              SavedNames = new System.Collections.Generic.Dictionary<string, string>(s.Names),
                              BaseTextPath = string.IsNullOrWhiteSpace(tBaseText.Text) ? null : tBaseText.Text,
                              SavedBaseTextDir = Directory.Exists(Settings.BaseTextDir) ? Settings.BaseTextDir : null };
        if (mopts != null && mopts.Groups.Count > 0 && string.Equals(mopts.ModDir, tMod.Text, StringComparison.OrdinalIgnoreCase))
        { o.InputFiles = mopts.SelectedFiles(); o.PreferredContainers = mopts.OptionContainers(); o.OptionsSummary = mopts.Describe(); }
        Result r = null;
        try { r = await Task.Run(() => new Converter(o, l, cts.Token).Run()); }
        catch (Exception ex) { Line(Level.Error, ex.Message); }
        Busy(false);
        if (r?.Names != null) { s.Names = r.Names; s.Save(); RefreshNames(); }
        if (r?.PaksDir != null && r.Paks.Count > 0)
        {
            lastOutput = tOut.Text; bOpen.Enabled = true;
            Line(Level.Info, "");
            Line(r.Success ? Level.Change : Level.Warn, $"Done{(l.Warnings > 0 ? $" with {l.Warnings} warning(s)" : "")}. Copy the 'atmosphere' folder from:");
            Line(Level.Info, "   " + tOut.Text);
            Line(Level.Info, "to the root of your SD card. Files: " + string.Join(", ", r.Paks));
            if (l.Warnings > 0) Line(Level.Info, "Warnings are explained above and saved in 'conversion report.txt'.");
        }
        else Line(Level.Error, "Conversion did not produce any files. See the messages above.");
    }

    /// <summary>Rename characters without converting a skin mod (e.g. on top of a text mod).</summary>
    async Task NamesOnly()
    {
        if (!string.IsNullOrWhiteSpace(tName.Text) && cSlot.SelectedIndex <= 0)
        { MessageBox.Show(this, "Pick the name slot to change in the \"Name slot\" list (open it to load the slots).", Text); return; }
        var outDir = !string.IsNullOrWhiteSpace(tOut.Text) ? tOut.Text
                   : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SRCW Switch names");
        tOut.Text = outDir; SaveSettings(); log.Clear(); Busy(true);
        var l = new Log(); l.OnLine += Line;
        var o = new Options { OutputDir = outDir,
                              NewName = string.IsNullOrWhiteSpace(tName.Text) ? null : tName.Text.Trim(),
                              NameKey = cSlot.SelectedIndex > 0 ? ((string)cSlot.SelectedItem).Split(' ')[0] : null,
                              SavedNames = new System.Collections.Generic.Dictionary<string, string>(s.Names),
                              BaseTextPath = string.IsNullOrWhiteSpace(tBaseText.Text) ? null : tBaseText.Text,
                              SavedBaseTextDir = Directory.Exists(Settings.BaseTextDir) ? Settings.BaseTextDir : null };
        Result r = null;
        try { r = await Task.Run(() => new Converter(o, l).RunNamesOnly()); } catch (Exception ex) { Line(Level.Error, ex.Message); }
        Busy(false);
        if (r?.Names != null) { s.Names = r.Names; s.Save(); RefreshNames(); }
        if (r?.Success == true)
        {
            lastOutput = outDir; bOpen.Enabled = true;
            Line(Level.Info, ""); Line(Level.Change, "Done. Copy the 'atmosphere' folder from:"); Line(Level.Info, "   " + outDir); Line(Level.Info, "to the root of your SD card.");
        }
    }

    async Task BuildDb()
    {
        if (!Directory.Exists(tDump.Text)) { MessageBox.Show(this, "Pick your Switch dump's UNION\\Content\\Paks folder first.", Text); return; }
        SaveSettings(); log.Clear(); Busy(true); cts = new CancellationTokenSource();
        bar.Style = ProgressBarStyle.Continuous;
        var l = new Log(); l.OnLine += Line;
        try
        {
            var dump = tDump.Text; var aes = tAes.Text;
            var db = await Task.Run(() => NameDb.BuildFromDump(dump, aes, Path.Combine(AppDir, "bin"), l,
                (d, t) => BeginInvoke(() => { bar.Maximum = Math.Max(1, t); bar.Value = Math.Min(d, t); }), cts.Token, null, Settings.BaseTextDir));
            slotsLoadedFor = null;
            db.Save(Settings.NamesPath);
            Line(Level.Change, "Name database saved. You no longer need the dump connected; it is used automatically from now on.");
        }
        catch (OperationCanceledException) { Line(Level.Error, "Cancelled."); }
        catch (Exception ex) { Line(Level.Error, "Could not build the database: " + ex.Message); }
        Busy(false); RefreshDbLabel();
    }
}
