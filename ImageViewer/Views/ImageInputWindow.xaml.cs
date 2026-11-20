using System.Windows;
using System.Windows.Controls;
using ImageViewer.ViewModels;

namespace ImageViewer.Views;

/// <summary>承接菜单中的倍率、Gamma 和手动 ROI 输入，复用现有操作命令。</summary>
public partial class ImageInputWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly string _section;

    /// <summary>显示指定操作的输入区域，输入在点击确定前不写回视图模型。</summary>
    /// <param name="viewModel">提供当前输入值和操作命令的主视图模型。</param>
    /// <param name="section">菜单指定的输入区域：Scale、Gamma 或 Roi。</param>
    public ImageInputWindow(MainViewModel viewModel, string section)
    {
        _vm = viewModel;
        _section = section;
        InitializeComponent();
        DataContext = _vm;
        Title = section switch
        {
            "Scale" => "指定缩放比例",
            "Gamma" => "调整 Gamma",
            _ => "手动设置 ROI"
        };
        scalePanel.Visibility = section == "Scale" ? Visibility.Visible : Visibility.Collapsed;
        gammaPanel.Visibility = section == "Gamma" ? Visibility.Visible : Visibility.Collapsed;
        roiPanel.Visibility = section == "Roi" ? Visibility.Visible : Visibility.Collapsed;
        scaleInput.Text = _vm.ScaleInputText;
        gammaInput.Text = _vm.GammaText;
        roiXInput.Text = _vm.RoiXText;
        roiYInput.Text = _vm.RoiYText;
        roiWidthInput.Text = _vm.RoiWText;
        roiHeightInput.Text = _vm.RoiHText;
        _vm.ErrorMessage = string.Empty;
        Loaded += OnLoaded;
    }

    /// <summary>对话框打开时聚焦首个输入框并选中已有内容。</summary>
    /// <param name="sender">当前输入窗口。</param>
    /// <param name="e">窗口加载事件。</param>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TextBox input = _section switch
        {
            "Scale" => scaleInput,
            "Gamma" => gammaInput,
            _ => roiXInput
        };
        input.Focus();
        input.SelectAll();
    }

    /// <summary>将输入交给对应命令，错误留在对话框中，成功后关闭窗口。</summary>
    /// <param name="sender">确定按钮。</param>
    /// <param name="e">按钮点击事件。</param>
    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        _vm.ErrorMessage = string.Empty;
        switch (_section)
        {
            case "Scale":
                _vm.ScaleInputText = scaleInput.Text;
                _vm.ApplyScaleCommand.Execute(null);
                scaleInput.Text = _vm.ScaleInputText;
                break;
            case "Gamma":
                _vm.GammaText = gammaInput.Text;
                _vm.ApplyGammaCommand.Execute(null);
                gammaInput.Text = _vm.GammaText;
                break;
            case "Roi":
                _vm.RoiXText = roiXInput.Text;
                _vm.RoiYText = roiYInput.Text;
                _vm.RoiWText = roiWidthInput.Text;
                _vm.RoiHText = roiHeightInput.Text;
                _vm.ApplyCustomRoiCommand.Execute(null);
                break;
        }

        if (_vm.HasError)
        {
            return;
        }

        DialogResult = true;
    }
}
