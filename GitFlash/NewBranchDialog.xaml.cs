using System.Windows;
using System.Windows.Input;

namespace GitFlash;

/// <summary>新建分支的输入对话框：填分支名称。</summary>
public partial class NewBranchDialog : Window
{
    /// <summary>用户填写的分支名称</summary>
    public string BranchName => TxtBranch.Text.Trim();

    public NewBranchDialog()
    {
        InitializeComponent();
        TxtBranch.Focus();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BranchName))
        {
            MessageBox.Show("请输入分支名称。", "GitFlash", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    // 回车确认
    private void TxtBranch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            BtnOk_Click(sender, e);
    }
}
