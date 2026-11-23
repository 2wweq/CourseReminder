using System;
using System.Linq;
using System.Windows;
using CourseReminder.Data;
using CourseReminder.Sys;

namespace CourseReminder;

/// <summary>应用入口：单实例接管、便携数据目录初始化、全局异常兜底。</summary>
public partial class App : Application
{
    /// <summary>上一次弹「未处理异常」的时间，用于限流。</summary>
    private DateTime _lastErrorShown = DateTime.MinValue;
    private System.Windows.Threading.DispatcherTimer? _toastProbeCloser;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 自检模式：--selftest <测试目录> <报告文件>，不建窗口，跑完即退
        var stIndex = Array.FindIndex(e.Args, a => a == "--selftest");
        if (stIndex >= 0 && e.Args.Length >= stIndex + 3)
        {
            var code = SelfTest.Run(e.Args[stIndex + 1], e.Args[stIndex + 2]);
            Shutdown(code);
            return;
        }

        // 界面探针：--uiprobe <报告文件>，建窗口后把本周课表结构 dump 成文本（用于无界面环境验证渲染）
        var uiIndex = Array.FindIndex(e.Args, a => a == "--uiprobe");
        if (uiIndex >= 0 && e.Args.Length >= uiIndex + 2)
        {
            RunUiProbe(e.Args[uiIndex + 1]);
            return;
        }

        // 提醒卡片探针：--toastprobe <报告文件>，弹一张卡片并 dump 位置/尺寸/文本（无界面环境验证）
        var tpIndex = Array.FindIndex(e.Args, a => a == "--toastprobe");
        if (tpIndex >= 0 && e.Args.Length >= tpIndex + 2)
        {
            RunToastProbe(e.Args[tpIndex + 1]);
            return;
        }

        // 托盘探针已随托盘功能一并移除（程序不再创建托盘图标）

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("UI 线程未处理异常", args.Exception);
            args.Handled = true;

            // 同一个错误反复弹框会盖住真正要操作的内容；5 分钟内只提示一次，其余只记日志
            if (DateTime.Now - _lastErrorShown < TimeSpan.FromMinutes(5))
            {
                return;
            }
            _lastErrorShown = DateTime.Now;

