using System;
using System.Drawing;
using System.Windows.Forms;
namespace SwitchPorter.Gui;

/// <summary>A titled section that folds open/closed with an arrow; shows a short summary while closed.</summary>
public class Section : TableLayoutPanel
{
    readonly Button header = new() { FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft, Height = 30, Dock = DockStyle.Fill, Cursor = Cursors.Hand, Margin = new Padding(0) };
    readonly Label summary = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(26, 0, 3, 4) };
    Control body;
    readonly string title;
    bool expanded;
    public event Action<bool> Toggled;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string> Summary { get; set; }   // text shown under the title while folded

    public Section(string title, Control body, bool expanded)
    {
        this.title = title; this.body = body;
        ColumnCount = 1; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Margin = new Padding(0, 8, 0, 0);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.FlatAppearance.BorderSize = 0; header.FlatAppearance.MouseOverBackColor = Color.FromArgb(228, 233, 243);
        header.Font = new Font("Segoe UI Semibold", 10f); header.BackColor = Color.FromArgb(238, 241, 247);
        header.Click += (_, _) => Expanded = !Expanded;
        body.Margin = new Padding(14, 4, 0, 4); body.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        Controls.Add(header, 0, 0); Controls.Add(summary, 0, 1); Controls.Add(body, 0, 2);
        for (int i = 0; i < 3; i++) RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Expanded = expanded;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Expanded
    {
        get => expanded;
        set
        {
            expanded = value;
            header.Text = (value ? "▼   " : "►   ") + title;
            body.Visible = value;
            RefreshSummary();
            Toggled?.Invoke(value);
        }
    }

    public void RefreshSummary()
    {
        var t = expanded ? null : Summary?.Invoke();
        summary.Text = t ?? ""; summary.Visible = !string.IsNullOrEmpty(t);
    }

    public void FitWidth(int w) { summary.MaximumSize = new Size(Math.Max(200, w - 40), 0); }
}
