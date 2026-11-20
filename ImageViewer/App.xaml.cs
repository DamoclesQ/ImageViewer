using System.Windows;
using ImageViewer.Models;
using ImageViewer.ViewModels;
using ImageViewer.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ImageViewer;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>创建应用依赖，并在窗口完成布局后加载启动文件。</summary>
    /// <param name="e">包含可选图片路径的启动参数。</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var collection = new ServiceCollection();
        collection.AddSingleton<ImageModel>();
        collection.AddSingleton<MainViewModel>();
        Services = collection.BuildServiceProvider();

        var vm = Services.GetRequiredService<MainViewModel>();
        var window = new MainWindow(vm);
        window.Loaded += (_, _) =>
        {
            if (e.Args.Length > 0)
            {
                vm.OnFileDrop(e.Args[0]);
            }
        };
        MainWindow = window;
        window.Show();
    }

    /// <summary>应用退出时释放图像等单例资源。</summary>
    /// <param name="e">退出事件。</param>
    protected override void OnExit(ExitEventArgs e)
    {
        // 释放 IDisposable 服务
        (Services as IDisposable)?.Dispose();
        base.OnExit(e);
    }
}
