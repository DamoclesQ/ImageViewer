using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace ImageViewer.Models;

/// <summary>
/// 图片数据模型：以 OpenCvSharp <see cref="Mat"/> 为核心存储，
/// 提供 WPF 渲染所需<see cref="BitmapSource"/> 及像素查询、ROI 统计接口
/// 显示层参考ImageWatch 设计：像素叠加与状态栏始终显示原始值，归一化仅用于图像渲染
/// </summary>
public sealed class ImageModel : IDisposable
{
    private Mat _mat = new();
    private Mat _displayBase = new();
    private double _displayGamma = 1.0;

    public BitmapSource? Source { get; private set; }
    public int Width => _mat.Width;
    public int Height => _mat.Height;
    public int Channels => _mat.Channels();
    public string? FilePath { get; private set; }
    public string? FileName => FilePath is null ? null : Path.GetFileName(FilePath);
    public bool IsLoaded => Source is not null && !_mat.Empty();
    public bool IsPseudoColorEnabled { get; private set; }
    public double DisplayGamma => _displayGamma;

    /// <summary>
    /// OpenCV 类型字符串，例如 "CV_8UC3","CV_32FC1"
    /// 与 ImageWatch 状态栏显示保持一致
    /// </summary>
    public string TypeStr => _mat.Empty() ? string.Empty : MatTypeToString(_mat.Type());

