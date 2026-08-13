using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace GitFlash;

/// <summary>
/// 主窗口：三栏布局（文件浏览器 / 变更区 / 历史对比区）
/// </summary>
public partial class MainWindow : Window
{
    // 左栏收起状态
    private bool _repoPanelCollapsed;
    private double _repoPanelWidth = 240;

    // 仓库列表（本地持久化）与当前仓库
    private readonly List<string> _repoPaths = new();
    private string? _currentRepoPath;

    // 文件内容视图的编辑状态
    private string? _editingFilePath;      // 当前编辑的文件；null 表示未进入编辑（如二进制/大文件/提示态）
    private string _editingOriginal = "";  // 加载或上次保存时的内容，用于判断是否有未保存修改
    private bool _editingHadBom;           // 原文件是否带 UTF-8 BOM，保存时保持一致
    private bool _editingDirty;            // 是否有未保存的修改

    // 仓库列表配置文件（保存在系统应用数据目录，软件重启后还在）
    private static string ConfigFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GitFlash", "repos.json");

    public MainWindow()
    {
        InitializeComponent();
        LoadRepos();
    }

    // ==================== 打开 / 克隆仓库 ====================

    // 打开本地仓库（选择文件夹；如果不是 git 仓库，询问是否初始化）
    private void BtnOpenRepo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择 Git 仓库文件夹" };
        if (dlg.ShowDialog() != true)
            return;

        string path = dlg.FolderName;
        string? top = GetTopLevel(path);

