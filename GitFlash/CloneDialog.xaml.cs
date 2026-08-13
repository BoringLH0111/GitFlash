using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace GitFlash;

/// <summary>克隆远程仓库的输入对话框：填远程地址 + 保存位置。</summary>
public partial class CloneDialog : Window
{
    /// <summary>用户填写的远程地址</summary>
    public string RemoteUrl => TxtUrl.Text.Trim();

    /// <summary>用户选择的保存目录</summary>
    public string SaveFolder => TxtFolder.Text.Trim();

    public CloneDialog()
    {
        InitializeComponent();
        // 默认保存位置：用户文档下的 GitRepos 文件夹
        TxtFolder.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GitRepos");
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择保存位置" };
        if (dlg.ShowDialog() == true)
            TxtFolder.Text = dlg.FolderName;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        // 简单校验：地址非空、保存目录存在
        if (string.IsNullOrWhiteSpace(RemoteUrl))
        {
            MessageBox.Show("请输入远程仓库地址。", "GitFlash", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(SaveFolder) || !Directory.Exists(SaveFolder))
        {
            MessageBox.Show("请选择存在的保存文件夹。", "GitFlash", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
