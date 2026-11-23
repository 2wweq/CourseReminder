using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CourseReminder.Views;

/// <summary>
/// 自绘的课前提醒卡片（路线 C）：右下角滑入，6 秒后自动消失。
/// 不依赖系统通知通道，系统关闭通知/开启专注助手时依然可见。
/// 点击卡片 → 回调打开「今日剩余课程」。
/// </summary>
public partial class ToastWindow : Window
{
    private const double Edge = 20;
    private const double Gap = 10;
    private const int LifeSeconds = 6;

    private static readonly List<ToastWindow> Open = new();

    private readonly Action? _onClick;
    private DispatcherTimer? _timer;
    private bool _closing;

    private ToastWindow(string title, string body, Action? onClick)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        _onClick = onClick;

        Loaded += OnLoaded;
        MouseEnter += (_, _) => _timer?.Stop();
        MouseLeave += (_, _) => RestartTimer();
        MouseLeftButtonUp += OnCardClick;
    }

    /// <summary>当前打开的卡片（自检/探针用）。</summary>
    public static IReadOnlyList<ToastWindow> OpenWindows => Open.ToList();

    public string TitleValue => TitleText.Text;

    public string BodyValue => BodyText.Text;

    /// <summary>弹出一张提醒卡片；多张同时存在时向上叠放。</summary>
    public static void ShowToast(string title, string body, Action? onClick)
    {
        var win = new ToastWindow(title, body, onClick);
        Open.Add(win);
        win.Show();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CourseReminder.Data.Log.Info("卡片OnLoaded 开始");
        // 布局还没稳定时改动 Opacity/Transform 会让渲染管线死锁，
        // 因此这里只做定位与计时，动画推迟到空闲阶段再启动。
        Opacity = 1;
        Relayout();
        RestartTimer();
        CourseReminder.Data.Log.Info("卡片定位完成 Left=" + Left.ToString("F0") + " Top=" + Top.ToString("F0") + " H=" + ActualHeight.ToString("F0"));
        Dispatcher.BeginInvoke(new Action(() =>
        {
            CourseReminder.Data.Log.Info("开始淡入");
            FadeIn();
            CourseReminder.Data.Log.Info("淡入已启动");
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// 从工作区右下角开始，把当前所有卡片由新到旧向上重新叠放。
    /// 任何一张卡片关闭后也调用它，否则中间会出现空洞、后面的卡片不会回落。
    /// </summary>
    private static void Relayout()
    {
        var area = SystemParameters.WorkArea;
        var left = area.Right - Edge;
        var stacked = area.Bottom - Edge;

        // Open 是按弹出顺序（旧→新）存的；新的应该在最下面，所以倒序排
        foreach (var win in Open.AsEnumerable().Reverse())
        {
            if (!win.IsLoaded) continue;

            var width = win.ActualWidth > 0 ? win.ActualWidth : win.Width;
            var height = win.ActualHeight > 0 ? win.ActualHeight : win.DesiredSize.Height;
            if (height <= 0) height = 100;

            stacked -= height;
            if (stacked < area.Top + Edge) stacked = area.Top + Edge;

            win.Left = left - width;
            win.Top = stacked;
            stacked -= Gap;
        }
    }

    private void FadeIn()
    {
        // 只对窗口不透明度做动画：改动 RenderTransform 会在布局阶段触发死锁
        var fade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
        {
            FillBehavior = FillBehavior.Stop
        };
        Opacity = 1;
        BeginAnimation(OpacityProperty, fade);
    }

    private void RestartTimer()
    {
        if (_closing) return;
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(LifeSeconds) };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            CloseAnimated();
        };
        _timer.Start();
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        CloseAnimated();
        _onClick?.Invoke();
    }

    private void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        _timer?.Stop();

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Open.Remove(this);
        // 上方卡片下落补位
        Relayout();
    }
}
