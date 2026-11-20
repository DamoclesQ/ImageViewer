# ImageViewer

ImageViewer 是一个基于 WPF 和 OpenCV 的桌面图片浏览工具，面向需要查看图像像素、缩放细节和 ROI 统计信息的场景。

## 功能特性

- 仅使用 OpenCV 解码图片，常见格式包括 `bmp`、`jpg`、`jpeg`、`png`、`tif`、`tiff`、`gif`，实际支持取决于当前 OpenCV 构建
- 支持拖拽打开图片
- 支持自动适配窗口和 1:1 原始尺寸显示
- 支持指定缩放比例，例如 `100`、`150%`、`2x`
- 支持鼠标滚轮缩放、右键拖动平移
- 支持像素坐标与原始像素值查看
- 支持 ROI 区域选择和均值统计
- 支持中心十字线叠加显示
- 支持叠加文字背景切换
- 支持红、绿、蓝叠加层颜色选择
- 支持 Gamma 和单通道 Jet 伪彩色，原始像素与 ROI 统计不受显示调整影响
- 支持手动输入 ROI，单通道同时显示均值、最小值和最大值
- 支持连续缩放期间动态采样与高倍率像素文字缓存
- About 对话框版本号自动绑定应用程序集版本

## 技术栈

- .NET 10
- WPF
- CommunityToolkit.Mvvm
- OpenCvSharp4
- OpenCvSharp4.WpfExtensions
- Microsoft.Extensions.DependencyInjection

## 项目结构

```text
ImageViewer/
├─ App.xaml
├─ App.xaml.cs
├─ Controls/
│  └─ ImageCanvasControl.cs
├─ Converters/
│  └─ ValueConverters.cs
├─ Models/
│  └─ ImageModel.cs
├─ Resources/
│  ├─ ImageViewerIcon.ico
│  └─ ImageViewerIcon.xaml
├─ ViewModels/
│  └─ MainViewModel.cs
└─ Views/
   ├─ MainWindow.xaml
   └─ MainWindow.xaml.cs
```

## 构建要求

- Windows
- Visual Studio 2026 或兼容版本
- .NET 10 SDK

## 构建方式

在 Visual Studio 中打开解决方案或项目后执行生成。

也可以在命令行中执行：

```powershell
dotnet build ImageViewer\ImageViewer.csproj
```

## 使用说明

### 打开图片

- 点击工具栏“打开”或按 `Ctrl+O` 选择图片
- 或将图片文件拖拽到窗口中
- 或通过命令行参数传入图片路径启动程序

### 缩放和平移

- `Auto`：自动适配窗口，后续窗口变化继续适配
- `1:1`：一个原图像素对应一个 WPF 显示单位；高 DPI 下不等于物理像素比例
- 缩放输入框：输入指定放大率后按 `Enter` 或点击 `%`
  - `100`：100%
  - `150%`：150%
  - `2x`：200%
- 鼠标滚轮：以鼠标位置为中心缩放
- 右键拖动：平移图像
- `Ctrl + ↑/↓/←/→`：按方向平移显示区域

### 像素信息

鼠标移动到图像上时，底部状态栏显示：

- 图像尺寸
- 当前像素坐标
- 图像 OpenCV 类型，例如 `CV_8UC3`、`CV_32FC1`
- 当前像素的原始通道值

像素值不带通道名称前缀，使用圆括号包裹，通道顺序遵循 OpenCV：

- 单通道：例如 `(128)`
- 三通道：按 B、G、R 顺序，例如 `(12, 34, 56)`
- 四通道：按 B、G、R、A 顺序，例如 `(12, 34, 56, 255)`

### ROI 统计

1. 点击 `ROI` 按钮或按 `R`
2. 在图像上按住左键拖动选择区域
3. 松开鼠标后显示 ROI 信息和均值统计

框选包含起点和终点像素，单击可选择 1×1 像素。关闭 ROI 工具保留已有选区；工具栏第二行可直接输入 ROI、清除选区或调整 Gamma。

### 十字线和叠加层

- 点击“十字线”或按 `C`：切换中心十字线
- 点击“文字背景”：切换像素文字背景
- 工具栏第二行的红、绿、蓝色块按钮：选择叠加层颜色
- 按 `ESC`：拖动中取消本次操作；无拖动时清除 ROI 和十字线

## 图片格式说明

所有图片统一通过 OpenCV 的 `Cv2.ImRead` 读取，不再提供 ICO 专用解码支持。打开对话框列出常见图片扩展名；其他 OpenCV 支持的格式可通过“所有文件”、拖放或启动参数打开。无法解码的文件会显示打开失败提示，并保留当前图片和选区。

## 版本说明

应用版本来自项目文件中的程序集版本配置：

```xml
<AssemblyVersion>1.0.0.0</AssemblyVersion>
<FileVersion>1.0.0.0</FileVersion>
```

About 对话框会读取应用程序集版本并自动显示。

应用图标采用图片框与像素放大镜样式。`ImageViewerIcon.xaml` 为可编辑矢量源，`ImageViewerIcon.ico` 包含 16、24、32、48、64、128、256 像素图像。修改矢量源后，可在 Windows PowerShell 中执行 `./tools/Generate-AppIcon.ps1` 重新生成 ICO。

## 操作与开发文档

- [用户说明](docs/用户说明.md)：操作规则、快捷键和输入行为。
- [开发说明](docs/开发说明.md)：数据流、坐标约定、状态转换、缓存和资源释放。

## 本地回归验证

`tests/` 仅用于本地验证，已加入 Git 忽略规则，不包含在解决方案中。若本机保留了探针，可执行：

```powershell
dotnet run --project tests/ImageViewer.Probe/ImageViewer.Probe.csproj
```

探针生成测试图并执行视口、ROI、原始数据、错误恢复和 WPF 离屏绘制检查。截图位于探针输出目录的 `probe-output` 下，离屏验证不等同于实际桌面帧率测试。
