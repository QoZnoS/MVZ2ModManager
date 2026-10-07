using MVZ2ModManager.Theme;
using MVZ2ModManager.UI.Controls;

namespace MVZ2ModManager.UI;

/// <summary>
/// 带一个复选框的确认框。
///
/// <para>存在的理由：<see cref="System.Windows.Forms.MessageBox"/> 放不下复选框，而
/// "卸载时要不要连资源目录一起删"这种问题必须能**同时**给出警告和一个明确的选择 ——
/// 弹两次 Yes/No 会让人搞不清哪个是哪个。</para>
/// </summary>
internal sealed class CheckboxConfirmDialog : Form
{
    private readonly CheckBox _check;

    private CheckboxConfirmDialog(string title, string message, string? checkText, bool checkEnabled, bool checkDefault, bool accentOk)
    {
        var t = ThemeEngine.Current;

        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = t.Background;
        Font = ThemeEngine.MakeFont(9f);

        var body = new Label
        {
            Text = message,
            AutoSize = false,
            Location = new Point(18, 16),
            Size = new Size(432, 150),
            ForeColor = t.Text,
        };

        _check = new CheckBox
        {
            Text = checkText ?? "",
            Location = new Point(20, 174),
            Size = new Size(430, 40),
            Checked = checkDefault,
            Enabled = checkEnabled,
            Visible = checkText != null,
            ForeColor = t.Text,
        };

        int buttonY = checkText != null ? 224 : 174;
        ClientSize = new Size(468, buttonY + 52);

        var cancelBtn = new RButton { Text = "取消", Width = 96, Height = 32, Location = new Point(238, buttonY), CornerRadius = 6 };
        var okBtn = new RButton { Text = "确定", Width = 96, Height = 32, Location = new Point(344, buttonY), CornerRadius = 6 };
        ThemeEngine.StyleGhostButton(cancelBtn);
        ThemeEngine.StyleRButton(okBtn, accent: true);
        if (accentOk) okBtn.Tag = "accent";

        cancelBtn.Click += (_, __) => { DialogResult = DialogResult.Cancel; Close(); };
        okBtn.Click += (_, __) => { DialogResult = DialogResult.OK; Close(); };

        Controls.Add(body);
        if (checkText != null) Controls.Add(_check);
        Controls.Add(cancelBtn);
        Controls.Add(okBtn);

        HandleCreated += (_, __) => ThemeEngine.ApplyScrollTheme(this);
    }

    /// <summary>
    /// 弹一个带复选框的确认框。
    /// </summary>
    /// <returns><c>true</c> = 用户点了确定；<paramref name="checkedValue"/> 是复选框最终状态。</returns>
    public static bool Show(
        IWin32Window? owner,
        string title,
        string message,
        string? checkText,
        out bool checkedValue,
        bool checkDefault = true,
        bool checkEnabled = true)
    {
        checkedValue = false;
        using var dlg = new CheckboxConfirmDialog(title, message, checkText, checkEnabled, checkDefault, accentOk: true);

        // 内容超长时让标签自动撑开，别把字截掉。
        foreach (Control c in dlg.Controls)
        {
            if (c is Label lbl)
            {
                var measured = TextRenderer.MeasureText(lbl.Text, lbl.Font, new Size(lbl.Width, int.MaxValue), TextFormatFlags.WordBreak);
                if (measured.Height > lbl.Height)
                {
                    int extra = measured.Height - lbl.Height;
                    lbl.Height = measured.Height;
                    foreach (Control other in dlg.Controls)
                        if (other != lbl) other.Top += extra;
                    dlg.ClientSize = new Size(dlg.ClientSize.Width, dlg.ClientSize.Height + extra);
                }
            }
        }

        bool ok = dlg.ShowDialog(owner) == DialogResult.OK;
        checkedValue = dlg._check.Checked;
        return ok;
    }
}
