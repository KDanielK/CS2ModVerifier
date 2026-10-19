using CS2ModVerifier.Core;
using CS2ModVerifier.SourceCompare;

namespace CS2ModVerifier.App;

/// <summary>
/// One row per installed code mod, expandable to its High and Medium findings, which can be ignored.
/// The details of the selected mod are shown below the list.
/// </summary>
internal sealed class MainForm : Form
{
    private const int GlyphWidth = 18;
    private const int DotSize = 16;

    private readonly CheckService checks;
    private readonly IgnoreStore ignores;
    private readonly Dictionary<string, ModCheckResult> mods = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListView list;
    private readonly TextBox details;
    private readonly ToolStripStatusLabel status;
    private readonly Button checkAll;
    private readonly Button ignoreButton;
    private readonly Font boldFont;
    private readonly Font smallBoldFont;
    private readonly ToolTip hoverTip = new() { AutoPopDelay = 30_000, InitialDelay = 400, ReshowDelay = 200 };
    private readonly System.Windows.Forms.Timer spinnerTimer = new() { Interval = 80 };
    private readonly System.Windows.Forms.Timer rebuildTimer = new() { Interval = 100 };
    private readonly ToolStripProgressBar progress;
    private string? hoverKey;
    private int spinnerFrame;
    private bool busy;

    private sealed record ModRow(ModCheckResult Result);

    private sealed record FindingRow(ModCheckResult Result, Finding Finding);

    /// <summary>A code mod that has not been checked yet: shown greyed out until its result arrives.</summary>
    private sealed record PendingRow(PendingMod Mod);

