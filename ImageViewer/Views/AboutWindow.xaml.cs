using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace ImageViewer.Views;

/// <summary>显示软件信息和可在默认浏览器中打开的 Git 仓库链接。</summary>
public partial class AboutWindow : Window
{
    /// <summary>初始化关于窗口并读取当前应用版本。</summary>
    public AboutWindow()
    {
        InitializeComponent();
        string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "未知";
        versionText.Text = $"版本：{version}";
    }

    /// <summary>在用户点击仓库链接时调用系统默认浏览器。</summary>
    /// <param name="sender">仓库超链接。</param>
    /// <param name="e">包含仓库网址的导航事件。</param>
    private void OnRepositoryNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            MessageBox.Show(this, $"无法打开浏览器，请手动访问：\n{e.Uri.AbsoluteUri}",
                "打开仓库", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
