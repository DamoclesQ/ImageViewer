using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageViewer.Models;
using ImageViewer.Views;
using Microsoft.Win32;

namespace ImageViewer.ViewModels;

/// <summary>
/// 管理图片查看操作。原图坐标原点位于左上角边界，像素中心为 (x + 0.5, y + 0.5)。
/// 已确认的 ROI 与拖动预览分别保存，显示调整不修改原始像素。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const double ScaleMax = 50.0;
    private const double ZoomStep = 1.1;
    /// <summary>图片移动后保留的可见尺寸，以及缩小后的目标最小长边，单位为 WPF DIP。</summary>
    private const double MinVisibleSize = 50.0;
    private readonly ImageModel _imageModel;
    private Point _dragStart;
    private Point _roiStartPixel;
    private int _lastPixelX = -1;
    private int _lastPixelY = -1;

    /// <summary>供画布读取的图像数据。</summary>
    public ImageModel ImageData => _imageModel;

    /// <summary>缩小后长边至少为 50 DIP，小图最低为原始尺寸；画布更小时允许完整适配。</summary>
    private double ScaleMin
    {
        get
        {
            double minimumImageScale = MinVisibleSize / Math.Max(_imageModel.Width, _imageModel.Height);
            double fitScale = Math.Min(CanvasWidth / _imageModel.Width, CanvasHeight / _imageModel.Height);
            return Math.Min(1.0, Math.Min(minimumImageScale, fitScale));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUsePseudoColor))]
    [NotifyCanExecuteChangedFor(nameof(AutoFitCommand), nameof(InitSizeCommand), nameof(ApplyScaleCommand),
        nameof(ToggleRoiCommand), nameof(ApplyCustomRoiCommand), nameof(ToggleCrosshairCommand),
        nameof(ToggleTextBackCommand), nameof(ToggleSmoothScalingCommand), nameof(TogglePseudoColorCommand),
        nameof(ApplyGammaCommand), nameof(ResetGammaCommand))]
    private bool _hasImage;

    /// <summary>伪彩色仅适用于已加载的单通道图像。</summary>
    public bool CanUsePseudoColor => HasImage && _imageModel.Channels == 1;

    [ObservableProperty] private double _imgScale = 1.0;
    [ObservableProperty] private string _scaleInputText = "100";
    [ObservableProperty] private string _scaleText = string.Empty;
    [ObservableProperty] private double _showX;
    [ObservableProperty] private double _showY;
    [ObservableProperty] private double _showW;
    [ObservableProperty] private double _showH;
    [ObservableProperty] private double _canvasWidth = 800;
    [ObservableProperty] private double _canvasHeight = 600;
    [ObservableProperty] private bool _isAutoFit;
    [ObservableProperty] private bool _isSmoothScaling;
    [ObservableProperty] private string _imageSizeText = string.Empty;
    [ObservableProperty] private string _imageTypeText = string.Empty;
    [ObservableProperty] private string _pixelLocationText = string.Empty;
    [ObservableProperty] private string _pixelValueText = string.Empty;
    [ObservableProperty] private string _imgInfoText = string.Empty;
    [ObservableProperty] private string _imgPathToolTip = string.Empty;
    [ObservableProperty] private string _gammaText = "1";
    [ObservableProperty] private string _statusMessage = "打开或拖入图片；滚轮缩放，右键拖动平移。";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    /// <summary>是否需要在顶部工具区显示输入或加载错误。</summary>
    public bool HasError => ErrorMessage.Length > 0;
    [ObservableProperty] private bool _isSelectRoi;
    [ObservableProperty] private bool _isDrawingRoi;
    [ObservableProperty] private Rect _roiPreviewRect;
    [ObservableProperty] private Rect _roiRectOnSrc;
    [ObservableProperty] private string _roiInfoText = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearRoiCommand))]
    private bool _hasRoi;
    [ObservableProperty] private string _roiXText = "0";
    [ObservableProperty] private string _roiYText = "0";
    [ObservableProperty] private string _roiWText = "0";
    [ObservableProperty] private string _roiHText = "0";
    [ObservableProperty] private bool _isPanning;
    [ObservableProperty] private bool _isPseudoColor;
    [ObservableProperty] private bool _isShowCrosshair;
    [ObservableProperty] private bool _isUseTextBack;
    [ObservableProperty] private Color _brushColor = Colors.Red;

    /// <summary>创建主界面状态。</summary>
    /// <param name="imageModel">由应用容器管理的图像模型。</param>
    public MainViewModel(ImageModel imageModel)
    {
        _imageModel = imageModel;
    }

    /// <summary>叠加颜色变化后重绘。</summary>
    /// <param name="value">新的叠加颜色。</param>
    partial void OnBrushColorChanged(Color value)
    {
        RequestRender();
    }

    /// <summary>正常操作反馈更新时清除上一次错误，底部仅保留图像数据。</summary>
    /// <param name="value">新的操作反馈。</param>
    partial void OnStatusMessageChanged(string value)
    {
        ErrorMessage = string.Empty;
    }

    /// <summary>选择并打开单张图片。</summary>
    [RelayCommand]
    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图片文件|*.bmp;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.gif|所有文件|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            LoadImage(dialog.FileName);
        }
    }

    /// <summary>进入自动模式，画布尺寸变化时继续适配。</summary>
    [RelayCommand(CanExecute = nameof(HasImage))]
    private void AutoFit()
    {
        CancelPointerInteraction();
        IsAutoFit = true;
        ErrorMessage = string.Empty;
        FitToCanvas();
    }

    /// <summary>按一个原图像素对应一个 WPF 显示单位居中显示。</summary>
    [RelayCommand(CanExecute = nameof(HasImage))]
    private void InitSize()
    {
        CancelPointerInteraction();
        IsAutoFit = false;
        ErrorMessage = string.Empty;
        SetViewport(1.0, new Point(_imageModel.Width / 2.0, _imageModel.Height / 2.0),
            new Point(CanvasWidth / 2.0, CanvasHeight / 2.0));
    }

    /// <summary>应用指定倍率，以当前视口中心为锚点。</summary>
    [RelayCommand(CanExecute = nameof(HasImage))]
    private void ApplyScale()
    {
        if (!TryParseScale(ScaleInputText, out double scale))
        {
            ScaleInputText = FormatScaleInput(ImgScale);
            StatusMessage = "倍率无效，请输入正数，例如 100、150% 或 2x。";
            ErrorMessage = StatusMessage;
            return;
        }

        var anchor = new Point(CanvasWidth / 2.0, CanvasHeight / 2.0);
        CancelPointerInteraction();
        IsAutoFit = false;
        SetViewport(scale, CanvasToSource(anchor.X, anchor.Y), anchor);
        StatusMessage = "已应用指定倍率。";
    }

    /// <summary>切换 ROI 绘制工具，关闭时保留已有选区。</summary>
    [RelayCommand(CanExecute = nameof(HasImage))]
    private void ToggleRoi()
    {
        CancelPointerInteraction();
        IsSelectRoi = !IsSelectRoi;
        StatusMessage = IsSelectRoi
            ? "ROI：左键拖动框选，单击选择一个像素；Esc 取消当前拖动。"
            : "ROI 工具已关闭，已有选区仍保留；点击清除 ROI 可移除。";
        RequestRender();
    }

    /// <summary>应用手动输入的 ROI，将超出图像的宽高裁切到图内。</summary>
    [RelayCommand(CanExecute = nameof(HasImage))]
    private void ApplyCustomRoi()
    {
        if (!TryParseInt(RoiXText, out int x) || !TryParseInt(RoiYText, out int y) ||
            !TryParseInt(RoiWText, out int w) || !TryParseInt(RoiHText, out int h) ||
            x < 0 || y < 0 || w <= 0 || h <= 0 || x >= _imageModel.Width || y >= _imageModel.Height)
        {
            StatusMessage = "ROI 无效：X、Y 必须在图内，宽、高必须为正整数。";
            ErrorMessage = StatusMessage;
            return;
        }

        CancelPointerInteraction();
        SetRoi(new Rect(x, y, Math.Min(w, _imageModel.Width - x), Math.Min(h, _imageModel.Height - y)));
        RequestRender();
    }

    /// <summary>清除选区和输入框，不改变十字线或显示调整。</summary>
    [RelayCommand(CanExecute = nameof(HasRoi))]
    private void ClearRoi()
    {
        IsDrawingRoi = false;
        RoiPreviewRect = Rect.Empty;
        RoiRectOnSrc = Rect.Empty;
        HasRoi = false;
        RoiInfoText = string.Empty;
        RoiXText = "0";
        RoiYText = "0";
        RoiWText = "0";
        RoiHText = "0";
        StatusMessage = "ROI 已清除。";
        RequestRender();
    }

    /// <summary>切换图像几何中心十字线。</summary>
    [RelayCommand(CanExecute = nameof(HasImage))]
    private void ToggleCrosshair()
    {
        IsShowCrosshair = !IsShowCrosshair;
        RequestRender();
    }

    /// <summary>切换高倍率像素文字背景。</summary>
    [RelayCommand(CanExecute = nameof(HasImage))]
    private void ToggleTextBack()
    {
        IsUseTextBack = !IsUseTextBack;
        RequestRender();
    }

    /// <summary>切换平滑浏览，30 倍起仍以最近邻进行像素检查。</summary>
    [RelayCommand(CanExecute = nameof(HasImage))]
    private void ToggleSmoothScaling()
    {
        IsSmoothScaling = !IsSmoothScaling;
        RequestRender();
    }

    /// <summary>切换单通道显示伪彩色。</summary>
    [RelayCommand(CanExecute = nameof(CanUsePseudoColor))]
    private void TogglePseudoColor()
    {
        _imageModel.SetPseudoColorEnabled(!IsPseudoColor);
        IsPseudoColor = _imageModel.IsPseudoColorEnabled;
        RequestRender();
    }

    /// <summary>应用有限且位于 0.05 到 20 范围内的 Gamma。</summary>
    [RelayCommand(CanExecute = nameof(HasImage))]
    private void ApplyGamma()
    {
        if (!TryParseNumber(GammaText, out double gamma) || gamma < 0.05 || gamma > 20.0)
        {
            GammaText = FormatNumber(_imageModel.DisplayGamma);
            StatusMessage = "Gamma 无效，请输入 0.05 到 20 之间的数值。";
            ErrorMessage = StatusMessage;
            return;
        }

        _imageModel.SetGamma(gamma);
        GammaText = FormatNumber(gamma);
        StatusMessage = "Gamma 已应用，原始像素和 ROI 统计不变。";
        RequestRender();
    }

    /// <summary>仅重置 Gamma，保留伪彩色和选区。</summary>
    [RelayCommand(CanExecute = nameof(HasImage))]
    private void ResetGamma()
    {
        _imageModel.SetGamma(1.0);
        GammaText = "1";
        StatusMessage = "Gamma 已重置。";
        RequestRender();
    }

    /// <summary>先取消正在进行的拖动；否则清除 ROI 和十字线。</summary>
    [RelayCommand]
    private void Escape()
    {
        if (IsDrawingRoi || IsPanning)
        {
            CancelPointerInteraction();
            StatusMessage = "当前拖动已结束，已确认的 ROI 保留。";
            return;
        }

        ClearRoi();
        IsSelectRoi = false;
        IsShowCrosshair = false;
        StatusMessage = "ROI 和十字线已清除。";
        RequestRender();
    }

    /// <summary>显示软件信息、当前程序集版本和可点击的 Git 仓库链接。</summary>
    [RelayCommand]
    private static void ShowAbout()
    {
        var window = new AboutWindow
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    /// <summary>同步画布尺寸，自动模式重新适配，手动模式保留原图视口中心。</summary>
    /// <param name="width">画布宽度，单位为 WPF DIP。</param>
    /// <param name="height">画布高度，单位为 WPF DIP。</param>
    public void OnCanvasResized(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var center = new Point(ShowX + ShowW / 2.0, ShowY + ShowH / 2.0);
        CanvasWidth = width;
        CanvasHeight = height;
        if (!HasImage)
        {
            return;
        }

        CancelPointerInteraction();
        if (IsAutoFit)
        {
            FitToCanvas();
            return;
        }

        SetViewport(ImgScale, center, new Point(width / 2.0, height / 2.0));
    }

    /// <summary>把画布位置换算为原图边界坐标，允许结果位于图外。</summary>
    /// <param name="canvasX">画布横坐标。</param>
    /// <param name="canvasY">画布纵坐标。</param>
    public Point CanvasToSource(double canvasX, double canvasY)
    {
        return new Point(ShowX + canvasX / ImgScale, ShowY + canvasY / ImgScale);
    }

    /// <summary>根据滚轮增量执行可逆缩放，保留鼠标下方原图位置。</summary>
    /// <param name="canvasX">缩放锚点横坐标。</param>
    /// <param name="canvasY">缩放锚点纵坐标。</param>
    /// <param name="delta">滚轮原始增量，通常每格为 120。</param>
    public void OnMouseWheel(double canvasX, double canvasY, int delta)
    {
        if (!HasImage || IsDrawingRoi || IsPanning || delta == 0)
        {
            return;
        }

        IsAutoFit = false;
        SetViewport(ImgScale * Math.Pow(ZoomStep, delta / 120.0),
            CanvasToSource(canvasX, canvasY), new Point(canvasX, canvasY));
    }

    /// <summary>更新原始像素读数，图外清空，同一像素不重复读取。</summary>
    /// <param name="canvasX">画布横坐标。</param>
    /// <param name="canvasY">画布纵坐标。</param>
    public void OnMouseMoveNone(double canvasX, double canvasY)
    {
        if (!HasImage)
        {
            return;
        }

        Point point = CanvasToSource(canvasX, canvasY);
        int x = (int)Math.Floor(point.X);
        int y = (int)Math.Floor(point.Y);
        if (x < 0 || y < 0 || x >= _imageModel.Width || y >= _imageModel.Height)
        {
            ClearPixelInfo();
            return;
        }

        if (x == _lastPixelX && y == _lastPixelY)
        {
            return;
        }

        _lastPixelX = x;
        _lastPixelY = y;
        PixelLocationText = $"X:{x}, Y:{y}";
        double[] raw = _imageModel.GetPixelRaw(x, y);
        string values = string.Join(", ", raw.Select(FormatValue));
        PixelValueText = $"({values})";
    }

    /// <summary>清空图外或鼠标离开画布时的读数。</summary>
    public void ClearPixelInfo()
    {
        _lastPixelX = -1;
        _lastPixelY = -1;
        PixelLocationText = string.Empty;
        PixelValueText = string.Empty;
    }

    /// <summary>从有效图像像素开始 ROI 预览。</summary>
    /// <param name="canvasX">按下位置横坐标。</param>
    /// <param name="canvasY">按下位置纵坐标。</param>
    /// <returns>是否开始选择，供画布决定是否捕获鼠标。</returns>
    public bool OnLeftButtonDown(double canvasX, double canvasY)
    {
        if (!HasImage || !IsSelectRoi || IsPanning)
        {
            return false;
        }

        Point point = CanvasToSource(canvasX, canvasY);
        if (point.X < 0 || point.Y < 0 || point.X >= _imageModel.Width || point.Y >= _imageModel.Height)
        {
            return false;
        }

        _roiStartPixel = new Point(Math.Floor(point.X), Math.Floor(point.Y));
        IsDrawingRoi = true;
        UpdateRoiPreview(canvasX, canvasY);
        return true;
    }

    /// <summary>拖动只更新预览，不计算统计。</summary>
    /// <param name="canvasX">当前横坐标。</param>
    /// <param name="canvasY">当前纵坐标。</param>
    public void OnLeftButtonDrag(double canvasX, double canvasY)
    {
        if (IsDrawingRoi)
        {
            UpdateRoiPreview(canvasX, canvasY);
        }
    }

    /// <summary>提交包含起点和终点像素的选区，执行一次统计。</summary>
    /// <param name="canvasX">松开位置横坐标。</param>
    /// <param name="canvasY">松开位置纵坐标。</param>
    public void OnLeftButtonUp(double canvasX, double canvasY)
    {
        if (!IsDrawingRoi)
        {
            return;
        }

        UpdateRoiPreview(canvasX, canvasY);
        SetRoi(RoiPreviewRect);
        IsDrawingRoi = false;
        RequestRender();
    }

    /// <summary>开始右键平移，ROI 绘制中不切换操作。</summary>
    /// <param name="canvasX">按下位置横坐标。</param>
    /// <param name="canvasY">按下位置纵坐标。</param>
    /// <returns>是否开始平移。</returns>
    public bool OnRightButtonDown(double canvasX, double canvasY)
    {
        if (!HasImage || IsDrawingRoi)
        {
            return false;
        }

        _dragStart = new Point(canvasX, canvasY);
        IsPanning = true;
        return true;
    }

    /// <summary>根据鼠标位移平移视口。</summary>
    /// <param name="canvasX">当前横坐标。</param>
    /// <param name="canvasY">当前纵坐标。</param>
    public void OnRightButtonDrag(double canvasX, double canvasY)
    {
        if (!IsPanning)
        {
            return;
        }

        PanBy(_dragStart.X - canvasX, _dragStart.Y - canvasY);
        _dragStart = new Point(canvasX, canvasY);
    }

    /// <summary>结束右键平移。</summary>
    public void OnRightButtonUp()
    {
        IsPanning = false;
    }

    /// <summary>移动视口，确保仍有一部分图像可见。</summary>
    /// <param name="dx">视口向右移动的 WPF 显示单位。</param>
    /// <param name="dy">视口向下移动的 WPF 显示单位。</param>
    public void PanBy(double dx, double dy)
    {
        if (!HasImage || IsDrawingRoi)
        {
            return;
        }

        IsAutoFit = false;
        ShowX += dx / ImgScale;
        ShowY += dy / ImgScale;
        ConstrainViewport();
        RequestRender();
    }

    /// <summary>限制视口位置，使横纵方向各保留至少 50 DIP 的图片；不足时保留该方向的完整尺寸。</summary>
    private void ConstrainViewport()
    {
        double visibleX = Math.Min(MinVisibleSize, Math.Min(CanvasWidth, _imageModel.Width * ImgScale));
        double visibleY = Math.Min(MinVisibleSize, Math.Min(CanvasHeight, _imageModel.Height * ImgScale));
        ShowX = Math.Clamp(ShowX,
            (visibleX - CanvasWidth) / ImgScale, _imageModel.Width - visibleX / ImgScale);
        ShowY = Math.Clamp(ShowY,
            (visibleY - CanvasHeight) / ImgScale, _imageModel.Height - visibleY / ImgScale);
    }

    /// <summary>取消未提交的鼠标操作，保留上次确认的 ROI。</summary>
    public void CancelPointerInteraction()
    {
        IsDrawingRoi = false;
        IsPanning = false;
        RoiPreviewRect = Rect.Empty;
        RequestRender();
    }

    /// <summary>加载拖入或启动参数传入的文件。</summary>
    /// <param name="filePath">单个图片路径。</param>
    public void OnFileDrop(string filePath)
    {
        LoadImage(filePath);
    }

    /// <summary>通知画布显示内容发生变化。</summary>
    public event EventHandler? RenderRequested;

    /// <summary>请求重绘，由 WPF 合并同一帧内的视觉失效。</summary>
    private void RequestRender()
    {
        RenderRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>加载失败时保留原图、视口和选区，成功后才更新界面。</summary>
    /// <param name="path">待打开的文件路径。</param>
    private void LoadImage(string path)
    {
        try
        {
            _imageModel.Load(path);
        }
        catch (Exception exception)
        {
            StatusMessage = $"打开失败：{Path.GetFileName(path)}。{exception.Message}";
            ErrorMessage = StatusMessage;
            return;
        }

        CancelPointerInteraction();
        HasImage = true;
        TogglePseudoColorCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUsePseudoColor));
        ClearRoi();
        IsSelectRoi = false;
        IsShowCrosshair = false;
        IsPseudoColor = false;
        GammaText = "1";
        ClearPixelInfo();
        ImgInfoText = Path.GetFullPath(path);
        ImgPathToolTip = Path.GetFullPath(path);
        ImageSizeText = $"{_imageModel.Width} × {_imageModel.Height}";
        ImageTypeText = _imageModel.TypeStr;
        if (_imageModel.Width > CanvasWidth || _imageModel.Height > CanvasHeight)
        {
            AutoFit();
        }
        else
        {
            InitSize();
        }

        StatusMessage = "滚轮缩放 · 右键拖动平移 · R 选区 · C 十字线 · Ctrl+0 自动适配";
    }

    /// <summary>适配并居中，使用统一的倍率和可见范围限制。</summary>
    private void FitToCanvas()
    {
        double scale = Math.Min(CanvasWidth / _imageModel.Width, CanvasHeight / _imageModel.Height);
        SetViewport(scale,
            new Point(_imageModel.Width / 2.0, _imageModel.Height / 2.0),
            new Point(CanvasWidth / 2.0, CanvasHeight / 2.0));
    }

    /// <summary>统一限制倍率并更新视口，优先保留锚点，触及边界时保证部分图片可见。</summary>
    /// <param name="scale">目标倍率。</param>
    /// <param name="sourceAnchor">原图锚点。</param>
    /// <param name="canvasAnchor">画布锚点。</param>
    private void SetViewport(double scale, Point sourceAnchor, Point canvasAnchor)
    {
        ImgScale = Math.Clamp(scale, ScaleMin, ScaleMax);
        ShowW = CanvasWidth / ImgScale;
        ShowH = CanvasHeight / ImgScale;
        ShowX = sourceAnchor.X - canvasAnchor.X / ImgScale;
        ShowY = sourceAnchor.Y - canvasAnchor.Y / ImgScale;
        ConstrainViewport();
        ScaleText = $"{ImgScale * 100:0.##}%";
        ScaleInputText = FormatScaleInput(ImgScale);
        RequestRender();
    }

    /// <summary>更新包含两个端点像素的 ROI 预览，支持向任意方向拖动。</summary>
    /// <param name="canvasX">当前横坐标。</param>
    /// <param name="canvasY">当前纵坐标。</param>
    private void UpdateRoiPreview(double canvasX, double canvasY)
    {
        Point point = CanvasToSource(canvasX, canvasY);
        double x = Math.Clamp(Math.Floor(point.X), 0, _imageModel.Width - 1);
        double y = Math.Clamp(Math.Floor(point.Y), 0, _imageModel.Height - 1);
        RoiPreviewRect = new Rect(Math.Min(x, _roiStartPixel.X), Math.Min(y, _roiStartPixel.Y),
            Math.Abs(x - _roiStartPixel.X) + 1, Math.Abs(y - _roiStartPixel.Y) + 1);
        RequestRender();
    }

    /// <summary>保存有效整数 ROI，一次扫描获取全部统计。</summary>
    /// <param name="roi">已裁切到图内的整数边界矩形。</param>
    private void SetRoi(Rect roi)
    {
        RoiRectOnSrc = roi;
        HasRoi = true;
        RoiXText = FormatNumber(roi.X);
        RoiYText = FormatNumber(roi.Y);
        RoiWText = FormatNumber(roi.Width);
        RoiHText = FormatNumber(roi.Height);
        var stats = _imageModel.GetRoiStatistics((int)roi.X, (int)roi.Y, (int)roi.Width, (int)roi.Height);
        string[] labels = GetChannelLabels(_imageModel.Channels);
        string average = string.Join("  ", stats.Average.Select((value, channel) =>
            $"{labels[channel]}:{FormatValue(value)}"));
        RoiInfoText = $"ROI [{roi.X:0}, {roi.Y:0}, {roi.Width:0}, {roi.Height:0}]   Avg [{average}]";
        if (_imageModel.Channels == 1)
        {
            RoiInfoText += $"   Min {FormatValue(stats.Min[0])}   Max {FormatValue(stats.Max[0])}";
        }

        StatusMessage = "ROI 已更新，统计基于原始像素；关闭 ROI 工具仍保留选区。";
    }

    /// <summary>提供通道显示名称，单通道使用 Value，多通道遵循 OpenCV 通道顺序。</summary>
    /// <param name="channels">图像通道数。</param>
    private static string[] GetChannelLabels(int channels)
    {
        return channels switch
        {
            1 => ["Value"],
            2 => ["Gray", "A"],
            3 => ["B", "G", "R"],
            _ => ["B", "G", "R", "A"]
        };
    }

    /// <summary>格式化百分数输入。</summary>
    /// <param name="scale">实际倍率。</param>
    private static string FormatScaleInput(double scale)
    {
        return (scale * 100).ToString("0.####", CultureInfo.CurrentCulture);
    }

    /// <summary>格式化 Gamma 和 ROI 输入。</summary>
    /// <param name="value">待显示数值。</param>
    private static string FormatNumber(double value)
    {
        return value.ToString("0.##", CultureInfo.CurrentCulture);
    }

    /// <summary>解析当前区域或固定区域的整数。</summary>
    /// <param name="text">输入文本。</param>
    /// <param name="value">解析结果。</param>
    private static bool TryParseInt(string text, out int value)
    {
        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value) ||
            int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>解析有限浮点数。</summary>
    /// <param name="text">输入文本。</param>
    /// <param name="value">解析结果。</param>
    private static bool TryParseNumber(string text, out double value)
    {
        bool parsed = double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
            double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return parsed && double.IsFinite(value);
    }

    /// <summary>解析百分数或以 x 结尾的倍率。</summary>
    /// <param name="text">例如 100、150%、2x。</param>
    /// <param name="scale">实际倍率。</param>
    private static bool TryParseScale(string text, out double scale)
    {
        text = text.Trim();
        bool multiplier = text.EndsWith('x') || text.EndsWith('X');
        if (multiplier || text.EndsWith('%'))
        {
            text = text[..^1];
        }

        bool parsed = TryParseNumber(text, out double value);
        scale = multiplier ? value : value / 100.0;
        return parsed && scale > 0;
    }

    /// <summary>统一格式化原始像素、统计值及特殊浮点值。</summary>
    /// <param name="value">原始数值。</param>
    internal static string FormatValue(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "+Inf" : "-Inf";
        }

        return value == Math.Floor(value) ? $"{value:0}" : $"{value:G4}";
    }
}
