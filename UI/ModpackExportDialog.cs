using MVZ2ModManager.Core;
using MVZ2ModManager.Theme;
using MVZ2ModManager.UI.Controls;

namespace MVZ2ModManager.UI;

/// <summary>
/// 导出模组包的对话框：起个名字 + 勾选要打进去的文件。
///
/// <para>默认勾上 <c>plugins</c> 与 <c>StreamingAssets/Mods</c> —— 前者是插件本体，
/// 后者是这套游戏特有的模组资源（metas / sprites），少了它模组在别人机器上是残的。
/// <c>config</c> 默认不勾：那是个人调参，塞进包里容易把别人的配置覆盖掉。</para>
///
/// <para>顺带把"包多大"实时显示出来 —— 加不加 <c>config</c>、要不要带上资源，
/// 体积差异很大，勾的时候就得看得见。</para>
/// </summary>
internal sealed class ModpackExportDialog : Form
{
    public string PackName => _nameBox.Text.Trim();
    public List<string> IncludedPaths { get; private set; } = new();

    private readonly RTextBox _nameBox;
    private readonly TreeView _tree;
    private readonly Label _summary;

    public ModpackExportDialog(List<PackRoot> roots, string defaultName)
    {
        var t = ThemeEngine.Current;

        Text = "创建模组包";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = t.Background;
        Font = ThemeEngine.MakeFont(9f);

        var nameLbl = new Label
        {
            Text = "包名：", AutoSize = true, Location = new Point(16, 14), ForeColor = t.Text,
        };
        _nameBox = new RTextBox
        {
            Location = new Point(16, 36), Width = 428, Height = 30,
            BackColor = t.SurfaceAlt, ForeColor = t.Text, CornerRadius = 6,
            Text = defaultName,
        };

        var treeLbl = new Label
        {
            Text = "要打进去的内容（已按默认勾选；取消勾选可缩小体积）：",
            AutoSize = true, Location = new Point(16, 76), ForeColor = t.SubText, Font = ThemeEngine.MakeFont(8.5f),
        };

        _tree = new TreeView
        {
            Location = new Point(16, 98), Width = 428, Height = 330,
            CheckBoxes = true, BackColor = t.Surface, ForeColor = t.Text, BorderStyle = BorderStyle.FixedSingle,
        };
        _tree.AfterCheck += (_, e) =>
        {
            if (e.Action == TreeViewAction.Unknown || e.Node == null) return;
            SetChildrenChecked(e.Node, e.Node.Checked);
            UpdateSummary();
        };
        _tree.HandleCreated += (_, __) => ThemeEngine.ApplyScrollTheme(_tree);

        _summary = new Label
        {
            Location = new Point(16, 434), Width = 428, Height = 20,
            ForeColor = t.SubText, Font = ThemeEngine.MakeFont(8.5f), AutoSize = false,
        };

        var note = new Label
        {
            Location = new Point(16, 456), Width = 428, Height = 32,
            ForeColor = t.SubText, Font = ThemeEngine.MakeFont(8f), AutoSize = false,
            Text = "不会包含：BepInEx\\core、BepInEx\\interop、BepInEx.cfg —— 那些是运行环境，不属于模组。",
        };

        var cancelBtn = new RButton { Text = "取消", Width = 100, Height = 32, Location = new Point(16, 496), CornerRadius = 6 };
        var saveBtn = new RButton { Text = "创建", Width = 100, Height = 32, Location = new Point(344, 496), CornerRadius = 6 };
        ThemeEngine.StyleGhostButton(cancelBtn);
        ThemeEngine.StyleRButton(saveBtn, accent: true);
        cancelBtn.Click += (_, __) => { DialogResult = DialogResult.Cancel; Close(); };
        saveBtn.Click += (_, __) => Confirm();

        Controls.Add(nameLbl);
        Controls.Add(_nameBox);
        Controls.Add(treeLbl);
        Controls.Add(_tree);
        Controls.Add(_summary);
        Controls.Add(note);
        Controls.Add(cancelBtn);
        Controls.Add(saveBtn);

        ClientSize = new Size(460, 544);
        PopulateTree(roots);
        UpdateSummary();

        HandleCreated += (_, __) => ThemeEngine.ApplyScrollTheme(this);
        Shown += (_, __) => { _nameBox.Inner.Focus(); _nameBox.Inner.SelectAll(); };
    }

    // ------------------------------------------------------------ 树

    private void PopulateTree(List<PackRoot> roots)
    {
        _tree.Nodes.Clear();
        var defaults = ModpackManager.DefaultRootNames;

        foreach (var root in roots)
        {
            if (!Directory.Exists(root.AbsolutePath)) continue;

            bool defaultChecked = defaults.Contains(root.Name);
            var node = new TreeNode(root.Name) { Tag = root.AbsolutePath, Checked = defaultChecked };
            PopulateChildren(node, root.AbsolutePath, defaultChecked, root.KeepNames);
            _tree.Nodes.Add(node);
        }
    }

    private static void PopulateChildren(TreeNode parent, string dirPath, bool defaultChecked, string[] keepNames)
    {
        foreach (var dir in Directory.GetDirectories(dirPath).OrderBy(d => d))
        {
            string name = Path.GetFileName(dir);
            if (keepNames.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))) continue;

            var node = new TreeNode(name) { Tag = dir, Checked = defaultChecked };
            PopulateChildren(node, dir, defaultChecked, keepNames);
            parent.Nodes.Add(node);
        }

        foreach (var file in Directory.GetFiles(dirPath).OrderBy(f => f))
        {
            string name = Path.GetFileName(file);
            if (keepNames.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))) continue;

            parent.Nodes.Add(new TreeNode(name) { Tag = file, Checked = defaultChecked });
        }
    }

    private static void SetChildrenChecked(TreeNode node, bool value)
    {
        foreach (TreeNode child in node.Nodes)
        {
            child.Checked = value;
            SetChildrenChecked(child, value);
        }
    }

    private void UpdateSummary()
    {
        var files = new List<string>();
        CollectCheckedFiles(_tree.Nodes, files);

        long bytes = 0;
        foreach (var f in files)
        {
            try { bytes += new FileInfo(f).Length; } catch { }
        }

        _summary.Text = $"已选 {files.Count} 个文件，未压缩约 {FormatSize(bytes)}";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.#} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes} B";
    }

    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(_nameBox.Text))
        {
            MessageBox.Show(this, "请给模组包起个名字。", "需要名字", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var included = new List<string>();
        CollectCheckedFiles(_tree.Nodes, included);
        if (included.Count == 0)
        {
            MessageBox.Show(this, "至少要勾选一个文件。", "没有选中任何内容", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        IncludedPaths = included;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static void CollectCheckedFiles(TreeNodeCollection nodes, List<string> result)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Nodes.Count == 0)
            {
                if (node.Checked && node.Tag is string path) result.Add(path);
            }
            else
            {
                CollectCheckedFiles(node.Nodes, result);
            }
        }
    }
}
