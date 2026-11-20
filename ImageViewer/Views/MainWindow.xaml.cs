using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ImageViewer.ViewModels;

namespace ImageViewer.Views;

/// <summary>承接窗口级快捷键和文件拖放，图像交互由画布与视图模型处理。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>创建主窗口并连接画布。</summary>
    /// <param name="viewModel">应用容器提供的主视图模型。</param>
    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        InitializeComponent();
        DataContext = _vm;
        imageCanvas.Attach(_vm);
        PreviewDrop += OnFileDrop;
        PreviewDragOver += OnDragOver;
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += OnDeactivated;
    }

    /// <summary>只接受单个文件，加载完成后将键盘焦点交还画布。</summary>
    /// <param name="sender">拖入目标。</param>
    /// <param name="e">拖放数据。</param>
    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files)
        {
            _vm.OnFileDrop(files[0]);
            imageCanvas.Focus();
        }

        e.Handled = true;
    }

    /// <summary>单文件显示打开反馈，多文件或其他数据不接受。</summary>
    /// <param name="sender">拖入目标。</param>
    /// <param name="e">拖放数据。</param>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 }
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>处理查看快捷键，菜单导航和输入控件优先使用自身的键盘行为。</summary>
    /// <param name="sender">主窗口。</param>
    /// <param name="e">键盘事件。</param>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (mainMenu.IsKeyboardFocusWithin || mainMenu.Items.OfType<MenuItem>().Any(item => item.IsSubmenuOpen))
        {
            return;
        }

        bool control = Keyboard.Modifiers == ModifierKeys.Control;
        ICommand? command = null;
        if (control)
        {
            command = e.Key switch
            {
                Key.O => _vm.OpenFileCommand,
                Key.D0 or Key.NumPad0 => _vm.AutoFitCommand,
                Key.D1 or Key.NumPad1 => _vm.InitSizeCommand,
                _ => null
            };
        }

        bool editing = Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem;
        if (command == null && editing)
        {
            return;
        }

        if (command == null && control && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            double dx = e.Key == Key.Left ? -40 : e.Key == Key.Right ? 40 : 0;
            double dy = e.Key == Key.Up ? -40 : e.Key == Key.Down ? 40 : 0;
            _vm.PanBy(dx, dy);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            command = e.Key switch
            {
                Key.R => _vm.ToggleRoiCommand,
                Key.C => _vm.ToggleCrosshairCommand,
                Key.Escape => _vm.EscapeCommand,
                _ => null
            };
        }

        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>切换到其他窗口时结束未提交的操作，避免鼠标捕获滞留。</summary>
    /// <param name="sender">主窗口。</param>
    /// <param name="e">失去激活事件。</param>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        _vm.CancelPointerInteraction();
    }

    /// <summary>打开菜单指定的倍率、Gamma 或 ROI 输入窗口。</summary>
    /// <param name="sender">带有输入区域标记的菜单项。</param>
    /// <param name="e">菜单点击事件。</param>
    private void OnEditImageInputClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string section })
        {
            var window = new ImageInputWindow(_vm, section)
            {
                Owner = this
            };
            window.ShowDialog();
            imageCanvas.Focus();
        }
    }

    /// <summary>更新叠加颜色，保持颜色子菜单中恰好一项勾选。</summary>
    /// <param name="sender">带有颜色标记的菜单项。</param>
    /// <param name="e">菜单点击事件。</param>
    private void OnOverlayColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } selectedItem)
        {
            _vm.BrushColor = tag switch
            {
                "Green" => Colors.Green,
                "Blue" => Colors.Blue,
                _ => Colors.Red
            };

            foreach (MenuItem item in overlayColorMenu.Items.OfType<MenuItem>())
            {
                item.IsChecked = ReferenceEquals(item, selectedItem);
            }
        }
    }
}