    /// <summary>使用 OpenCV 读取原始图像并构建显示基础，全部成功后才替换旧图像。</summary>
    /// <param name="filePath">图片文件路径。</param>
    public void Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        Mat? next = Cv2.ImRead(filePath, ImreadModes.Unchanged);
        Mat? display = null;
        try
        {
            if (next.Empty())
            {
                throw new InvalidOperationException("文件不存在、格式不支持或内容已损坏。");
            }

            display = BuildDisplayMat(next);
            BitmapSource source = CreateSource(display, 1.0, false);
            _mat.Dispose();
            _displayBase.Dispose();
            _mat = next;
            _displayBase = display;
            next = null;
            display = null;
            Source = source;
            FilePath = filePath;
            IsPseudoColorEnabled = false;
            _displayGamma = 1.0;
        }
        finally
        {
            next?.Dispose();
            display?.Dispose();
        }
    }

    /// <summary>生成单通道伪彩色显示，成功后才更新当前设置。</summary>
    /// <param name="enabled">是否启用 Jet 伪彩色。</param>
    public void SetPseudoColorEnabled(bool enabled)
    {
        bool useColor = enabled && Channels == 1;
        if (_mat.Empty() || useColor == IsPseudoColorEnabled)
        {
            return;
        }

        Source = CreateSource(_displayBase, _displayGamma, useColor);
        IsPseudoColorEnabled = useColor;
    }

    /// <summary>调整显示 Gamma，复用已归一化的图像，不重复处理原始数据。</summary>
    /// <param name="gamma">正的有限 Gamma 值。</param>
    public void SetGamma(double gamma)
    {
        if (gamma <= 0 || double.IsNaN(gamma) || double.IsInfinity(gamma))
            throw new ArgumentOutOfRangeException(nameof(gamma), "Gamma 必须为正数。");

        if (_mat.Empty() || gamma == _displayGamma)
        {
            return;
        }

        Source = CreateSource(_displayBase, gamma, IsPseudoColorEnabled);
        _displayGamma = gamma;
    }

    /// <summary>
    /// 生成专用于显示的 CV_8U Mat（不修改原始数据）
    /// 参考ImageWatch 策略
    /// - CV_8U：创建共享只读存储的 Mat 头，避免复制整图
    /// - 其他类型：先过滤 NaN/Inf（浮点），再使用 <see cref="Cv2.Normalize"/>
    ///   (NORM_MINMAX) 将实际像素范围映射到 [0, 255]，输出 CV_8U
    /// </summary>
    /// <param name="src">保持不变的原始图像。</param>
    private static Mat BuildDisplayMat(Mat src)
    {
        if (src.Depth() == MatType.CV_8U)
        {
            return new Mat(src, new Rect(0, 0, src.Width, src.Height));
        }

        using var toNorm = src.Clone();
        if (src.Depth() is MatType.CV_32F or MatType.CV_64F)
        {
            ReplaceNonFiniteValues(toNorm);
        }

        var dst = new Mat();
        Cv2.Normalize(toNorm, dst, 0, 255, NormTypes.MinMax, MatType.CV_8U);
        return dst;
    }

    /// <summary>仅在显示副本内将浮点 NaN 和正负无穷替换为零。</summary>
    /// <param name="image">32 位或 64 位浮点显示副本。</param>
    private static unsafe void ReplaceNonFiniteValues(Mat image)
    {
        int valuesPerRow = image.Width * image.Channels();
        for (int y = 0; y < image.Height; y++)
        {
            byte* row = image.DataPointer + y * image.Step();
            for (int i = 0; i < valuesPerRow; i++)
            {
                if (image.Depth() == MatType.CV_32F)
                {
                    float* value = (float*)row + i;
                    if (!float.IsFinite(*value))
                    {
                        *value = 0;
                    }
                }
                else
                {
                    double* value = (double*)row + i;
                    if (!double.IsFinite(*value))
                    {
                        *value = 0;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 获取指定坐标显示颜色**（ARGB）
    /// 直接从已归一化的 <see cref="Source"/>（WriteableBitmap）读取，与屏幕显示完全一致
    /// 用于叠加层文字颜色的亮度判断。
    /// </summary>
    /// <param name="x">原图像素横坐标。</param>
    /// <param name="y">原图像素纵坐标。</param>
    public Color GetPixel(int x, int y)
    {
        if (Source is null || (uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return Colors.Transparent;

        // stride = 每行字节数；1×1 裁剪时只需 bytesPerPixel
        int bytesPerPixel = (Source.Format.BitsPerPixel + 7) / 8;
        byte[] px = new byte[4];
        Source.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), px, bytesPerPixel, 0);

        return Source.Format.ToString() switch
        {
            "Gray8" => Color.FromArgb(255, px[0], px[0], px[0]),
            "Bgr24" => Color.FromArgb(255, px[2], px[1], px[0]),
            "Bgra32" => Color.FromArgb(px[3], px[2], px[1], px[0]),
            _ => Color.FromArgb(255, px[0], px[0], px[0]),
        };
    }

    /// <summary>
    /// 获取指定坐标**原始像素值**（不归一化），以 double 数组返回，通道顺序 B, G, R, (A)
    /// 参考ImageWatch：像素叠加层和状态栏始终显示此原始值
    /// </summary>
    public double[] GetPixelRaw(int x, int y)
    {
        if (_mat.Empty() || (uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return [];

        int ch = _mat.Channels();
        int depth = _mat.Depth();
        int step = (int)_mat.Step();
        int elemSize = depth switch
        {
            MatType.CV_8U or MatType.CV_8S => 1,
            MatType.CV_16U or MatType.CV_16S => 2,
            MatType.CV_32S or MatType.CV_32F => 4,
            MatType.CV_64F => 8,
            _ => 1,
        };

        double[] vals = new double[ch];
        unsafe
        {
            byte* row = (byte*)_mat.DataPointer + (long)y * step + (long)x * ch * elemSize;
            for (int c = 0; c < ch; c++)
            {
                byte* p = row + (long)c * elemSize;
                vals[c] = depth switch
                {
                    MatType.CV_8U => *p,
                    MatType.CV_8S => *(sbyte*)p,
                    MatType.CV_16U => *(ushort*)p,
                    MatType.CV_16S => *(short*)p,
                    MatType.CV_32S => *(int*)p,
                    MatType.CV_32F => *(float*)p,
                    MatType.CV_64F => *(double*)p,
                    _ => *p,
                };
            }
        }
        return vals;
    }

    /// <summary>计算矩形 ROI 区域各通道的最小最大原始值，返回 (min[], max[]) B,G,R,(A) 顺序，浮点类型忽略 NaN/Inf</summary>
    public (double[] Min, double[] Max) GetRoiMinMax(int x, int y, int w, int h)
    {
        var stats = GetRoiStatistics(x, y, w, h);
        return (stats.Min, stats.Max);
    }

    /// <summary>计算矩形 ROI 区域各通道的均值，返回 (B, G, R, A)，浮点类型忽略 NaN/Inf</summary>
    public (double B, double G, double R, double A) GetAverageColor(int x, int y, int w, int h)
    {
        var stats = GetRoiStatistics(x, y, w, h);
        if (stats.Average.Length == 0) return (-1, -1, -1, -1);

        // OpenCv 通道顺序：BGR(A) 0=B, 1=G, 2=R, 3=A
        return Channels switch
        {
            1 => (stats.Average[0], stats.Average[0], stats.Average[0], 255),
            2 => (stats.Average[0], stats.Average[0], stats.Average[0], stats.Average[1]),
            3 => (stats.Average[0], stats.Average[1], stats.Average[2], 255),
            _ => (stats.Average[0], stats.Average[1], stats.Average[2], stats.Average[3]),
        };
    }

    public void Unload()
    {
        Source = null;
        FilePath = null;
        IsPseudoColorEnabled = false;
        _displayGamma = 1.0;
        _mat.Dispose();
        _mat = new Mat();
        _displayBase.Dispose();
        _displayBase = new Mat();
    }

    /// <summary>释放原始图像和显示基础缓存。</summary>
    public void Dispose()
    {
        _mat.Dispose();
        _displayBase.Dispose();
        Source = null;
    }

    // ─────────────────────────────────────────
    // 内部工具
    // ─────────────────────────────────────────

    /// <summary>从缓存的 8 位图像生成显示结果，归一化不随 Gamma 重复执行。</summary>
    /// <param name="display">归一化显示基础。</param>
    /// <param name="gammaValue">Gamma 指数。</param>
    /// <param name="pseudoColor">是否使用单通道伪彩色。</param>
    private static BitmapSource CreateSource(Mat display, double gammaValue, bool pseudoColor)
    {
        if (gammaValue == 1.0 && !pseudoColor)
        {
            return MatToFrozenBitmapSource(display);
        }

        using var gamma = ApplyGamma(display, gammaValue);
        if (pseudoColor && gamma.Channels() == 1)
        {
            using var color = new Mat();
            Cv2.ApplyColorMap(gamma, color, ColormapTypes.Jet);
            return MatToFrozenBitmapSource(color);
        }

        return MatToFrozenBitmapSource(gamma);
    }

    /// <summary>以查找表调整显示亮度，四通道图像保留原有透明度。</summary>
    /// <param name="src">8 位显示图像。</param>
    /// <param name="gamma">Gamma 指数。</param>
    private static Mat ApplyGamma(Mat src, double gamma)
    {
        var dst = new Mat();
        if (Math.Abs(gamma - 1.0) < 0.000001)
        {
            src.CopyTo(dst);
            return dst;
        }

        byte[] lut = new byte[256];
        for (int i = 0; i < lut.Length; i++)
        {
            lut[i] = (byte)Math.Clamp(Math.Round(Math.Pow(i / 255.0, gamma) * 255.0), 0, 255);
        }

        using var table = Mat.FromPixelData(1, 256, MatType.CV_8UC1, lut);
        Cv2.LUT(src, table, dst);
        if (src.Channels() == 4)
        {
            using var alpha = new Mat();
            Cv2.ExtractChannel(src, alpha, 3);
            Cv2.InsertChannel(alpha, dst, 3);
        }
        return dst;
    }

    /// <summary>一次扫描计算 ROI 各通道统计，忽略非有限值。</summary>
    /// <param name="x">左上角横坐标。</param>
    /// <param name="y">左上角纵坐标。</param>
    /// <param name="w">区域宽度。</param>
    /// <param name="h">区域高度。</param>
    public (double[] Min, double[] Max, double[] Average) GetRoiStatistics(int x, int y, int w, int h)
    {
        if (_mat.Empty()) return ([], [], []);

        int x2 = Math.Min(Width, x + w);
        int y2 = Math.Min(Height, y + h);
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        if (x2 <= x || y2 <= y) return ([], [], []);

        int ch = Channels;
        int depth = _mat.Depth();
        int step = (int)_mat.Step();
        int elemSize = GetElementSize(depth);
        var mins = new double[ch];
        var maxs = new double[ch];
        var sums = new double[ch];
        var counts = new long[ch];
        Array.Fill(mins, double.PositiveInfinity);
        Array.Fill(maxs, double.NegativeInfinity);

        unsafe
        {
            byte* basePtr = (byte*)_mat.DataPointer;
            for (int yy = y; yy < y2; yy++)
            {
                byte* row = basePtr + (long)yy * step + (long)x * ch * elemSize;
                for (int xx = x; xx < x2; xx++)
                {
                    for (int c = 0; c < ch; c++)
                    {
                        double value = ReadValue(row + (long)c * elemSize, depth);
                        if (double.IsNaN(value) || double.IsInfinity(value)) continue;

                        mins[c] = Math.Min(mins[c], value);
                        maxs[c] = Math.Max(maxs[c], value);
                        sums[c] += value;
                        counts[c]++;
                    }
                    row += ch * elemSize;
                }
            }
        }

        var averages = new double[ch];
        for (int c = 0; c < ch; c++)
        {
            if (counts[c] == 0)
            {
                mins[c] = double.NaN;
                maxs[c] = double.NaN;
                averages[c] = double.NaN;
            }
            else
            {
                averages[c] = sums[c] / counts[c];
            }
        }

        return (mins, maxs, averages);
    }

    private static int GetElementSize(int depth) => depth switch
    {
        MatType.CV_8U or MatType.CV_8S => 1,
        MatType.CV_16U or MatType.CV_16S => 2,
        MatType.CV_32S or MatType.CV_32F => 4,
        MatType.CV_64F => 8,
        _ => 1,
    };

    private static unsafe double ReadValue(byte* p, int depth) => depth switch
    {
        MatType.CV_8U => *p,
        MatType.CV_8S => *(sbyte*)p,
        MatType.CV_16U => *(ushort*)p,
        MatType.CV_16S => *(short*)p,
        MatType.CV_32S => *(int*)p,
        MatType.CV_32F => *(float*)p,
        MatType.CV_64F => *(double*)p,
        _ => *p,
    };

    private static BitmapSource MatToFrozenBitmapSource(Mat src)
    {
        var wb = WriteableBitmapConverter.ToWriteableBitmap(src);
        wb.Freeze();
        return wb;
    }

    /// <summary>
    /// 将 OpenCV MatType 转换为 ImageWatch 风格的类型字符串，例："CV_32FC1"
    /// </summary>
    private static string MatTypeToString(MatType type)
    {
        string depth = (type.Value & 7) switch
        {
            0 => "8U",
            1 => "8S",
            2 => "16U",
            3 => "16S",
            4 => "32S",
            5 => "32F",
            6 => "64F",
            _ => "??",
        };
        int ch = ((type.Value >> 3) & 0x3F) + 1;
        return $"{depth}C{ch}";
    }
}