        if (top == null)
        {
            // 不是 git 仓库：询问是否初始化
            var result = MessageBox.Show(
                "该文件夹还不是一个 Git 仓库。\n是否帮你在里面初始化（git init）？",
                "GitFlash", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;
            try
            {
                GitHelper.Run(path, "init");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化失败：{ex.Message}", "GitFlash",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        else if (!Normalize(top).Equals(Normalize(path), StringComparison.OrdinalIgnoreCase))
        {
            // 所选文件夹是某个仓库的子目录：自动使用父仓库
            MessageBox.Show($"「{path}」属于仓库「{top}」\n已自动切换到该仓库。", "GitFlash",
                MessageBoxButton.OK, MessageBoxImage.Information);
            path = top;
        }

        AddRepoToList(path);
        SelectRepo(path);
    }

    // 克隆远程仓库（输入地址，后台执行，避免卡住界面）
    private async void BtnCloneRepo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CloneDialog { Owner = this };
        if (dlg.ShowDialog() != true)
            return;

        string url = dlg.RemoteUrl;
        string parent = dlg.SaveFolder;
        string repoName = Path.GetFileName(url);
        if (repoName.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repoName = repoName[..^4];
        if (string.IsNullOrWhiteSpace(repoName))
            repoName = "repo";
        string dest = Path.Combine(parent, repoName);

        // 目标文件夹已存在且非空，拒绝覆盖
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
        {
            MessageBox.Show($"保存位置已存在非空文件夹：{dest}", "GitFlash",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RepoStatusText.Text = $"正在克隆 {repoName} …";
        try
        {
            await Task.Run(() => GitHelper.RunAsync(parent, "clone", url, dest));
            AddRepoToList(dest);
            SelectRepo(dest);
            RepoStatusText.Text = "克隆完成";
        }
        catch (Exception ex)
        {
            RepoStatusText.Text = "";
            MessageBox.Show($"克隆失败：{ex.Message}", "GitFlash",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==================== 仓库列表管理 ====================

    // 返回所在 git 仓库的根目录；不在任何仓库中时返回 null
    private static string? GetTopLevel(string path)
    {
        try { return GitHelper.Run(path, "rev-parse", "--show-toplevel"); }
        catch { return null; }
    }

    // 判断一个文件夹本身是否为独立仓库的根目录
    // （避免把"仓库内的子文件夹"误判成仓库）
    private static bool IsRepoRoot(string path)
    {
        string? top = GetTopLevel(path);
        return top != null && Normalize(top).Equals(Normalize(path), StringComparison.OrdinalIgnoreCase);
    }

    // 把仓库加入列表（已在列表中则提示并选中），并立即保存
    private void AddRepoToList(string path)
    {
        string full = Normalize(path);
        if (_repoPaths.Any(p => p.Equals(full, StringComparison.OrdinalIgnoreCase)))
        {
            // 已在列表中：给出提示并直接选中，避免"点了没反应"的困惑
            MessageBox.Show("该仓库已在列表中。", "GitFlash", MessageBoxButton.OK, MessageBoxImage.Information);
            SelectRepo(full);
            return;
        }

        RefreshRepoPlaceholder();          // 先清掉占位项
        _repoPaths.Add(full);
        RepoListBox.Items.Add(CreateRepoItem(full));
        SaveRepos();
    }

    // 选中列表中的某个仓库
    private void SelectRepo(string path)
    {
        string full = Normalize(path);
        foreach (ListBoxItem item in RepoListBox.Items)
        {
            if (item.Tag is string p && p.Equals(full, StringComparison.OrdinalIgnoreCase))
            {
                RepoListBox.SelectedItem = item;
                return;
            }
        }
    }

    // 切换仓库：验证存在、读取当前分支、更新界面。返回是否切换成功。
    private bool SwitchToRepo(string path)
    {
        // 有未保存的编辑时先处理，避免切换后把改动写错地方
        if (!CheckUnsavedChanges()) return false;

        if (!Directory.Exists(path))
        {
            MessageBox.Show($"仓库文件夹已不存在：{path}\n已从列表中移除。", "GitFlash",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            RemoveRepo(path);
            return false;
        }

        if (!IsRepoRoot(path))
        {
            MessageBox.Show($"「{path}」已不是有效的仓库（可能已被删除或只是某仓库的子目录）。\n已从列表中移除。",
                "GitFlash", MessageBoxButton.OK, MessageBoxImage.Warning);
            RemoveRepo(path);
            return false;
        }

        _currentRepoPath = path;
        RefreshRepoHeader();

        // 刷新中栏变更列表 + 右栏历史；并重置 diff 显示（避免残留上一个仓库的对比）
        RefreshChanges();
        RefreshHistory();
        RefreshFileTree();
        DiffBeforeText.Text = "（点击中间的文件查看代码对比）";
        DiffAfterText.Text = "";
        DiffFileText.Text = "";
        return true;
    }

    // 从列表移除仓库（不改动磁盘上的真实仓库）
    private void RemoveRepo_Click(object sender, RoutedEventArgs e)
    {
        if (RepoListBox.SelectedItem is ListBoxItem item && item.Tag is string path)
            RemoveRepo(path);
    }

    private void RemoveRepo(string path)
    {
        string full = Normalize(path);
        _repoPaths.RemoveAll(p => p.Equals(full, StringComparison.OrdinalIgnoreCase));

        // 注意：不能在遍历集合时删除，先拷贝一份再删
        foreach (ListBoxItem item in RepoListBox.Items.Cast<ListBoxItem>().ToList())
        {
            if (item.Tag is string p && p.Equals(full, StringComparison.OrdinalIgnoreCase))
            {
                RepoListBox.Items.Remove(item);
                break;
            }
        }

        if (_currentRepoPath != null && Normalize(_currentRepoPath).Equals(full, StringComparison.OrdinalIgnoreCase))
        {
            _currentRepoPath = null;
            CurrentRepoText.Text = "未打开仓库";
        }

        RefreshRepoPlaceholder();
        SaveRepos();
    }

    // ==================== 辅助方法 ====================

    // 统一路径格式：转为绝对路径并去掉末尾斜杠
    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd('\\', '/');

    // 读取当前分支名
    private static string GetCurrentBranch(string path)
    {
        try { return GitHelper.Run(path, "branch", "--show-current"); }
        catch { return ""; }
    }

    // 刷新工具栏右上角的「仓库名 · 分支」显示
    private void RefreshRepoHeader()
    {
        if (_currentRepoPath == null)
        {
            CurrentRepoText.Text = "未打开仓库";
            return;
        }
        string name = Path.GetFileName(_currentRepoPath.TrimEnd('\\', '/'));
        string branch = GetCurrentBranch(_currentRepoPath);
        CurrentRepoText.Text = string.IsNullOrEmpty(branch)
            ? $"{name} · 暂无提交"
            : $"{name} · {branch}";
    }

    // 构造列表项：仓库名 + 路径（路径存在 Tag 里，供后续取用）
    private static ListBoxItem CreateRepoItem(string path)
    {
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        var item = new ListBoxItem
        {
            Tag = path,
            Padding = new Thickness(6, 4, 6, 4),
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.Medium });
        sp.Children.Add(new TextBlock { Text = path, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)) });
        item.Content = sp;
        return item;
    }

    // 列表为空时显示占位提示；添加真实仓库前先清理占位
    private void RefreshRepoPlaceholder()
    {
        for (int i = RepoListBox.Items.Count - 1; i >= 0; i--)
        {
            if (RepoListBox.Items[i] is ListBoxItem it && it.Tag == null)
                RepoListBox.Items.RemoveAt(i);
        }
        if (RepoListBox.Items.Count == 0)
        {
            RepoListBox.Items.Add(new ListBoxItem
            {
                Content = "（还没有仓库\n点上方「打开仓库」或「克隆仓库」）",
                IsEnabled = false,
                Padding = new Thickness(4),
            });
        }
    }

    // 仓库列表切换事件
    private void RepoListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RepoListBox.SelectedItem is ListBoxItem item && item.Tag is string path)
        {
            // 切换被取消（如未保存修改点了"取消"）时，恢复原来的选择
            if (!SwitchToRepo(path) && e.RemovedItems.Count > 0 && e.RemovedItems[0] is ListBoxItem prev)
                RepoListBox.SelectedItem = prev;
        }
    }

    // ==================== 持久化 ====================

    // 启动时加载上次保存的仓库列表
    private void LoadRepos()
    {
        int removed = 0;
        try
        {
            if (File.Exists(ConfigFile))
            {
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ConfigFile));
                if (list != null)
                {
                    foreach (var p in list)
                    {
                        if (!Directory.Exists(p))
                            continue;        // 文件夹已不存在
                        if (!IsRepoRoot(p))
                        {
                            removed++;       // 不是独立仓库（如只是某仓库的子目录）
                            continue;
                        }
                        // 数据与界面列表要同步添加，否则仓库在数据里却不显示
                        _repoPaths.Add(Normalize(p));
                        RepoListBox.Items.Add(CreateRepoItem(Normalize(p)));
                    }
                }
            }
        }
        catch
        {
            // 配置文件损坏时忽略，不影响使用
        }

        if (removed > 0)
        {
            MessageBox.Show($"检测到 {removed} 个无效仓库，已自动从列表中移除。", "GitFlash",
                MessageBoxButton.OK, MessageBoxImage.Information);
            SaveRepos();   // 顺手清理配置文件
        }

        RefreshRepoPlaceholder();
        if (_repoPaths.Count > 0)
            SelectRepo(_repoPaths[0]);   // 自动选中第一个仓库
    }

    // 保存仓库列表到配置文件
    private void SaveRepos()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
            File.WriteAllText(ConfigFile, JsonSerializer.Serialize(_repoPaths));
        }
        catch
        {
            // 保存失败不阻塞使用
        }
    }

    // ==================== 中栏：变更区 ====================

    // 读取并显示当前仓库的未提交改动
    private void RefreshChanges()
    {
        ChangesListBox.Items.Clear();

        if (_currentRepoPath == null)
        {
            ChangesListBox.Items.Add(new ListBoxItem { Content = "未打开仓库", IsEnabled = false, Padding = new Thickness(4) });
            return;
        }

        List<ChangeFile> files;
        try
        {
            files = ChangeFile.Parse(GitHelper.Run(_currentRepoPath, "status", "--porcelain"));
        }
        catch (Exception ex)
        {
            ChangesListBox.Items.Add(new ListBoxItem { Content = $"读取变更失败：{ex.Message}", IsEnabled = false, Padding = new Thickness(4) });
            return;
        }

        var staged = files.Where(f => f.IsStaged).ToList();
        var unstaged = files.Where(f => !f.IsStaged).ToList();

        if (staged.Count == 0 && unstaged.Count == 0)
        {
            ChangesListBox.Items.Add(new ListBoxItem { Content = "工作区干净，没有改动", IsEnabled = false, Padding = new Thickness(4) });
            return;
        }

        if (staged.Count > 0)
        {
            AddGroupHeader($"已暂存变更（{staged.Count}）");
            foreach (var f in staged) AddChangeItem(f);
        }
        if (unstaged.Count > 0)
        {
            AddGroupHeader($"未暂存变更（{unstaged.Count}）");
            foreach (var f in unstaged) AddChangeItem(f);
        }
    }

    // 添加分组标题项（不可选中）
    private void AddGroupHeader(string title)
    {
        ChangesListBox.Items.Add(new ListBoxItem
        {
            Content = title,
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            Padding = new Thickness(2, 6, 2, 2),
        });
    }

    // 添加一个文件变更项（路径 + 状态徽标）
    private void AddChangeItem(ChangeFile f)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = f.Path,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        sp.Children.Add(new TextBlock
        {
            Text = BadgeText(f),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x3F, 0xE3)),
        });

        ChangesListBox.Items.Add(new ListBoxItem { Content = sp, Tag = f, Padding = new Thickness(6, 3, 6, 3) });
    }

    // 状态徽标文本
    private static string BadgeText(ChangeFile f)
    {
        if (f.IsStaged)
        {
            return f.StagedStatus switch
            {
                'A' => "＋ 新增",
                'D' => "－ 删除",
                'R' => "⇄ 重命名",
                _ => "✓ 已暂存",
            };
        }
        if (f.IsUntracked) return "＋ 新增";
        return f.WorktreeStatus switch
        {
            'D' => "－ 删除",
            'R' => "⇄ 重命名",
            _ => "✎ 修改",
        };
    }

    // 取当前选中的文件变更
    private ChangeFile? GetSelectedChange()
    {
        return ChangesListBox.SelectedItem is ListBoxItem { Tag: ChangeFile f } ? f : null;
    }

    // 右键时自动选中鼠标下的文件项（否则右键菜单不知道操作哪个文件）
    private void ChangesListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not ListBoxItem)
            element = VisualTreeHelper.GetParent(element);
        if (element is ListBoxItem item)
            ChangesListBox.SelectedItem = item;
    }

    // 点击文件 → 右栏切换到对比视图并显示 diff
    private void ChangesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GetSelectedChange() is ChangeFile f)
        {
            BtnToDiff_Click(sender, e);
            RefreshDiff(f);
        }
    }

    // ===== 变更操作：暂存 / 取消暂存 / 撤回 =====

    // 暂存全部（包含未跟踪的新文件）
    private void StageAll_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureRepo()) return;
        if (!RunGitSafely("add", "-A")) return;
        RepoStatusText.Text = "已暂存全部";
        RefreshChanges();
    }

    // 暂存选中文件
    private void StageFile_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureRepo()) return;
        if (GetSelectedChange() is not ChangeFile f) return;
        if (!RunGitSafely("add", "--", f.Path)) return;
        RefreshChanges();
    }

    // 取消暂存选中文件
    private void UnstageFile_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureRepo()) return;
        if (GetSelectedChange() is not ChangeFile f || !f.IsStaged) return;
        if (!RunGitSafely("restore", "--staged", "--", f.Path)) return;
        RefreshChanges();
    }

    // 撤回单个文件的修改（危险，二次确认）
    private void RevertFile_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureRepo()) return;
        if (GetSelectedChange() is not ChangeFile f) return;
        var result = MessageBox.Show(
            $"确定要丢弃「{f.Path}」的所有未提交修改吗？\n此操作无法撤销！",
            "危险操作", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        if (!RunGitSafely("restore", "--", f.Path)) return;
        RefreshChanges();
    }

    // 撤回所有已跟踪文件的修改（危险，二次确认；未跟踪新文件不删除）
    private void RevertAll_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureRepo()) return;
        var result = MessageBox.Show(
            "确定要丢弃所有已跟踪文件的修改吗？\n未跟踪的新文件不会被删除。\n此操作无法撤销！",
            "危险操作", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        if (!RunGitSafely("restore", ".")) return;
        RefreshChanges();
    }

    // 手动刷新（外部编辑器改完文件后可用）
    private void RefreshChanges_Click(object sender, RoutedEventArgs e)
    {
        RefreshChanges();
    }

    // 提交已暂存的内容
    private void CommitButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureRepo()) return;

        string msg = CommitMessageBox.Text.Trim();
        if (string.IsNullOrEmpty(msg))
        {
            MessageBox.Show("请先填写提交说明。", "GitFlash", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!RunGitSafely("commit", "-m", msg))
            return;

        CommitMessageBox.Text = "";
        RepoStatusText.Text = "提交成功";
        RefreshChanges();
        RefreshRepoHeader();   // 提交后分支信息可能变化（如从"暂无提交"变为有分支）
        RefreshHistory();      // 提交历史里出现新记录
    }

    // 确保已打开仓库
    private bool EnsureRepo()
    {
        if (_currentRepoPath != null) return true;
        MessageBox.Show("请先在左侧打开一个仓库。", "GitFlash", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    // 执行 git 命令，失败时弹窗提示并返回 false
    private bool RunGitSafely(params string[] args)
    {
        try
        {
            GitHelper.Run(_currentRepoPath!, args);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "GitFlash", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ==================== 界面交互 ====================

    // 收起 / 展开左栏
    private void BtnToggleRepo_Click(object sender, RoutedEventArgs e)
    {
        if (_repoPanelCollapsed)
        {
            ColRepo.Width = new GridLength(_repoPanelWidth);
            Splitter1.Visibility = Visibility.Visible;
            BtnToggleRepo.Content = "◂ 收起仓库栏";
            _repoPanelCollapsed = false;
        }
        else
        {
            if (ColRepo.ActualWidth > 0)
                _repoPanelWidth = ColRepo.ActualWidth;
            ColRepo.Width = new GridLength(0);
            Splitter1.Visibility = Visibility.Collapsed;
            BtnToggleRepo.Content = "▸ 展开仓库栏";
            _repoPanelCollapsed = true;
        }
    }

    // 右栏切换到「历史视图」
    private void BtnToHistory_Click(object sender, RoutedEventArgs e)
    {
        HistoryPanel.Visibility = Visibility.Visible;
        DiffPanel.Visibility = Visibility.Collapsed;
        FilePanel.Visibility = Visibility.Collapsed;
        CommitPanel.Visibility = Visibility.Collapsed;
    }

    // 右栏切换到「对比视图」
    private void BtnToDiff_Click(object sender, RoutedEventArgs e)
    {
        HistoryPanel.Visibility = Visibility.Collapsed;
        DiffPanel.Visibility = Visibility.Visible;
        FilePanel.Visibility = Visibility.Collapsed;
        CommitPanel.Visibility = Visibility.Collapsed;
    }

    // ==================== 右栏：历史视图（分支 + 提交历史） ====================

    // 刷新右栏历史视图的全部内容
    private void RefreshHistory()
    {
        RefreshBranches();
        RefreshCommits();
    }

    // 读取并显示分支列表：
    //  - 本地分支（★ 标记当前分支）
    //  - 远程分支（origin/xxx）：克隆后本地只有默认分支，点它可自动创建本地跟踪分支
    private void RefreshBranches()
    {
        BranchListBox.Items.Clear();

        if (_currentRepoPath == null)
        {
            BranchListBox.Items.Add(new ListBoxItem { Content = "未打开仓库", IsEnabled = false, Padding = new Thickness(4) });
            return;
        }

        List<(string Name, bool Current)> locals;
        List<string> remotes = new();
        try
        {
            // 本地分支：用 for-each-ref 获取，每行形如 "*|master"（当前分支）或 " |dev"。
            // %(HEAD) 对当前分支输出 *，其余输出空格；分支名不允许含 |，可安全作分隔符。
            string output = GitHelper.Run(_currentRepoPath,
                "for-each-ref", "refs/heads", "--format=%(HEAD)|%(refname:short)");
            locals = new List<(string, bool)>();
            foreach (var raw in output.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                int sep = line.IndexOf('|');
                if (sep < 0) continue;
                string name = line[(sep + 1)..].Trim();
                if (name.Length == 0) continue;
                locals.Add((name, line[..sep].Trim() == "*"));
            }

            // 远程分支：refs/remotes 下的所有跟踪分支（如 origin/dev），排除 */HEAD
            string remoteOut = GitHelper.Run(_currentRepoPath,
                "for-each-ref", "refs/remotes", "--format=%(refname:short)");
            foreach (var raw in remoteOut.Split('\n'))
            {
                string name = raw.TrimEnd('\r').Trim();
                if (name.Length == 0) continue;
                if (name.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase)) continue;
                remotes.Add(name);
            }
        }
        catch (Exception ex)
        {
            BranchListBox.Items.Add(new ListBoxItem { Content = $"读取分支失败：{ex.Message}", IsEnabled = false, Padding = new Thickness(4) });
            return;
        }

        if (locals.Count == 0 && remotes.Count == 0)
        {
            BranchListBox.Items.Add(new ListBoxItem { Content = "（暂无分支）", IsEnabled = false, Padding = new Thickness(4) });
            return;
        }

        var currentItem = default(ListBoxItem);

        if (locals.Count > 0)
        {
            BranchListBox.Items.Add(new ListBoxItem
            {
                Content = $"本地分支（{locals.Count}）",
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                Padding = new Thickness(2, 6, 2, 2),
            });
            foreach (var (name, current) in locals)
            {
                var item = CreateBranchItem(name, current ? "★ " : "", current);
                BranchListBox.Items.Add(item);
                if (current) currentItem = item;
            }
        }

        if (remotes.Count > 0)
        {
            BranchListBox.Items.Add(new ListBoxItem
            {
                Content = "远程分支",
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                Padding = new Thickness(2, 6, 2, 2),
            });
            foreach (var name in remotes)
            {
                BranchListBox.Items.Add(CreateBranchItem(name, "⇣ ", false));
            }
        }

        // 默认选中当前分支（注意：设置 SelectedItem 会触发切换事件，用标志位跳过）
        if (currentItem != null)
        {
            _suppressBranchSwitch = true;
            BranchListBox.SelectedItem = currentItem;
            _suppressBranchSwitch = false;
        }
    }

    // 创建一个分支列表项
    private static ListBoxItem CreateBranchItem(string name, string prefix, bool current)
    {
        var item = new ListBoxItem { Tag = name, Padding = new Thickness(6, 3, 6, 3) };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = prefix,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x3F, 0xE3)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text = name,
            FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        item.Content = sp;
        return item;
    }

    // 读取并显示提交历史（最多 50 条）
    private void RefreshCommits()
    {
        CommitListBox.Items.Clear();
        if (_currentRepoPath == null)
            return;

        List<CommitInfo> commits;
        try
        {
            commits = CommitInfo.Parse(GitHelper.Run(_currentRepoPath, "log", "-n", "50",
                "--date=format:%Y-%m-%d %H:%M",
                "--pretty=format:%h%x00%an%x00%ad%x00%s%x00%H"));
        }
        catch (Exception ex)
        {
            CommitListBox.Items.Add(new ListBoxItem { Content = $"读取提交历史失败：{ex.Message}", IsEnabled = false, Padding = new Thickness(4) });
            return;
        }

        if (commits.Count == 0)
        {
            CommitListBox.Items.Add(new ListBoxItem { Content = "（该分支还没有提交）", IsEnabled = false, Padding = new Thickness(4) });
            return;
        }

        foreach (var c in commits)
        {
            var item = new ListBoxItem { Tag = c, Padding = new Thickness(6, 4, 6, 4) };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = c.Message,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            sp.Children.Add(new TextBlock
            {
                Text = $"{c.ShortHash} · {c.Author} · {c.Time}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            });
            item.Content = sp;
            CommitListBox.Items.Add(item);
        }
    }

    // ==================== 右栏：提交详情（点击历史提交查看） ====================

    // 当前正在查看详情的提交完整哈希（文件列表切换 diff 时要用）
    private string? _viewingCommitHash;

    // 点击提交历史中的某条记录 → 显示该次提交修改了哪些文件及代码对比
    private void CommitListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommitListBox.SelectedItem is ListBoxItem { Tag: CommitInfo c })
            ShowCommitDetail(c);
    }

    private void ShowCommitDetail(CommitInfo c)
    {
        _viewingCommitHash = c.FullHash;
        CommitDetailTitle.Text = c.Message;
        CommitDetailMeta.Text = $"{c.FullHash} · {c.Author} · {c.Time}";
        CommitFileListBox.Items.Clear();
        CommitDiffBeforeText.Text = "";
        CommitDiffAfterText.Text = "";

        var green = new SolidColorBrush(Color.FromRgb(0x1E, 0x7D, 0x32));
        var red = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

        // 用 --numstat 读取该提交修改的文件列表（每行：新增数\t删除数\t路径）
        List<(int Added, int Deleted, string Path)> files = new();
        try
        {
            string stat = GitHelper.Run(_currentRepoPath!, "show", "--numstat", "--format=", c.FullHash);
            foreach (var raw in stat.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                // 二进制文件 git 会输出 "-"，这里按 0 处理
                int added = int.TryParse(parts[0], out var a) ? a : 0;
                int deleted = int.TryParse(parts[1], out var d) ? d : 0;
                files.Add((added, deleted, parts[2]));
            }
        }
        catch (Exception ex)
        {
            CommitDiffBeforeText.Text = $"读取提交详情失败：{ex.Message}";
            return;
        }

        if (files.Count == 0)
        {
            CommitFileListBox.Items.Add(new ListBoxItem
            { Content = "（该提交没有文件改动）", IsEnabled = false, Padding = new Thickness(4) });
        }
        else
        {
            foreach (var (added, deleted, path) in files)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock
                {
                    Text = path,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                sp.Children.Add(new TextBlock
                {
                    Text = $" +{added}", FontSize = 11, Foreground = green,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                sp.Children.Add(new TextBlock
                {
                    Text = $" -{deleted}", FontSize = 11, Foreground = red,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                CommitFileListBox.Items.Add(new ListBoxItem { Content = sp, Tag = path, Padding = new Thickness(6, 3, 6, 3) });
            }
            // 默认选中第一个文件（会触发下面的 SelectionChanged 加载 diff）
            CommitFileListBox.SelectedIndex = 0;
        }

        // 切到提交详情视图
        CommitPanel.Visibility = Visibility.Visible;
        HistoryPanel.Visibility = Visibility.Collapsed;
        DiffPanel.Visibility = Visibility.Collapsed;
        FilePanel.Visibility = Visibility.Collapsed;
    }

    // 点击「修改的文件」列表中的某一项 → 显示该文件的代码对比
    private void CommitFileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentRepoPath == null || _viewingCommitHash == null) return;
        if (CommitFileListBox.SelectedItem is not ListBoxItem { Tag: string path }) return;

        try
        {
            string diff = GitHelper.Run(_currentRepoPath, "show", _viewingCommitHash, "--", path);
            FillDiffColumns(CommitDiffBeforeText, CommitDiffAfterText, diff);
        }
        catch (Exception ex)
        {
            FillDiffColumns(CommitDiffBeforeText, CommitDiffAfterText, $"读取对比失败：{ex.Message}");
        }
    }

    // 选中分支即切换（用标志位防止刷新列表时误触发）
    private bool _suppressBranchSwitch;

    private void BranchListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBranchSwitch) return;
        if (BranchListBox.SelectedItem is ListBoxItem { Tag: string branch })
            SwitchBranch(branch);
    }

    // 右键菜单：切换到选中分支
    private void SwitchBranch_Click(object sender, RoutedEventArgs e)
    {
        if (BranchListBox.SelectedItem is ListBoxItem { Tag: string branch })
            SwitchBranch(branch);
    }

    // 执行切换分支；分支名可能是本地分支（master）或远程分支（origin/dev）。
    // 切远程分支时，本地没有同名分支会自动创建跟踪分支（git checkout -b dev origin/dev）。
    private void SwitchBranch(string branch)
    {
        if (_currentRepoPath == null) return;
        branch = branch.Trim();
        if (branch.Length == 0) return;
        string current = GetCurrentBranch(_currentRepoPath);

        // 远程分支 → 去掉 origin/ 前缀得到本地分支名
        bool fromRemote = branch.StartsWith("origin/", StringComparison.OrdinalIgnoreCase);
        string target = fromRemote ? branch["origin/".Length..] : branch;
        if (target.Length == 0) return;
        if (target.Equals(current, StringComparison.OrdinalIgnoreCase))
            return;

        // 有未保存的编辑时先处理，避免切换分支后把编辑内容写错地方
        if (!CheckUnsavedChanges()) return;

        // 有未提交修改时先确认（切换可能把修改带到新分支，甚至失败）
        if (!ConfirmUncommittedSwitch()) return;

        // 本地不存在同名分支且目标来自远程 → 创建跟踪分支
        bool created = fromRemote && !LocalBranchExists(target);
        if (created)
        {
            if (!RunGitSafely("checkout", "-b", target, branch))
            {
                RefreshBranches();   // 失败：恢复选中当前分支，避免列表与实际不符
                return;
            }
        }
        else
        {
            if (!RunGitSafely("checkout", target))
            {
                RefreshBranches();
                return;
            }
        }

        RepoStatusText.Text = created ? $"已创建并切换到分支 {target}" : $"已切换到 {target}";
        RefreshRepoHeader();
        RefreshHistory();
        RefreshChanges();
        RefreshFileTree();
    }

    // 本地是否已存在名为 name 的分支
    private bool LocalBranchExists(string name)
    {
        try
        {
            return !string.IsNullOrEmpty(GitHelper.Run(_currentRepoPath!,
                "rev-parse", "--verify", "--quiet", $"refs/heads/{name}"));
        }
        catch { return false; }
    }

    // 有未提交修改时先确认；返回 true 表示可以继续
    private bool ConfirmUncommittedSwitch()
    {
        try
        {
            string st = GitHelper.Run(_currentRepoPath!, "status", "--porcelain");
            if (!string.IsNullOrEmpty(st))
            {
                var r = MessageBox.Show(
                    "当前有未提交的修改，切换分支可能会把修改带到新分支，\n若文件冲突则会切换失败。仍要继续吗？",
                    "GitFlash", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return false;
            }
        }
        catch { /* 读取状态失败不阻止切换 */ }
        return true;
    }

    // 新建分支（从当前分支最新提交创建，创建后自动切换）
    private void NewBranch_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureRepo()) return;

        var dlg = new NewBranchDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;

        string name = dlg.BranchName;
        if (!RunGitSafely("checkout", "-b", name)) return;
        RepoStatusText.Text = $"已创建并切换到分支 {name}";
        RefreshRepoHeader();
        RefreshHistory();
    }

    // ==================== 右栏：拉取 / 推送 ====================

    // 当前分支关联的远程分支名（如 origin/master）；没有关联时返回 null
    private static string? GetUpstream(string path, string branch)
    {
        if (string.IsNullOrEmpty(branch)) return null;
        try
        {
            string u = GitHelper.Run(path, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}");
            return string.IsNullOrEmpty(u) ? null : u;
        }
        catch { return null; }   // 没有 upstream 时 git 会报错，这里视为"未关联"
    }

    // 拉取远程更新（git pull，可能耗时，异步执行）
    private async void Pull_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureRepo()) return;
        if (!HasRemote()) return;

        // 拉取会用远程内容覆盖本地文件，先处理未保存的编辑
        if (!CheckUnsavedChanges()) return;

        // 新分支还没关联远程分支时，git pull 会报错，先给出中文提示
        string branch = GetCurrentBranch(_currentRepoPath!);
        if (GetUpstream(_currentRepoPath!, branch) == null)
        {
            MessageBox.Show(
                $"当前分支「{branch}」还没有关联远程分支，无法直接拉取。\n" +
                "请先点「推送」，把分支发布到远程（会自动建立关联），之后拉取/推送就都能正常使用了。",
                "GitFlash", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RepoStatusText.Text = "正在拉取…";
        try
        {
            await Task.Run(() => GitHelper.RunAsync(_currentRepoPath!, "pull"));
            RepoStatusText.Text = "拉取完成";
            RefreshRepoHeader();
            RefreshHistory();
            RefreshChanges();
            RefreshFileTree();
        }
        catch (Exception ex)
        {
            RepoStatusText.Text = "";
            MessageBox.Show($"拉取失败：{ex.Message}", "GitFlash", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // 推送当前分支（git push -u origin 分支名，首次推送自动建立关联，可能耗时，异步执行）
    private async void Push_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureRepo()) return;
        if (!HasRemote()) return;

        string branch = GetCurrentBranch(_currentRepoPath!);
        if (string.IsNullOrEmpty(branch))
        {
            MessageBox.Show("当前没有处于某个分支上（可能是游离状态），无法推送。", "GitFlash",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 推送只同步「已提交」的内容；有未提交的修改时先提醒，避免用户以为暂存/改动已被推送
        try
        {
            string st = GitHelper.Run(_currentRepoPath!, "status", "--porcelain");
            if (!string.IsNullOrEmpty(st))
            {
                var r = MessageBox.Show(
                    "当前还有未提交的修改（已暂存或未暂存）。\n" +
                    "推送只会把「已提交」的内容同步到远程，这些修改不会出现在 Gitee 上。\n\n" +
                    "点「是」：仍继续推送已提交的内容；\n点「否」：取消，先回中栏完成提交。",
                    "GitFlash", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (r != MessageBoxResult.Yes) return;
            }
        }
        catch { /* 读取状态失败不阻止推送 */ }

        RepoStatusText.Text = "正在推送…";
        try
        {
            string output = await Task.Run(() => GitHelper.RunAsync(_currentRepoPath!, "push", "-u", "origin", branch));
            // push 输出 Everything up-to-date 表示没有新提交可推，给出明确提示
            RepoStatusText.Text = output.Contains("Everything up-to-date", StringComparison.OrdinalIgnoreCase)
                ? "远程已是最新，没有新提交可推送"
                : "推送完成";
        }
        catch (Exception ex)
        {
            RepoStatusText.Text = "";
            MessageBox.Show($"推送失败：{ex.Message}\n\n提示：如果要求输入账号密码，请在 git 弹出的窗口中操作；如果提示被拒绝（non-fast-forward），说明远程有更新的提交，请先点「拉取」合并。",
                "GitFlash", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // 检查仓库是否配置了远程地址
    private bool HasRemote()
    {
        try
        {
            if (string.IsNullOrEmpty(GitHelper.Run(_currentRepoPath!, "remote")))
            {
                MessageBox.Show("该仓库还没有配置远程地址，无法拉取/推送。\n建议使用「克隆仓库」拉取远程仓库，或用命令行配置。",
                    "GitFlash", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"读取远程地址失败：{ex.Message}", "GitFlash", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ==================== 右栏：对比视图（两列 diff） ====================

    // 显示一个文件的工作区/暂存区对比（左列：修改前，右列：修改后）
    private void RefreshDiff(ChangeFile f)
    {
        DiffFileText.Text = f.Path;

        string diff;
        try
        {
            if (f.IsUntracked)
            {
                // 未跟踪的新文件没有历史版本：读取文件内容，全部视为新增
                string full = Path.Combine(_currentRepoPath!, f.Path);
                if (!File.Exists(full))
                {
                    diff = "（文件已不存在）";
                }
                else
                {
                    string content = File.ReadAllText(full);
                    diff = content.Length == 0
                        ? "（空文件）"
                        : string.Join("\n", content.Split('\n').Select(l => "+" + l));
                }
            }
            else if (f.IsStaged && f.WorktreeStatus == ' ')
            {
                // 已暂存且工作区无再改动：对比暂存区与上次提交
                diff = GitHelper.Run(_currentRepoPath!, "diff", "--cached", "--", f.Path);
            }
            else
            {
                // 含未暂存改动：对比工作区与上次提交（暂存+未暂存一起显示）
                diff = GitHelper.Run(_currentRepoPath!, "diff", "HEAD", "--", f.Path);
            }
        }
        catch (Exception ex)
        {
            FillDiffColumns(DiffBeforeText, DiffAfterText, $"读取对比失败：{ex.Message}");
            return;
        }

        FillDiffColumns(DiffBeforeText, DiffAfterText, diff);
    }

    // 把 unified diff 文本填入左右两列（左：修改前，右：修改后），两列行数保持一致实现对齐：
    //  - 删除行(-) 只在左列（红色）；新增行(+) 只在右列（绿色）
    //  - 上下文行两列都显示；标题/行号等元信息行只放左列（灰色）
    private static void FillDiffColumns(TextBlock before, TextBlock after, string diff)
    {
        before.Inlines.Clear();
        after.Inlines.Clear();

        if (string.IsNullOrWhiteSpace(diff))
        {
            before.Text = "（没有可显示的差异）";
            return;
        }

        var green = new SolidColorBrush(Color.FromRgb(0x1E, 0x7D, 0x32));
        var red = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        var gray = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        var dark = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));

        foreach (var raw in diff.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                AddDiffLine(before, "", gray);
                AddDiffLine(after, "", gray);
            }
            else if (line.StartsWith("+++ ") || line.StartsWith("--- ") || line.StartsWith("diff ")
                     || line.StartsWith("index ") || line.StartsWith("@@") || line.StartsWith('\\'))
            {
                // 元信息 / 文件名 / hunk 头：只放左列
                AddDiffLine(before, line, gray);
                AddDiffLine(after, "", gray);
            }
            else if (line.StartsWith('-'))
            {
                AddDiffLine(before, "-" + line[1..], red);
                AddDiffLine(after, "", dark);   // 右列占位空行，保持对齐
            }
            else if (line.StartsWith('+'))
            {
                AddDiffLine(before, "", dark);  // 左列占位空行，保持对齐
                AddDiffLine(after, "+" + line[1..], green);
            }
            else if (line.StartsWith(' '))
            {
                string content = line[1..];
                AddDiffLine(before, content, dark);
                AddDiffLine(after, content, dark);
            }
            else
            {
                AddDiffLine(before, line, gray);
                AddDiffLine(after, "", gray);
            }
        }
    }

    // 向某一列追加一行
    private static void AddDiffLine(TextBlock target, string text, Brush brush)
    {
        var run = new Run(text + "\n") { Foreground = brush };
        target.Inlines.Add(run);
    }

    // ==================== 左栏：仓库文件树 ====================

    // 目录节点还没真正加载时的占位标记（放在 Tag 里，便于判断是否已加载）
    private const string FileTreePlaceholder = "__load__";

    // 重建当前仓库的文件树（懒加载：只建根节点，展开时才逐层读取）
    private void RefreshFileTree()
    {
        // 切换仓库会重建整棵树，先清掉旧文件的编辑状态
        // （否则下面的占位文本会被 TextChanged 误判成对旧文件的修改）
        ResetEditState();
        FileTreeView.Items.Clear();
        FileContentBox.Text = "（点击左侧仓库文件查看内容）";
        FileViewText.Text = "";

        if (_currentRepoPath == null) return;
        string rootName = Path.GetFileName(_currentRepoPath.TrimEnd('\\', '/'));
        var root = CreateDirNode(rootName, _currentRepoPath);
        FileTreeView.Items.Add(root);
        root.IsExpanded = true;   // 展开根目录，触发懒加载第一层
    }

    // 创建目录节点（带占位子项，展开时才加载真实内容，避免大仓库卡顿）
    private static TreeViewItem CreateDirNode(string name, string dirPath)
    {
        var item = new TreeViewItem { Header = name, Tag = dirPath };
        item.Items.Add(new TreeViewItem { Header = "…", Tag = FileTreePlaceholder, IsEnabled = false });
        return item;
    }

    // 目录展开事件：如果还是占位项，则真正读取子目录和文件
    private void FileTreeView_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { Tag: string tag } item
            && tag != FileTreePlaceholder
            && Directory.Exists(tag)
            && item.Items.Count == 1
            && item.Items[0] is TreeViewItem { Tag: string childTag } && childTag == FileTreePlaceholder)
        {
            LoadDirChildren(item, tag);
        }
    }

    // 读取目录下的一层内容（文件夹在前，按名称排序；跳过 .git）
    private static void LoadDirChildren(TreeViewItem dirItem, string dirPath)
    {
        dirItem.Items.Clear();
        try
        {
            foreach (var d in Directory.GetDirectories(dirPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(d);
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                dirItem.Items.Add(CreateDirNode(name, d));
            }
            foreach (var f in Directory.GetFiles(dirPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                dirItem.Items.Add(new TreeViewItem { Header = Path.GetFileName(f), Tag = f });
            }
        }
        catch (Exception ex)
        {
            dirItem.Items.Add(new TreeViewItem { Header = $"读取失败：{ex.Message}", IsEnabled = false });
        }
    }

    // 点击文件（目录被选中时不处理）
    private void FileTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (FileTreeView.SelectedItem is TreeViewItem { Tag: string path } item
            && path != FileTreePlaceholder
            && File.Exists(path))
        {
            // 切换查看的文件前先处理未保存的修改；取消时恢复原来的选中项
            if (!CheckUnsavedChanges())
            {
                if (e.OldValue is TreeViewItem oldItem)
                    oldItem.IsSelected = true;
                return;
            }
            ShowFileContent(path);
        }
    }

    // ==================== 右栏：文件内容查看 / 编辑 ====================

    // 显示仓库文件的文本内容（右栏第三个视图）；普通文本文件可直接编辑
    private void ShowFileContent(string path)
    {
        FileViewText.Text = Path.GetFileName(path);

        try
        {
            var fi = new FileInfo(path);
            const long maxSize = 512 * 1024;   // 超过 512KB 不读取，避免卡顿
            if (fi.Length > maxSize)
            {
                FileContentBox.IsReadOnly = true;
                FileContentBox.Text = $"（文件较大（{fi.Length / 1024.0:F0} KB），为避免卡顿暂不显示）";
                ResetEditState();
            }
            else
            {
                byte[] bytes = File.ReadAllBytes(path);
                // 前 8000 字节里出现 NUL 就认为是二进制文件
                bool binary = bytes.Take(Math.Min(bytes.Length, 8000)).Any(b => b == 0);
                if (binary)
                {
                    FileContentBox.IsReadOnly = true;
                    FileContentBox.Text = "（二进制文件，无法以文本显示）";
                    ResetEditState();
                }
                else
                {
                    bool hadBom = bytes.Length >= 3
                        && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                    string content = Encoding.UTF8.GetString(bytes);

                    // 先记录"原始内容"，再填入文本框：
                    // 填充本身会触发 TextChanged，此时 Text == Original，不会误报脏状态
                    _editingFilePath = path;
                    _editingOriginal = content;
                    _editingHadBom = hadBom;
                    _editingDirty = false;
                    FileContentBox.IsReadOnly = false;
                    FileContentBox.Text = content;
                    SaveFileButton.IsEnabled = false;
                }
            }
        }
        catch (Exception ex)
        {
            FileContentBox.IsReadOnly = true;
            FileContentBox.Text = $"无法读取文件：{ex.Message}";
            ResetEditState();
        }

        // 切到文件内容视图
        FilePanel.Visibility = Visibility.Visible;
        HistoryPanel.Visibility = Visibility.Collapsed;
        DiffPanel.Visibility = Visibility.Collapsed;
        CommitPanel.Visibility = Visibility.Collapsed;
    }

    // 清空编辑状态（进入提示态/二进制/大文件，或重建文件树前调用）
    private void ResetEditState()
    {
        _editingFilePath = null;
        _editingOriginal = "";
        _editingHadBom = false;
        _editingDirty = false;
        SaveFileButton.IsEnabled = false;
    }

    // 文本框内容变化：对比"原始内容"更新脏状态，并控制保存按钮可用性
    private void FileContentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 未进入编辑态（占位提示/二进制/大文件）时不计算脏状态
        if (_editingFilePath == null)
        {
            SaveFileButton.IsEnabled = false;
            return;
        }
        _editingDirty = !string.Equals(FileContentBox.Text, _editingOriginal, StringComparison.Ordinal);
        SaveFileButton.IsEnabled = _editingDirty;
    }

    // 保存当前编辑的文件（保持原文件的 UTF-8 BOM 设定）。返回是否保存成功。
    private bool SaveFile(string path)
    {
        try
        {
            string text = FileContentBox.Text;
            var enc = new UTF8Encoding(_editingHadBom);
            File.WriteAllBytes(path, enc.GetBytes(text));

            _editingOriginal = text;   // 以本次保存为新的"原始内容"
            _editingDirty = false;
            SaveFileButton.IsEnabled = false;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", "GitFlash",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // 点「保存」按钮
    private void SaveFile_Click(object sender, RoutedEventArgs e)
    {
        if (_editingFilePath == null) return;
        if (!SaveFile(_editingFilePath)) return;
        RepoStatusText.Text = "已保存";
        RefreshChanges();   // 内容变了，变更区的状态/对比需要刷新
    }

    // 检查是否有未保存的修改：有则弹窗让用户选择。
    // 返回 false 表示用户选择"取消"，应立即中止当前操作。
    private bool CheckUnsavedChanges()
    {
        if (_editingFilePath == null || !_editingDirty)
            return true;

        string name = Path.GetFileName(_editingFilePath);
        var result = MessageBox.Show(
            $"文件「{name}」有未保存的修改。\n\n" +
            "是：保存修改后继续\n否：放弃修改，继续当前操作\n取消：返回继续编辑",
            "未保存的修改", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
            return SaveFile(_editingFilePath);   // 保存失败时也不继续
        return result == MessageBoxResult.No;    // 只有"放弃修改"才放行
    }
}
