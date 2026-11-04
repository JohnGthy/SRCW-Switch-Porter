using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
namespace SwitchPorter.Gui;

/// <summary>Pick a mod's outfits / accessories / add-ons (from its mod.ini, or its .disabled files) before converting.</summary>
public class OptionsDialog : Form
{
    readonly ModOptions m;
    readonly List<(ModOptions.Group g, Control c)> editors = new();

    public OptionsDialog(ModOptions mods)
    {
        m = mods;
        Text = "Mod parts" + (string.IsNullOrEmpty(m.Title) ? "" : " - " + m.Title);
        Width = 620; Height = 640; StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 9.5f);
        MinimizeBox = false; MaximizeBox = false;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);
        root.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(570, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 8),
            Text = m.FromIni
                ? "Choose what to include, like in a PC mod manager. Only the chosen parts are converted; your PC files are not changed."
                : "Untick parts you don't want converted. Files ending in .disabled start unticked; tick them to include them anyway. Your PC files are not changed."
        });

        var list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        root.Controls.Add(list);
        foreach (var g in m.Groups)
        {
            var box = new GroupBox { Text = g.Name, Width = 550, AutoSize = true, Padding = new Padding(8) };
            var inner = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            if (!string.IsNullOrWhiteSpace(g.Description))
                inner.Controls.Add(new Label { Text = g.Description, AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = Color.DimGray });
            Control c;
            if (g.Multiple)
            {
                var cl = new CheckedListBox { Width = 520, CheckOnClick = true, Height = Math.Min(200, 22 * Math.Max(1, g.Choices.Count) + 6), BorderStyle = BorderStyle.None };
                foreach (var ch in g.Choices) cl.Items.Add(ch, g.Selected.Contains(ch));
                c = cl;
            }
            else
            {
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
                cb.Items.AddRange(g.Choices.Cast<object>().ToArray());
                cb.SelectedItem = g.Choices.FirstOrDefault(g.Selected.Contains) ?? g.Choices.FirstOrDefault();
                c = cb;
            }
            inner.Controls.Add(c); box.Controls.Add(inner); list.Controls.Add(box);
            editors.Add((g, c));
        }

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        var ok = new Button { Text = "OK", Width = 100, Height = 32, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok); root.Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            foreach (var (g, c) in editors)
            {
                g.Selected.Clear();
                if (c is CheckedListBox cl) foreach (var it in cl.CheckedItems) g.Selected.Add((string)it);
                else if (c is ComboBox cb && cb.SelectedItem is string sel) g.Selected.Add(sel);
            }
        };
    }
}