    public MainForm(CheckService checks, IgnoreStore ignores)
    {
        this.checks = checks;
        this.ignores = ignores;
        Text = "CS2 Mod Verifier";
        Icon = SystemIcons.Shield;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1100, 720);
        MinimumSize = new Size(700, 460);
        Font = new Font("Segoe UI", 9.5f);
        boldFont = new Font(Font, FontStyle.Bold);
        smallBoldFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);

        list = new BufferedListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            Dock = DockStyle.Fill,
            ShowItemToolTips = false,
            OwnerDraw = true,
            // The image list only sets the row height; badges are drawn by hand.
            SmallImageList = new ImageList { ImageSize = new Size(1, 26) },
        };
        list.Columns.Add("Mod", 330);
        list.Columns.Add("Version", 90);
        list.Columns.Add("Source comparison", 170);
        list.Columns.Add("Paradox files", 160);
        list.Columns.Add("Open findings", 230);
        list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        list.DrawItem += (_, _) => { };
        list.DrawSubItem += DrawSubItem;
        list.MouseClick += OnListClick;
        list.MouseDoubleClick += (_, _) => ToggleSelected();
        list.KeyDown += OnListKey;
        list.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        list.MouseMove += OnListMouseMove;
        list.MouseLeave += (_, _) => HideHover();
        spinnerTimer.Tick += (_, _) =>
        {
            spinnerFrame = (spinnerFrame + 1) % 12;
            InvalidateSpinners();
        };
        rebuildTimer.Tick += (_, _) => Rebuild();

        var menu = new ContextMenuStrip();
        var ignoreItem = menu.Items.Add("Ignore finding", null, (_, _) => ToggleIgnore());
        var viewItem = menu.Items.Add("View the code on GitHub", null, (_, _) => ViewOnGitHub());
        menu.Opening += (_, e) =>
        {
            if (SelectedFinding() is not { } row)
            {
                e.Cancel = true;
                return;
            }

            ignoreItem.Text = ignores.IsIgnored(row.Result.Scan.Mod, row.Finding) ? "Restore finding" : "Ignore finding";
            viewItem.Visible = Traces.For(row.Result, row.Finding).Any(t => t.Url is not null);
        };
        list.ContextMenuStrip = menu;

        details = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5f),
            BackColor = SystemColors.Window,
            Text = "Select a mod to see what was checked. Expand a mod (click the arrow) to see its findings.",
        };

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 400 };
        split.Panel1.Controls.Add(list);
        split.Panel2.Controls.Add(details);

        checkAll = new Button { Text = "Check all mods now", AutoSize = true };
        checkAll.Click += (_, _) => _ = checks.CheckAllAsync();
        var checkSelected = new Button { Text = "Check selected mod", AutoSize = true };
        checkSelected.Click += (_, _) =>
        {
            if (SelectedResult() is { } result)
            {
                _ = checks.CheckFolderAsync(result.Scan.Mod.Path);
            }
        };
        ignoreButton = new Button { Text = "Ignore finding", AutoSize = true, Enabled = false };
        ignoreButton.Click += (_, _) => ToggleIgnore();
        var openFolder = new Button { Text = "Open mod folder", AutoSize = true };
        openFolder.Click += (_, _) =>
        {
            if (SelectedResult() is { } result && Directory.Exists(result.Scan.Mod.Path))
            {
                // Opens Explorer on the folder; nothing in it is run.
                System.Diagnostics.Process.Start("explorer.exe", $"\"{result.Scan.Mod.Path}\"");
            }
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 4) };
        buttons.Controls.AddRange([checkAll, checkSelected, ignoreButton, openFolder]);

        var footer = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 26,
            Padding = new Padding(10, 4, 10, 4),
            ForeColor = SystemColors.GrayText,
            Text = Disclaimer.Short + " Ignored findings stay listed but do not count. See About for what each check shows.",
        };

        progress = new ToolStripProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Visible = false, Size = new Size(120, 16) };
        status = new ToolStripStatusLabel();
        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(progress);
        statusStrip.Items.Add(status);

        Controls.Add(split);
        Controls.Add(buttons);
        Controls.Add(footer);
        Controls.Add(statusStrip);

        foreach (var result in checks.Results)
        {
            if (result.Scan.HasCode)
            {
                mods[result.Scan.Mod.Path] = result;
            }
        }

        Rebuild();
    }

    /// <summary>Raised when the user ignores or restores a finding.</summary>
    public event Action? IgnoresChanged;

    public void Upsert(ModCheckResult result)
    {
        // Only code mods are listed; asset mods have nothing to compare or scan.
        if (!result.Scan.HasCode)
        {
            RemoveRow(result.Scan.Mod.Path);
            return;
        }

        mods[result.Scan.Mod.Path] = result;
        RequestRebuild();
    }

    public void RemoveRow(string modPath)
    {
        if (mods.Remove(modPath))
        {
            expanded.Remove(modPath);
            RequestRebuild();
        }
    }

    public void SetBusy(bool isBusy)
    {
        busy = isBusy;
        checkAll.Enabled = !isBusy;
        UpdateStatus();
    }

    /// <summary>Mods were queued, or one started or finished: redraw rows and the spinner.</summary>
    public void RefreshPending() => RequestRebuild();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            boldFont.Dispose();
            smallBoldFont.Dispose();
            hoverTip.Dispose();
            spinnerTimer.Dispose();
            rebuildTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Rebuilds the list a moment later. A check sends several events per mod; combining them into one
    /// rebuild keeps the list from flickering.
    /// </summary>
    private void RequestRebuild()
    {
        rebuildTimer.Stop();
        rebuildTimer.Start();
    }

    private void Rebuild()
    {
        rebuildTimer.Stop();
        var rows = BuildRows();

        // Same rows in the same order (the usual case while a check runs): update them in place, no clearing.
        if (rows.Count == list.Items.Count && rows.Select(r => r.Name).SequenceEqual(list.Items.Cast<ListViewItem>().Select(i => i.Name)))
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var item = list.Items[i];
                item.Tag = rows[i].Tag;
                item.Text = rows[i].Text;
                for (var column = 1; column < rows[i].SubItems.Count; column++)
                {
                    item.SubItems[column].Text = rows[i].SubItems[column].Text;
                }
            }

            list.Invalidate();
        }
        else
        {
            var selected = list.SelectedItems.Count == 1 ? list.SelectedItems[0].Name : null;
            list.BeginUpdate();
            list.Items.Clear();
            list.Items.AddRange([.. rows]);
            if (selected is not null && list.Items[selected] is { } again)
            {
                again.Selected = true;
                again.Focused = true;
            }

            list.EndUpdate();
        }

        if (checks.Pending.Any(p => p.Checking))
        {
            spinnerTimer.Start();
        }
        else
        {
            spinnerTimer.Stop();
        }

        OnSelectionChanged();
        UpdateStatus();
    }

    private List<ListViewItem> BuildRows()
    {
        var rows = new List<ListViewItem>();
        foreach (var result in mods.Values.OrderBy(Rank).ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var path = result.Scan.Mod.Path;
            var rollup = ignores.Summarize(result);
            var modItem = new ListViewItem(result.DisplayName) { Name = path, Tag = new ModRow(result) };
            modItem.SubItems.AddRange([Texts.Version(result), Texts.Source(result), Texts.Files(result), rollup.Text]);
            rows.Add(modItem);

            if (!expanded.Contains(path))
            {
                continue;
            }

            foreach (var finding in IgnoreStore.ImportantFindings(result))
            {
                var key = path + "#" + IgnoreStore.Key(result.Scan.Mod, finding);
                var findingItem = new ListViewItem(finding.Message) { Name = key, Tag = new FindingRow(result, finding) };
                findingItem.SubItems.AddRange(["", "", "", ""]);
                rows.Add(findingItem);
            }
        }

        // Code mods not checked yet are listed greyed out at the end, so the list is complete from the start.
        foreach (var waiting in checks.Pending.Where(p => !mods.ContainsKey(p.Mod.Path)).OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new ListViewItem(waiting.DisplayName) { Name = waiting.Mod.Path, Tag = new PendingRow(waiting) };
            item.SubItems.AddRange([waiting.Version, waiting.Checking ? "Checking..." : "Waiting", "", ""]);
            rows.Add(item);
        }

        return rows;
    }

    /// <summary>Repaints only the rows that show a spinner.</summary>
    private void InvalidateSpinners()
    {
        foreach (ListViewItem item in list.Items)
        {
            var spinning = item.Tag switch
            {
                PendingRow { Mod.Checking: true } => true,
                ModRow { Result: var result } => IsChecking(result.Scan.Mod.Path),
                _ => false,
            };
            if (spinning)
            {
                list.Invalidate(item.Bounds);
            }
        }
    }

    private void DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.SubItem is null)
        {
            return;
        }

        var selected = e.Item.Selected;
        var back = selected ? SystemColors.Highlight : (e.Item.Tag is FindingRow ? Color.FromArgb(0xF7, 0xF7, 0xF7) : SystemColors.Window);
        var fore = selected ? SystemColors.HighlightText : SystemColors.WindowText;
        var g = e.Graphics;
        const TextFormatFlags textFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        switch (e.Item.Tag)
        {
            case ModRow { Result: var result }:
            {
                using (var brush = new SolidBrush(back))
                {
                    g.FillRectangle(brush, e.Bounds);
                }

                if (e.ColumnIndex == 0)
                {
                    var hasFindings = IgnoreStore.ImportantFindings(result).Count > 0;
                    var x = e.Bounds.X + 4;
                    if (hasFindings)
                    {
                        TextRenderer.DrawText(g, expanded.Contains(result.Scan.Mod.Path) ? "▼" : "▶", Font,
                            new Rectangle(x, e.Bounds.Y, GlyphWidth, e.Bounds.Height), fore, textFlags);
                    }

                    x += GlyphWidth;
                    var rollup = ignores.Summarize(result);
                    var dot = new Rectangle(x, e.Bounds.Y + (e.Bounds.Height - DotSize) / 2, DotSize, DotSize);
                    if (IsChecking(result.Scan.Mod.Path))
                    {
                        DrawSpinner(g, dot, selected ? fore : Badges.Info);
                    }
                    else if (rollup.Highest is { } highest)
                    {
                        Badges.DrawDot(g, dot, Badges.For(highest), Badges.Letter(highest), smallBoldFont);
                    }
                    else
                    {
                        Badges.DrawDot(g, dot, Badges.Good, "✓", smallBoldFont);
                    }

                    x += DotSize + 6;
                    TextRenderer.DrawText(g, result.DisplayName, boldFont, new Rectangle(x, e.Bounds.Y, e.Bounds.Right - x, e.Bounds.Height), fore, textFlags);
                    return;
                }

                // No published source, or code not in it: a solid red cell, so it stands out in a long list.
                if (e.ColumnIndex == 2 && SourceNeedsAttention(result))
                {
                    var cell = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 3, e.Bounds.Width - 4, e.Bounds.Height - 6);
                    using (var brush = new SolidBrush(Badges.High))
                    {
                        g.FillRectangle(brush, cell);
                    }

                    TextRenderer.DrawText(g, "⚠ " + e.SubItem.Text, boldFont, Rectangle.Inflate(cell, -4, 0), Color.White, textFlags);
                    return;
                }

                var color = selected ? fore : ColumnColor(result, e.ColumnIndex);
                var font = e.ColumnIndex == 4 && ignores.Summarize(result).Highest is not null ? boldFont : Font;
                TextRenderer.DrawText(g, e.SubItem.Text, font, Rectangle.Inflate(e.Bounds, -4, 0), color, textFlags);
                return;
            }

            case PendingRow { Mod: var waiting }:
            {
                using (var brush = new SolidBrush(back))
                {
                    g.FillRectangle(brush, e.Bounds);
                }

                var grey = selected ? fore : SystemColors.GrayText;
                if (e.ColumnIndex == 0)
                {
                    var x = e.Bounds.X + 4 + GlyphWidth;
                    var dot = new Rectangle(x, e.Bounds.Y + (e.Bounds.Height - DotSize) / 2, DotSize, DotSize);
                    if (waiting.Checking)
                    {
                        DrawSpinner(g, dot, selected ? fore : Badges.Info);
                    }
                    else
                    {
                        using var pen = new Pen(grey, 1.5f);
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        g.DrawEllipse(pen, Rectangle.Inflate(dot, -2, -2));
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
                    }

                    x += DotSize + 6;
                    TextRenderer.DrawText(g, waiting.DisplayName, Font, new Rectangle(x, e.Bounds.Y, e.Bounds.Right - x, e.Bounds.Height), grey, textFlags);
                    return;
                }

                TextRenderer.DrawText(g, e.SubItem.Text, Font, Rectangle.Inflate(e.Bounds, -4, 0), grey, textFlags);
                return;
            }

            case FindingRow { Result: var result, Finding: var finding }:
            {
                // A finding row is drawn once, across the full width of the row.
                if (e.ColumnIndex != 0)
                {
                    return;
                }

                var row = e.Item.Bounds;
                using (var brush = new SolidBrush(back))
                {
                    g.FillRectangle(brush, row);
                }

                var ignored = ignores.IsIgnored(result.Scan.Mod, finding);
                var x = row.X + GlyphWidth + DotSize + 10;
                var pillHeight = row.Height - 8;
                x += Badges.DrawPill(g, new Point(x, row.Y + 4), pillHeight, ignored ? Badges.Ignored : Badges.For(finding.Severity),
                    finding.Severity.ToString().ToUpperInvariant(), smallBoldFont) + 6;
                if (ignored)
                {
                    x += Badges.DrawPill(g, new Point(x, row.Y + 4), pillHeight, Badges.Ignored, "IGNORED", smallBoldFont) + 6;
                }

                // Whether the risky calls behind this finding are in the published source.
                switch (Traces.AllInSource(Traces.For(result, finding)))
                {
                    case true:
                        x += Badges.DrawPill(g, new Point(x, row.Y + 4), pillHeight, Badges.Good, "IN SOURCE", smallBoldFont) + 6;
                        break;
                    case false:
                        x += Badges.DrawPill(g, new Point(x, row.Y + 4), pillHeight, Badges.High, "NOT IN SOURCE", smallBoldFont) + 6;
                        break;
                }

                var textColor = selected ? fore : ignored ? Badges.Ignored : SystemColors.WindowText;
                var label = $"{finding.Category}: ";
                var labelWidth = TextRenderer.MeasureText(g, label, boldFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, label, boldFont, new Rectangle(x, row.Y, labelWidth + 2, row.Height), textColor, textFlags);
                x += labelWidth + 2;
                var where = finding.File is null ? string.Empty : $"  ({finding.File})";
                TextRenderer.DrawText(g, finding.Message + where, Font, new Rectangle(x, row.Y, row.Right - x - 4, row.Height), textColor, textFlags);
                return;
            }
        }
    }

    private bool IsChecking(string modPath) =>
        checks.Pending.Any(p => p.Checking && string.Equals(p.Mod.Path, modPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>A ring of twelve dots, the brightest one moving with each timer tick.</summary>
    private void DrawSpinner(Graphics g, Rectangle bounds, Color color)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var center = new PointF(bounds.X + bounds.Width / 2f, bounds.Y + bounds.Height / 2f);
        var radius = bounds.Width / 2f - 2;
        for (var i = 0; i < 12; i++)
        {
            var age = (spinnerFrame - i + 12) % 12;
            var alpha = Math.Max(40, 255 - age * 20);
            var angle = i * Math.PI / 6;
            using var brush = new SolidBrush(Color.FromArgb(alpha, color));
            var x = center.X + (float)(Math.Cos(angle) * radius);
            var y = center.Y + (float)(Math.Sin(angle) * radius);
            g.FillEllipse(brush, x - 1.6f, y - 1.6f, 3.2f, 3.2f);
        }

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
    }

    /// <summary>Shows the plain-language explanation of the cell under the mouse.</summary>
    private void OnListMouseMove(object? sender, MouseEventArgs e)
    {
        var hit = list.HitTest(e.Location);
        if (hit.Item is null)
        {
            HideHover();
            return;
        }

        var column = hit.SubItem is null ? 0 : hit.Item.SubItems.IndexOf(hit.SubItem);
        string text;
        string key;
        switch (hit.Item.Tag)
        {
            case ModRow { Result: var result }:
                key = hit.Item.Name + "|" + column;
                text = Explain.Cell(result, ignores.Summarize(result), column);
                break;
            case FindingRow { Result: var result, Finding: var finding }:
                var ignored = ignores.IsIgnored(result.Scan.Mod, finding);
                key = hit.Item.Name + "|" + ignored;
                text = Explain.Finding(finding, ignored, Traces.For(result, finding));
                break;
            case PendingRow { Mod: var waiting }:
                key = hit.Item.Name + "|" + waiting.Checking;
                text = Explain.Waiting(waiting.Checking);
                break;
            default:
                HideHover();
                return;
        }

        if (key != hoverKey)
        {
            hoverKey = key;
            hoverTip.Show(text, list, e.X + 18, e.Y + 22, hoverTip.AutoPopDelay);
        }
    }

    private void HideHover()
    {
        hoverKey = null;
        hoverTip.Hide(list);
    }

    /// <summary>No source linked, or code not in the source, and the user has not ignored those findings.</summary>
    private bool SourceNeedsAttention(ModCheckResult result) =>
        result.SourceStatus is SourceStatus.NoSourceLinked or SourceStatus.Differences
        && IgnoreStore.ImportantFindings(result)
            .Where(f => f.Category is "NoSourceLinked" or "SourceMismatch" or "SignatureDiffers")
            .Any(f => !ignores.IsIgnored(result.Scan.Mod, f));

    private Color ColumnColor(ModCheckResult result, int column) => column switch
    {
        2 => result.SourceStatus switch
        {
            SourceStatus.Matches => Badges.Good,
            // Open differences and missing source are drawn as a red cell; here they are already ignored.
            SourceStatus.Differences or SourceStatus.NoSourceLinked => SystemColors.GrayText,
            SourceStatus.Error => Badges.Medium,
            SourceStatus.RateLimited => Badges.Info,
            _ => SystemColors.GrayText,
        },
        3 => result.Scan.Manifest.Status switch
        {
            ManifestStatus.AllMatch => Badges.Good,
            ManifestStatus.Mismatch => Badges.High,
            _ => SystemColors.GrayText,
        },
        4 => ignores.Summarize(result).Highest is { } highest ? Badges.For(highest) : Badges.Good,
        _ => SystemColors.WindowText,
    };

    private void OnListClick(object? sender, MouseEventArgs e)
    {
        // A click on the arrow expands or collapses the mod.
        if (list.GetItemAt(e.X, e.Y) is { Tag: ModRow } item && e.X < item.Bounds.X + 4 + GlyphWidth)
        {
            Toggle(item);
        }
    }

    private void OnListKey(object? sender, KeyEventArgs e)
    {
        if (list.SelectedItems.Count != 1)
        {
            return;
        }

        var item = list.SelectedItems[0];
        if (item.Tag is ModRow { Result: var result })
        {
            var isOpen = expanded.Contains(result.Scan.Mod.Path);
            if ((e.KeyCode == Keys.Right && !isOpen) || (e.KeyCode == Keys.Left && isOpen))
            {
                Toggle(item);
                e.Handled = true;
            }
        }
        else if (item.Tag is FindingRow { Result: var parent } && e.KeyCode == Keys.Left && list.Items[parent.Scan.Mod.Path] is { } modItem)
        {
            // Left on a finding jumps back to its mod.
            modItem.Selected = true;
            modItem.Focused = true;
            e.Handled = true;
        }
    }

    private void ToggleSelected()
    {
        if (list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag is ModRow)
        {
            Toggle(list.SelectedItems[0]);
        }
    }

    private void Toggle(ListViewItem item)
    {
        if (item.Tag is not ModRow { Result: var result } || IgnoreStore.ImportantFindings(result).Count == 0)
        {
            return;
        }

        var path = result.Scan.Mod.Path;
        if (!expanded.Remove(path))
        {
            expanded.Add(path);
        }

        Rebuild();
    }

    private void ToggleIgnore()
    {
        if (SelectedFinding() is not { } row)
        {
            return;
        }

        var ignore = !ignores.IsIgnored(row.Result.Scan.Mod, row.Finding);
        ignores.SetIgnored(row.Result.Scan.Mod, row.Finding, ignore);
        Log.Write($"{(ignore ? "Ignored" : "Restored")} finding for {row.Result.Scan.Mod.Path}: {row.Finding.Category}: {row.Finding.Message}");
        Rebuild();
        IgnoresChanged?.Invoke();
    }

    /// <summary>Opens the line of the first traced call in the browser. Only links to github.com are opened.</summary>
    private void ViewOnGitHub()
    {
        if (SelectedFinding() is not { } row
            || Traces.For(row.Result, row.Finding).Select(t => t.Url).FirstOrDefault(u => u is not null) is not { } url
            || !url.StartsWith("https://github.com/", StringComparison.Ordinal))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OnSelectionChanged()
    {
        var finding = SelectedFinding();
        ignoreButton.Enabled = finding is not null;
        ignoreButton.Text = finding is not null && ignores.IsIgnored(finding.Result.Scan.Mod, finding.Finding) ? "Restore finding" : "Ignore finding";
        if (SelectedResult() is { } result)
        {
            details.Text = Texts.Details(result, ignores).ReplaceLineEndings("\r\n");
        }
        else if (list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag is PendingRow { Mod: var waiting })
        {
            details.Text = $"{waiting.DisplayName} {waiting.Version}\r\n\r\n{Explain.Waiting(waiting.Checking)}";
        }
    }

    private FindingRow? SelectedFinding() => list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as FindingRow : null;

    private ModCheckResult? SelectedResult() => list.SelectedItems.Count == 1
        ? list.SelectedItems[0].Tag switch
        {
            ModRow m => m.Result,
            FindingRow f => f.Result,
            _ => null,
        }
        : null;

    private void UpdateStatus()
    {
        var waiting = checks.Pending.Count(p => !mods.ContainsKey(p.Mod.Path) || p.Checking);
        var open = mods.Values.Count(r => ignores.Summarize(r).Highest is not null);
        progress.Visible = busy || waiting > 0;
        status.Text = waiting > 0
            ? $"Checking mods: {waiting} to go. {mods.Count} checked so far, {open} with open findings."
            : $"{mods.Count} code mods, {open} with open findings.{LastCheck()} Watching the mod folders for new downloads and updates.";

        // A check of only code mods with cached source comparisons takes well under a second; show that it ran.
        string LastCheck() => checks.LastCheckFinished is { } at ? $" Last check: {at:HH:mm:ss}." : string.Empty;
    }

    /// <summary>Mods that need attention first: changed files, code not in source, then by open severity.</summary>
    private int Rank(ModCheckResult r)
    {
        if (r.Scan.Manifest.Status == ManifestStatus.Mismatch && ignores.Summarize(r).Highest is not null)
        {
            return 0;
        }

        return ignores.Summarize(r).Highest switch
        {
            Severity.High => 1,
            Severity.Medium => 2,
            _ => 3,
        };
    }
}

/// <summary>A list that paints into an off-screen buffer first, so updates do not flicker.</summary>
internal sealed class BufferedListView : ListView
{
    public BufferedListView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
}
