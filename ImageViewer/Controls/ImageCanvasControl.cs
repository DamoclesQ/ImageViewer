using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls;

/// <summary>
/// WPF 图像画布。图片、网格和 ROI 共用原图边界坐标，不在缩放时裁剪或复制整图。
/// 连续交互期间降低平滑采样开销，静止后恢复质量；高倍率文字按像素缓存。
/// </summary>
public class ImageCanvasControl : FrameworkElement
{
    private const double GridScale = 30.0;
    private const double PixelTextScale = 50.0;
    private const int PixelTextCacheLimit = 8192;
    private static readonly Typeface PixelTypeface = new("Consolas");
    private static readonly Pen GridPen = new(Brushes.LightGray, 1.0);
    private static readonly Brush TextBackground = new SolidColorBrush(Color.FromArgb(220, 200, 200, 200));

    private readonly Dictionary<(int X, int Y), FormattedText> _pixelTextCache = new();
    private readonly DispatcherTimer _qualityTimer;
    private MainViewModel? _vm;
    private BitmapSource? _cachedSource;
    private double _cachedDpi;
    private bool _cachedTextBackground;
    private bool _isInteracting;
    private Pen? _overlayPen;

    /// <summary>初始化可复用的冻结绘图资源。</summary>
    static ImageCanvasControl()
    {
        GridPen.Freeze();
        TextBackground.Freeze();
    }

