using CS2ModVerifier.Core;

namespace CS2ModVerifier.App;

/// <summary>
/// Shown on first start, and from "About". On first start the user must accept it before anything is scanned.
/// </summary>
internal sealed class DisclaimerForm : Form
{
    public DisclaimerForm(bool firstRun = true)
    {
        Text = firstRun ? "CS2 Mod Verifier: before you start" : "About CS2 Mod Verifier";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(720, 560);
        MinimumSize = new Size(520, 420);
        Font = new Font("Segoe UI", 9.5f);

        var text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Window,
            Text = (Disclaimer.ChecksTitle.ToUpperInvariant() + "\n" + Disclaimer.Checks + "\n\n"
                + Disclaimer.Title.ToUpperInvariant() + "\n" + Disclaimer.Text).ReplaceLineEndings("\r\n"),
        };

        // Comparing with the source is the core function, so it is stated rather than offered as a setting.
        var network = new Label
        {
            Text = "To compare mods with their source, the tool downloads the source code from GitHub. It only reads public repositories "
                + "linked from a mod's Paradox Mods page, and the official releases of libraries such as Harmony. It sends nothing about you or your PC.",
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            Margin = new Padding(0, 8, 0, 8),
        };

        var accept = new Button { Text = firstRun ? "I understand, start" : "Close", AutoSize = true, DialogResult = DialogResult.OK };
        var quit = new Button { Text = "Quit", AutoSize = true, DialogResult = DialogResult.Cancel, Visible = firstRun };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(accept);
        buttons.Controls.Add(quit);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(text, 0, 0);
        layout.Controls.Add(network, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);

        AcceptButton = accept;
        CancelButton = firstRun ? quit : accept;
        Shown += (_, _) => accept.Focus();
    }
}