            var text = "程序遇到未处理的错误：" + args.Exception.Message +
                       Environment.NewLine + "详细信息已写入程序目录 data/ 下的日志。";
            if (MainWindow is Window owner && owner.IsVisible)
            {
                MessageBox.Show(owner, text, "课表提醒", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show(text, "课表提醒", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        if (!SingleInstance.TryAcquireFirst())
        {
            // 已有实例在运行：通知它把窗口拉到前台，本进程安静退出
            if (!SingleInstance.SignalExistingInstance())
            {
                // 旧版本实例（或监听线程未就绪）无法被唤醒，给用户一个可操作的提示
                MessageBox.Show(
                    "课表提醒已经在运行，但这次没能把它唤到前台。" + Environment.NewLine +
                    "请到任务栏找到已经打开的课表提醒窗口（可能被最小化），" +
                    "或在任务管理器结束旧的 CourseReminder 进程后再打开。",
                    "课表提醒", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        // 监听第二实例的激活信号（后台线程），收到后切回主窗口
        SingleInstance.StartActivationListener(ActivateMainWindow);

        // 睡眠唤醒 / 系统时间被改动后立即重扫，避免错过提醒
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.TimeChanged += OnTimeChanged;

        try
        {
            PortablePaths.EnsureCreated();
            Log.Info("启动成功；数据目录：" + PortablePaths.DataDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "数据目录创建失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var minimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (minimized)
        {
            // 带 --minimized 启动（例如自己做的快捷方式）：直接缩到任务栏，不打扰用户
            window.CollapseOnStart();
        }
    }

    /// <summary>把主窗口显示到前台（供二次启动复用）。</summary>
    private void ActivateMainWindow()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (MainWindow is not Window window) return;

            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

            window.Activate();
            // 强制抢一次前台（Windows 会限制后台进程直接置顶）
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
            Log.Info("收到激活信号，主窗口已切到前台");
        }));
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            Log.Info("系统从睡眠恢复，重新扫描提醒");
            ScanAfterSystemChange();
        }
    }

    private void OnTimeChanged(object? sender, EventArgs e)
    {
        Log.Info("系统时间已变更，重新扫描提醒");
        ScanAfterSystemChange();
    }

    private void ScanAfterSystemChange()
    {
        Dispatcher.BeginInvoke(new Action(() => (MainWindow as MainWindow)?.ForceScan()));
    }

    /// <summary>界面探针：开窗 → 等布局完成 → dump 本周课表 → 退出。</summary>
    private void RunUiProbe(string reportPath)
    {
        try
        {
            PortablePaths.EnsureCreated();
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(reportPath, "数据目录初始化失败：" + ex.Message);
            Shutdown(2);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                System.IO.File.WriteAllText(reportPath, window.DumpWeekView());
                Shutdown(0);
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(reportPath, "探针失败：" + ex);
                Shutdown(3);
            }
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>提醒卡片探针：弹卡片 → 等布局 → dump 位置尺寸文本 → 退出。</summary>
    private void RunToastProbe(string reportPath)
    {
        try { System.IO.File.WriteAllText(reportPath + ".start", DateTime.Now.ToString("HH:mm:ss")); } catch { }

        try
        {
            PortablePaths.EnsureCreated();
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(reportPath, "数据目录初始化失败：" + ex.Message);
            Shutdown(2);
            return;
        }

        Log.Info("ToastProbe 开始：" + reportPath);
        // 只有提醒卡片、没有主窗口，禁止「无窗口即退出」
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            Views.ToastWindow.ShowToast(
                "课前提醒：高等数学",
                "今天（周日）08:10 上课\n地点：教三-201\n节次：第 1 大节（08:10–09:40）",
                null);
        }
        catch (Exception ex)
        {
            Log.Error("ToastProbe 弹卡片失败", ex);
            try { System.IO.File.WriteAllText(reportPath, "弹卡片失败：" + ex); } catch { }
            Shutdown(4);
            return;
        }

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var sb = new System.Text.StringBuilder();
                var wins = Views.ToastWindow.OpenWindows;
                sb.AppendLine("提醒卡片数量：" + wins.Count);
                foreach (var w in wins)
                {
                    sb.AppendLine("可见=" + w.IsVisible + " 置顶=" + w.Topmost + " 不占任务栏=" + !w.ShowInTaskbar);
                    sb.AppendLine("Left=" + w.Left.ToString("F0") + " Top=" + w.Top.ToString("F0")
                                  + " Width=" + w.ActualWidth.ToString("F0")
                                  + " Height=" + w.ActualHeight.ToString("F0"));
                    sb.AppendLine("工作区=" + SystemParameters.WorkArea);
                    sb.AppendLine("标题=" + w.TitleValue);
                    sb.AppendLine("正文=" + w.BodyValue.Replace("\n", " / "));
                }
                System.IO.File.WriteAllText(reportPath, sb.ToString());
                Log.Info("ToastProbe 已写报告");

                // 再等一轮，验证卡片会自己关掉（LifeSeconds=6）
                var closer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(7.5) };
                _toastProbeCloser = closer; // 持有引用，避免定时器被 GC 回收后永不触发
                closer.Tick += (_, _) =>
                {
                    closer.Stop();
                    var left = Views.ToastWindow.OpenWindows.Count;
                    System.IO.File.AppendAllText(reportPath, "自动关闭=" + (left == 0 ? "是" : "否，剩余 " + left + " 张") + Environment.NewLine);
                    Log.Info("ToastProbe 自动关闭检查：" + left + " 张");
                    Shutdown(left == 0 ? 0 : 5);
                };
                closer.Start();
            }
            catch (Exception ex)
            {
                Log.Error("ToastProbe 失败", ex);
                try { System.IO.File.WriteAllText(reportPath, "探针失败：" + ex); } catch { }
                Shutdown(3);
            }
        };
        timer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.TimeChanged -= OnTimeChanged;
        Log.Info("退出");
        SingleInstance.Release();
        base.OnExit(e);
    }
}