    /// <summary>创建画布和静止后恢复采样质量的计时器。</summary>
    public ImageCanvasControl()
    {
        ClipToBounds = true;
        _qualityTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _qualityTimer.Tick += OnInteractionSettled;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>连接视图模型，替换连接时解除旧事件。</summary>
    /// <param name="vm">当前窗口的视图模型。</param>
    public void Attach(MainViewModel vm)
    {
        if (_vm != null)
        {
            _vm.RenderRequested -= OnRenderRequested;
        }

        _vm = vm;
        _vm.RenderRequested += OnRenderRequested;
        _pixelTextCache.Clear();
        InvalidateVisual();
    }

    /// <summary>重新进入视觉树时恢复订阅。</summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">加载事件。</param>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.RenderRequested -= OnRenderRequested;
            _vm.RenderRequested += OnRenderRequested;
            _vm.OnCanvasResized(ActualWidth, ActualHeight);
        }
    }

    /// <summary>离开视觉树时解除订阅、停止计时并释放文字缓存。</summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">卸载事件。</param>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.RenderRequested -= OnRenderRequested;
            _vm.CancelPointerInteraction();
        }

        _qualityTimer.Stop();
        _isInteracting = false;
        _pixelTextCache.Clear();
        _cachedSource = null;
    }

    /// <summary>同步光标、捕获状态和鼠标读数，然后请求 WPF 重绘。</summary>
    /// <param name="sender">当前视图模型。</param>
    /// <param name="e">重绘事件。</param>
    private void OnRenderRequested(object? sender, EventArgs e)
    {
        if (_vm == null)
        {
            return;
        }

        Cursor = _vm.IsPanning ? Cursors.Hand : _vm.IsSelectRoi ? Cursors.Cross : Cursors.Arrow;
        if (IsMouseCaptured && !_vm.IsDrawingRoi && !_vm.IsPanning)
        {
            ReleaseMouseCapture();
        }

        if (IsMouseOver)
        {
            Point point = Mouse.GetPosition(this);
            _vm.OnMouseMoveNone(point.X, point.Y);
        }

        InvalidateVisual();
    }

    /// <summary>将布局变化同步到视口。</summary>
    /// <param name="sizeInfo">新的布局尺寸。</param>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _vm?.OnCanvasResized(RenderSize.Width, RenderSize.Height);
    }

    /// <summary>绘制图片和叠加层，坐标均以像素左上角边界为基准。</summary>
    /// <param name="dc">WPF 绘图上下文。</param>
    protected override void OnRender(DrawingContext dc)
    {
        double width = RenderSize.Width;
        double height = RenderSize.Height;
        dc.DrawRectangle(Brushes.DarkSlateGray, null, new Rect(RenderSize));
        if (_vm == null || !_vm.HasImage)
        {
            DrawEmptyMessage(dc, width, height);
            return;
        }

        double scale = _vm.ImgScale;
        BitmapScalingMode mode = GetScalingMode(scale);
        if (RenderOptions.GetBitmapScalingMode(this) != mode)
        {
            RenderOptions.SetBitmapScalingMode(this, mode);
        }

        var imageRect = new Rect(-_vm.ShowX * scale, -_vm.ShowY * scale,
            _vm.ImageData.Width * scale, _vm.ImageData.Height * scale);
        dc.DrawImage(_vm.ImageData.Source, imageRect);

        if (scale >= GridScale)
        {
            DrawGrid(dc, scale, width, height);
        }

        if (scale >= PixelTextScale)
        {
            DrawPixelInfo(dc, scale, width, height);
        }
        else if (!ReferenceEquals(_cachedSource, _vm.ImageData.Source))
        {
            // 切换大图后及时解除旧 BitmapSource 的缓存引用。
            _pixelTextCache.Clear();
            _cachedSource = null;
        }

        UpdateOverlayPen(scale);
        if (_vm.IsShowCrosshair)
        {
            double x = (_vm.ImageData.Width / 2.0 - _vm.ShowX) * scale;
            double y = (_vm.ImageData.Height / 2.0 - _vm.ShowY) * scale;
            dc.DrawLine(_overlayPen, new Point(imageRect.Left, y), new Point(imageRect.Right, y));
            dc.DrawLine(_overlayPen, new Point(x, imageRect.Top), new Point(x, imageRect.Bottom));
        }

        Rect roi = _vm.IsDrawingRoi ? _vm.RoiPreviewRect : _vm.RoiRectOnSrc;
        if ((_vm.HasRoi || _vm.IsDrawingRoi) && !roi.IsEmpty)
        {
            dc.DrawRectangle(null, _overlayPen, new Rect((roi.X - _vm.ShowX) * scale,
                (roi.Y - _vm.ShowY) * scale, roi.Width * scale, roi.Height * scale));
        }
    }

    /// <summary>像素检查使用最近邻，平滑显示在连续交互时改用低质量采样。</summary>
    /// <param name="scale">当前缩放倍率。</param>
    private BitmapScalingMode GetScalingMode(double scale)
    {
        if (scale >= GridScale || (scale >= 1.0 && !_vm!.IsSmoothScaling))
        {
            return BitmapScalingMode.NearestNeighbor;
        }

        return _isInteracting ? BitmapScalingMode.LowQuality : BitmapScalingMode.HighQuality;
    }

    /// <summary>按倍率调整十字线与 ROI 共用的线宽，仅在颜色或线宽变化时重建画笔。</summary>
    /// <param name="scale">当前缩放倍率，叠加线宽为 1 到 3 个 WPF 显示单位。</param>
    private void UpdateOverlayPen(double scale)
    {
        Color color = _vm!.BrushColor;
        double thickness = scale switch
        {
            < 1.0 => 1.0,
            < 10.0 => 1.5,
            < GridScale => 2.0,
            _ => 3.0
        };

        if (_overlayPen?.Brush is not SolidColorBrush brush || brush.Color != color ||
            _overlayPen.Thickness != thickness)
        {
            _overlayPen = new Pen(new SolidColorBrush(color), thickness);
            _overlayPen.Freeze();
        }
    }

    /// <summary>在没有图片时给出操作提示。</summary>
    /// <param name="dc">绘图上下文。</param>
    /// <param name="width">画布宽度。</param>
    /// <param name="height">画布高度。</param>
    private void DrawEmptyMessage(DrawingContext dc, double width, double height)
    {
        var text = new FormattedText("拖入图片，或按 Ctrl+O 打开\n滚轮缩放 · 右键拖动平移",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"),
            16, Brushes.LightGray, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = TextAlignment.Center
        };
        dc.DrawText(text, new Point(width / 2.0, Math.Max(0, (height - text.Height) / 2.0)));
    }

    /// <summary>仅绘制图像覆盖区域的整像素边界。</summary>
    /// <param name="dc">绘图上下文。</param>
    /// <param name="scale">当前倍率。</param>
    /// <param name="width">画布宽度。</param>
    /// <param name="height">画布高度。</param>
    private void DrawGrid(DrawingContext dc, double scale, double width, double height)
    {
        double left = Math.Max(0, -_vm!.ShowX * scale);
        double top = Math.Max(0, -_vm.ShowY * scale);
        double right = Math.Min(width, (_vm.ImageData.Width - _vm.ShowX) * scale);
        double bottom = Math.Min(height, (_vm.ImageData.Height - _vm.ShowY) * scale);
        if (right <= left || bottom <= top)
        {
            return;
        }

        int startX = Math.Max(0, (int)Math.Ceiling(_vm.ShowX));
        int endX = Math.Min(_vm.ImageData.Width, (int)Math.Floor(_vm.ShowX + width / scale));
        int startY = Math.Max(0, (int)Math.Ceiling(_vm.ShowY));
        int endY = Math.Min(_vm.ImageData.Height, (int)Math.Floor(_vm.ShowY + height / scale));
        for (int x = startX; x <= endX; x++)
        {
            double screenX = (x - _vm.ShowX) * scale;
            dc.DrawLine(GridPen, new Point(screenX, top), new Point(screenX, bottom));
        }

        for (int y = startY; y <= endY; y++)
        {
            double screenY = (y - _vm.ShowY) * scale;
            dc.DrawLine(GridPen, new Point(left, screenY), new Point(right, screenY));
        }
    }

    /// <summary>缓存可见像素的文字和前景色，平移时复用已有内容。</summary>
    /// <param name="dc">绘图上下文。</param>
    /// <param name="scale">当前倍率。</param>
    /// <param name="width">画布宽度。</param>
    /// <param name="height">画布高度。</param>
    private void DrawPixelInfo(DrawingContext dc, double scale, double width, double height)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (!ReferenceEquals(_cachedSource, _vm!.ImageData.Source) || dpi != _cachedDpi ||
            _cachedTextBackground != _vm.IsUseTextBack || _pixelTextCache.Count > PixelTextCacheLimit)
        {
            _pixelTextCache.Clear();
            _cachedSource = _vm.ImageData.Source;
            _cachedDpi = dpi;
            _cachedTextBackground = _vm.IsUseTextBack;
        }

        int startX = Math.Max(0, (int)Math.Floor(_vm.ShowX));
        int endX = Math.Min(_vm.ImageData.Width, (int)Math.Ceiling(_vm.ShowX + width / scale));
        int startY = Math.Max(0, (int)Math.Floor(_vm.ShowY));
        int endY = Math.Min(_vm.ImageData.Height, (int)Math.Ceiling(_vm.ShowY + height / scale));
        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                if (!_pixelTextCache.TryGetValue((x, y), out FormattedText? text))
                {
                    text = CreatePixelText(x, y, scale, dpi);
                    _pixelTextCache[(x, y)] = text;
                }

                double left = (x - _vm.ShowX) * scale;
                double top = (y - _vm.ShowY) * scale;
                if (_vm.IsUseTextBack)
                {
                    dc.DrawRectangle(TextBackground, null, new Rect(left + 2, top + 2, scale - 4, scale - 4));
                }

                dc.DrawText(text, new Point(left + 4, top + 4));
            }
        }
    }

    /// <summary>从原始通道值创建单个像素的文字，前景色取决于显示亮度。</summary>
    /// <param name="x">原图像素横坐标。</param>
    /// <param name="y">原图像素纵坐标。</param>
    /// <param name="scale">像素格显示大小。</param>
    /// <param name="dpi">文字 DPI 比例。</param>
    private FormattedText CreatePixelText(int x, int y, double scale, double dpi)
    {
        double[] raw = _vm!.ImageData.GetPixelRaw(x, y);
        string[] lines = raw.Select(MainViewModel.FormatValue).ToArray();
        string value = string.Join("\n", lines);
        Color color = _vm.ImageData.GetPixel(x, y);
        double luminance = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
        Brush foreground = _vm.IsUseTextBack || luminance >= 100 ? Brushes.Black : Brushes.White;
        double fontSize = Math.Min(9.0, Math.Min((scale - 8) / (lines.Length * 1.4),
            (scale - 8) / (lines.Max(line => line.Length) * 0.62)));
        return new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            PixelTypeface, Math.Max(4, fontSize), foreground, dpi)
        {
            MaxTextWidth = scale - 8,
            MaxTextHeight = scale - 8,
            Trimming = TextTrimming.CharacterEllipsis
        };
    }

    /// <summary>标记连续缩放或平移，并延后恢复高质量采样。</summary>
    private void BeginInteraction()
    {
        _isInteracting = true;
        _qualityTimer.Stop();
        _qualityTimer.Start();
    }

    /// <summary>鼠标停止操作后恢复高质量采样。</summary>
    /// <param name="sender">恢复计时器。</param>
    /// <param name="e">计时事件。</param>
    private void OnInteractionSettled(object? sender, EventArgs e)
    {
        _qualityTimer.Stop();
        _isInteracting = false;
        InvalidateVisual();
    }

    /// <summary>转发完整滚轮增量，保留高分辨率滚轮输入。</summary>
    /// <param name="e">滚轮事件。</param>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_vm == null || !_vm.HasImage || _vm.IsDrawingRoi || _vm.IsPanning)
        {
            return;
        }

        BeginInteraction();
        Point point = e.GetPosition(this);
        _vm.OnMouseWheel(point.X, point.Y, e.Delta);
        e.Handled = true;
    }

    /// <summary>转发拖动并实时更新像素读数。</summary>
    /// <param name="e">鼠标移动事件。</param>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        Point point = e.GetPosition(this);
        if (_vm?.IsDrawingRoi == true && e.LeftButton == MouseButtonState.Pressed)
        {
            _vm.OnLeftButtonDrag(point.X, point.Y);
        }
        else if (_vm?.IsPanning == true && e.RightButton == MouseButtonState.Pressed)
        {
            BeginInteraction();
            _vm.OnRightButtonDrag(point.X, point.Y);
        }

        _vm?.OnMouseMoveNone(point.X, point.Y);
    }

    /// <summary>鼠标离开画布时清空读数。</summary>
    /// <param name="e">离开事件。</param>
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _vm?.ClearPixelInfo();
    }

    /// <summary>仅在真正开始选区时捕获鼠标。</summary>
    /// <param name="e">左键按下事件。</param>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        Point point = e.GetPosition(this);
        if (_vm?.OnLeftButtonDown(point.X, point.Y) == true)
        {
            if (!CaptureMouse())
            {
                _vm.CancelPointerInteraction();
            }

            e.Handled = true;
        }
    }

    /// <summary>先提交选区，再释放捕获，避免丢失捕获事件取消已完成操作。</summary>
    /// <param name="e">左键松开事件。</param>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_vm?.IsDrawingRoi != true)
        {
            return;
        }

        Point point = e.GetPosition(this);
        _vm.OnLeftButtonUp(point.X, point.Y);
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>开始右键平移并捕获鼠标。</summary>
    /// <param name="e">右键按下事件。</param>
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        Point point = e.GetPosition(this);
        if (_vm?.OnRightButtonDown(point.X, point.Y) != true)
        {
            return;
        }

        if (!CaptureMouse())
        {
            _vm.CancelPointerInteraction();
        }

        Cursor = Cursors.Hand;
        e.Handled = true;
    }

    /// <summary>结束平移并立即恢复显示质量。</summary>
    /// <param name="e">右键松开事件。</param>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (_vm?.IsPanning != true)
        {
            return;
        }

        _vm.OnRightButtonUp();
        ReleaseMouseCapture();
        OnInteractionSettled(this, EventArgs.Empty);
        Cursor = _vm.IsSelectRoi ? Cursors.Cross : Cursors.Arrow;
        e.Handled = true;
    }

    /// <summary>捕获意外丢失时清除拖动状态，避免选区或平移卡住。</summary>
    /// <param name="e">捕获丢失事件。</param>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_vm?.IsDrawingRoi == true || _vm?.IsPanning == true)
        {
            _vm.CancelPointerInteraction();
        }

        OnInteractionSettled(this, EventArgs.Empty);
    }
}
