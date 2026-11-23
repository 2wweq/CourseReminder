using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

// 启用了 UseWindowsForms，System.Drawing 与 WPF 有同名类型，这里用别名锁定 WPF 版本
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using CourseReminder.Data;
using CourseReminder.Models;
using CourseReminder.Notifications;
using CourseReminder.Services;
using CourseReminder.Sys;
using Microsoft.Win32;

namespace CourseReminder;

/// <summary>
/// 主窗口：今日课表 / 导入课表 / 设置 / 关于 四页；
/// 内含每分钟提醒扫描与通知器生命周期管理。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ScheduleService _schedule = new();
    private ToastNotifier? _notifier;
    private DispatcherTimer? _scanTimer;
    private readonly ObservableCollection<TodayItem> _todayItems = new();

    /// <summary>本周课表相对本周的偏移（0=本周，-1=上一周，1=下一周）。</summary>
    private int _weekOffset;

    /// <summary>真正退出程序（托盘「退出」）时为 true；普通关窗只隐藏。</summary>
    private bool _exiting;

    /// <summary>上次刷新视图时的「日期+当前大节」指纹，用于每分钟按需重绘。</summary>
    private string _lastRefreshKey = "";

    /// <summary>上次清理去重键的日期，用于每天 Trim 一次。</summary>
    private DateTime _lastTrimDate = DateTime.MinValue;

    // 注意：启用 UseWindowsForms 后 Brush 在 System.Drawing 与 System.Windows.Media 间二义，必须写全名
    private static readonly System.Windows.Media.Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x72, 0xC4));
    private static readonly System.Windows.Media.Brush CellBorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
    private static readonly System.Windows.Media.Brush TodayBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xF7, 0xFC));

    /// <summary>今日列表项：供 ListBox 绑定。</summary>
    public sealed class TodayItem
    {
        public string TimeRange { get; set; } = "";
        public string CourseInfo { get; set; } = "";
    }

    public MainWindow()
    {
        InitializeComponent();
        TodayList.ItemsSource = _todayItems;
        DataDirText.Text = PortablePaths.DataDir;
    }

    // ================= 生命周期 =================
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSettingsToUi();
        RefreshWeek();
        RefreshToday();
        StartNotifier();
        StartScanTimer();
        Log.Info("主窗口已就绪");
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // 普通关窗（点 X / Alt+F4）不退出进程，提醒继续跑；只有「退出课表提醒」才真正结束
        if (_exiting) return;

        e.Cancel = true;
        Collapse();
    }

    /// <summary>
    /// 收起主窗口：最小化到任务栏，任务栏按钮始终看得见，随时点得回来。
    /// 程序不再使用系统托盘，也没有「隐藏到托盘」这个选项。
    /// </summary>
    private void Collapse()
    {
        WindowState = WindowState.Minimized;
        Log.Info("主窗口已最小化到任务栏，提醒扫描继续运行");
    }

    /// <summary>启动时（例如带 --minimized）按同样规则收起窗口。</summary>
    public void CollapseOnStart() => Collapse();

    /// <summary>真正退出：停扫描、关窗并结束进程（只由「设置」页的退出按钮触发）。</summary>
    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;

        _scanTimer?.Stop();
        _scanTimer = null;
        _notifier?.Dispose();
        _notifier = null;

        Close();
        Application.Current.Shutdown();
    }

    /// <summary>立即扫描一次（睡眠唤醒/系统时间变化时由 App 调用）。</summary>
    public void ForceScan() => ScanReminders();

    private void StartNotifier()
    {
        if (_notifier != null) return;

        _notifier = new ToastNotifier(_schedule);
        _notifier.ShowTodayRequested += ShowTodayRemain;
    }

    /// <summary>把主窗口唤回前台（二次启动、点击提醒卡片时复用）。</summary>
    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>点击提醒卡片：切到今日页并列出剩余课程。</summary>
    private void ShowTodayRemain()
    {
        ShowMainWindow();
        MainTabs.SelectedIndex = 1;
        RefreshToday();

        MessageBox.Show(this, _notifier?.BuildTodayRemainText(DateTime.Now) ?? "今日课程数据不可用。",
            "今日剩余课程", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>每分钟扫描一次待发提醒（NFR-002 误差 ≤ 1 分钟）。</summary>
    private void StartScanTimer()
    {
        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _scanTimer.Tick += (_, _) => ScanReminders();
        _scanTimer.Start();
        ScanReminders();
    }

    private void ScanReminders()
    {
        try
        {
            var now = DateTime.Now;
            var advance = _schedule.Settings.AdvanceMinutes > 0
                ? _schedule.Settings.AdvanceMinutes
                : 40;

            // 每天清理一次去重键（保留 60 天），避免 state.json 无限增长
            if (now.Date != _lastTrimDate)
            {
                var before = _schedule.State.SentKeys.Count;
                _schedule.State.Trim(now);
                _lastTrimDate = now.Date;
                if (_schedule.State.SentKeys.Count != before)
                {
                    try { _schedule.SaveState(); }
                    catch (Exception ex) { Log.Error("清理提醒状态失败", ex); }
                }
            }

            var pending = ReminderCalculator.Evaluate(_schedule, now, advance);

            foreach (var item in pending)
            {
                StartNotifier();
                if (_notifier == null)
                {
                    // 通知器不可用时不要标记已发，留给下次扫描重试
                    Log.Error("通知器未初始化，提醒保留到下次扫描：" + item.Key);
                    continue;
                }

                _notifier.ShowReminder(item, _schedule.Settings.PlaySound);
                _schedule.State.TryMarkSent(item.Key);
            }

            if (pending.Count > 0)
            {
                _schedule.SaveState();
            }

            // 每分钟按需刷新：日期或「当前正在上的大节」变化时才重绘，
            // 既保证描边/今日列表每分钟更新，又避免整张表每分钟重建
            var refreshKey = now.Date.Ticks + "|" + CurrentSlotKey(now);
            if (refreshKey != _lastRefreshKey)
            {
                _lastRefreshKey = refreshKey;
                RefreshToday();
                RefreshWeek();
            }
        }
        catch (Exception ex)
        {
            Log.Error("提醒扫描失败", ex);
        }
    }

    /// <summary>当前时刻所处的大节（用于判断是否需要重绘）；不在任何大节内返回 "-"。</summary>
    private string CurrentSlotKey(DateTime now)
    {
        var slot = _schedule.Current.Slots.FirstOrDefault(s => now.TimeOfDay >= s.Start && now.TimeOfDay < s.End);
        return slot == null ? "-" : slot.Slot.ToString();
    }

    // ================= 今日课表 =================
    private void RefreshToday_Click(object sender, RoutedEventArgs e) => RefreshToday();

    private void RefreshToday()
    {
        var now = DateTime.Now;
        _todayItems.Clear();

        var weekday = _schedule.WeekdayText(((int)now.DayOfWeek + 6) % 7 + 1);
        var week = _schedule.TeachingWeek(now);
        TodayDateText.Text = now.ToString("yyyy年M月d日") + "  周" + weekday +
                             (week >= 1 ? "（第 " + week + " 教学周）" : "（不在学期内）");

        var courses = _schedule.CoursesOn(now.Date);
        if (courses.Count == 0)
        {
            _todayItems.Add(new TodayItem { TimeRange = "今天没有课", CourseInfo = "" });
            return;
        }

        foreach (var c in courses)
        {
            var isPast = _schedule.SlotEnd(c.Slot) <= now.TimeOfDay;
            var prefix = isPast ? "【已过】" : "";
            _todayItems.Add(new TodayItem
            {
                TimeRange = prefix + "第 " + c.Slot + " 大节  " + _schedule.SlotRangeText(c.Slot),
                CourseInfo = c.Name + "  @ " + c.Room +
                             (string.IsNullOrWhiteSpace(c.Teacher) ? "" : "  教师：" + c.Teacher) +
                             (c.WeekRuleText.Length == 0 ? "" : "  [" + c.WeekRuleText + "]")
            });
        }
    }

    // ================= 本周课表 =================
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 切到本周课表页时重绘一次，保证日期/高亮是最新的
        if (MainTabs.SelectedIndex == 0) RefreshWeek();
    }

    private void PrevWeek_Click(object sender, RoutedEventArgs e)
    {
        _weekOffset--;
        RefreshWeek();
    }

    private void ThisWeek_Click(object sender, RoutedEventArgs e)
    {
        _weekOffset = 0;
        RefreshWeek();
    }

    private void NextWeek_Click(object sender, RoutedEventArgs e)
    {
        _weekOffset++;
        RefreshWeek();
    }

    /// <summary>重绘本周课表：7 列（周一~周日）× 节次行数，今天列浅色底纹，当前这节加粗边框。</summary>
    private void RefreshWeek()
    {
        WeekGrid.Children.Clear();
        WeekGrid.ColumnDefinitions.Clear();
        WeekGrid.RowDefinitions.Clear();

        var now = DateTime.Now;
        var today = now.Date;
        var monday = ScheduleService.MondayOf(today).AddDays(_weekOffset * 7);
        var slots = _schedule.Current.Slots.OrderBy(s => s.Slot).ToList();

        var week = _schedule.TeachingWeek(monday);
        WeekRangeText.Text = (week >= 1 ? "第 " + week + " 教学周" : "不在学期内") + "　" +
                             monday.ToString("yyyy-MM-dd") + " ~ " + monday.AddDays(6).ToString("MM-dd") +
                             (_weekOffset == 0 ? "" : "　（非本周）");

        if (slots.Count == 0)
        {
            WeekGrid.Children.Add(new TextBlock
            {
                Text = "还没有课表：请到「导入课表」页导入 Excel，或点「手动录入 / 编辑课程」逐门添加。",
                Foreground = Brushes.Gray,
                Margin = new Thickness(8)
            });
            return;
        }

        var matrix = _schedule.WeekMatrix(monday).ToDictionary(c => (c.Weekday, c.Slot));

        // 列：0=节次标签，1~7=周一到周日
        WeekGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 7; i++)
            WeekGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 行：0=表头，1~N=大节
        WeekGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < slots.Count; i++)
            WeekGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 左上角
        AddWeekCell(new TextBlock
        {
            Text = "节次",
            FontSize = 12,
            Foreground = Brushes.Gray,
            Margin = new Thickness(6, 6, 8, 6)
        }, 0, 0);

        // 表头：周几 + 日期
        for (var d = 0; d < 7; d++)
        {
            var day = monday.AddDays(d);
            var isToday = _weekOffset == 0 && day == today;

            var head = new StackPanel
            {
                Margin = new Thickness(4, 6, 4, 6),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            head.Children.Add(new TextBlock
            {
                Text = "周" + _schedule.WeekdayText(d + 1),
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Foreground = isToday ? AccentBrush : Brushes.Black
            });
            head.Children.Add(new TextBlock
            {
                Text = day.ToString("MM/dd"),
                FontSize = 11,
                Foreground = Brushes.Gray,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            });
            AddWeekCell(head, 0, d + 1);
        }

        // 内容
        for (var r = 0; r < slots.Count; r++)
        {
            var slot = slots[r];

            var label = new StackPanel { Margin = new Thickness(6, 4, 10, 4) };
            label.Children.Add(new TextBlock
            {
                Text = "第 " + slot.Slot + " 大节",
                FontWeight = FontWeights.Bold,
                FontSize = 12
            });
            label.Children.Add(new TextBlock
            {
                Text = slot.Start.ToString(@"hh\:mm") + "–" + slot.End.ToString(@"hh\:mm"),
                FontSize = 11,
                Foreground = Brushes.Gray
            });
            AddWeekCell(label, r + 1, 0);

            for (var d = 0; d < 7; d++)
            {
                var day = monday.AddDays(d);
                var isToday = _weekOffset == 0 && day == today;
                var isNow = isToday && now.TimeOfDay >= slot.Start && now.TimeOfDay < slot.End;

                var border = new Border
                {
                    Margin = new Thickness(2),
                    Padding = new Thickness(4, 3, 4, 3),
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(isNow ? 2 : 1),
                    BorderBrush = isNow ? AccentBrush : CellBorderBrush,
                    Background = isToday ? TodayBrush : Brushes.White
                };

                if (matrix.TryGetValue((d + 1, slot.Slot), out var cell) && cell.HasCourse)
                {
                    // 同一格可能有单双周互补的两门课，逐门显示
                    var sp = new StackPanel();
                    foreach (var course in cell.Courses)
                    {
                        sp.Children.Add(new TextBlock
                        {
                            Text = course.Name,
                            FontSize = 12,
                            FontWeight = course.IsActive ? FontWeights.Bold : FontWeights.Normal,
                            Foreground = course.IsActive ? Brushes.Black : Brushes.Gray,
                            TextWrapping = TextWrapping.Wrap
                        });
                        sp.Children.Add(new TextBlock
                        {
                            Text = course.IsActive ? course.Room : "本周不上",
                            FontSize = 11,
                            Foreground = Brushes.Gray,
                            TextWrapping = TextWrapping.Wrap
                        });
                    }

                    border.Child = sp;
                }

                AddWeekCell(border, r + 1, d + 1);
            }
        }
    }

    /// <summary>把当前本周课表的结构 dump 成文本，供 --uiprobe 在无界面环境验证渲染结果。</summary>
    public string DumpWeekView()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("列定义=" + WeekGrid.ColumnDefinitions.Count + "，行定义=" + WeekGrid.RowDefinitions.Count +
                      "，子元素=" + WeekGrid.Children.Count);
        sb.AppendLine("标题=" + WeekRangeText.Text);

        foreach (var head in WeekGrid.Children.OfType<StackPanel>().Where(x => Grid.GetRow(x) == 0))
        {
            sb.AppendLine("表头[列" + Grid.GetColumn(head) + "]=" +
                          string.Join(" ", head.Children.OfType<TextBlock>().Select(t => t.Text)));
        }

        foreach (var border in WeekGrid.Children.OfType<Border>())
        {
            var pos = "[行" + Grid.GetRow(border) + ",列" + Grid.GetColumn(border) + "]";
            var content = border.Child is StackPanel sp
                ? string.Join(" / ", sp.Children.OfType<TextBlock>().Select(t => t.Text))
                : "（空）";
            sb.AppendLine("格子" + pos + "=" + content);
        }

        return sb.ToString();
    }

    private void AddWeekCell(UIElement el, int row, int col)
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, col);
        WeekGrid.Children.Add(el);
    }

    // ================= 导入课表 =================
    private void SelectExcel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择课表 Excel",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true) return;

        ImportFileText.Text = dialog.FileName;
        ImportPreviewList.Items.Clear();
        ConfirmImportBtn.IsEnabled = false;
        CancelImportBtn.IsEnabled = false;

        var result = _schedule.ImportPreview(dialog.FileName);

        if (!result.Ok)
        {
            ImportStatusText.Text = "导入被拦截，共 " + result.Errors.Count + " 处问题（旧课表未改动）：";
            ImportStatusText.Foreground = System.Windows.Media.Brushes.DarkRed;
            foreach (var err in result.Errors)
            {
                ImportPreviewList.Items.Add(err.ToString());
            }
            return;
        }

        ImportStatusText.Text = "解析成功：课程 " + result.Courses.Count + " 条、节次 " +
                                result.Slots.Count + " 条。请核对下列内容后点「确认导入」。";
        ImportStatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;

        foreach (var c in result.Courses.OrderBy(x => x.Weekday).ThenBy(x => x.Slot))
        {
            ImportPreviewList.Items.Add(
                "周" + _schedule.WeekdayText(c.Weekday) + "  第 " + c.Slot + " 大节  " +
                c.Name + "  @ " + c.Room +
                (c.WeekRuleText.Length == 0 ? "  [每周]" : "  [" + c.WeekRuleText + "]"));
        }

        ConfirmImportBtn.IsEnabled = true;
        CancelImportBtn.IsEnabled = true;
    }

    private void ManualEdit_Click(object sender, RoutedEventArgs e)
    {
        var editor = new Views.CourseEditorWindow(_schedule) { Owner = this };
        editor.ShowDialog();

        if (!editor.Changed) return;

        RefreshWeek();
        RefreshToday();
        ImportStatusText.Text = "课表已手动更新（保存到 data/schedule.json）。";
        ImportStatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;
    }

    private void ConfirmImport_Click(object sender, RoutedEventArgs e)    {
        _schedule.ConfirmPendingImport();
        ImportStatusText.Text = "已导入并保存。今日课表已按新课表刷新。";
        ImportStatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;
        ConfirmImportBtn.IsEnabled = false;
        CancelImportBtn.IsEnabled = false;
        RefreshWeek();
        RefreshToday();
    }

    private void CancelImport_Click(object sender, RoutedEventArgs e)
    {
        _schedule.CancelPendingImport();
        ImportStatusText.Text = "已取消，旧课表保持不变。";
        ImportStatusText.Foreground = System.Windows.Media.Brushes.Gray;
        ImportPreviewList.Items.Clear();
        ConfirmImportBtn.IsEnabled = false;
        CancelImportBtn.IsEnabled = false;
    }

    // ================= 设置 =================
    private void LoadSettingsToUi()
    {
        var s = _schedule.Settings;
        AdvanceMinutesBox.Text = s.AdvanceMinutes.ToString();
        PlaySoundBox.IsChecked = s.PlaySound;

        FirstMondayPicker.SelectedDate = s.Semester.FirstMonday;
        EndDatePicker.SelectedDate = s.Semester.EndDate;
        UpdateWeekInfo();
    }

    private void UpdateWeekInfo()
    {
        var sem = _schedule.Settings.Semester;
        if (!sem.IsConfigured)
        {
            WeekInfoText.Text = "未配置学期：周次规则暂按第 1 周处理，请填写学期起止。";
            return;
        }

        var week = _schedule.TeachingWeek(DateTime.Now);
        if (week >= 1)
        {
            WeekInfoText.Text = "当前为第 " + week + " 教学周（学期共 " + _schedule.TotalWeeks + " 周）";
            return;
        }

        var start = _schedule.FirstMondayAligned ?? DateTime.Now.Date;
        WeekInfoText.Text = DateTime.Now.Date < start
            ? "当前不在学期内：学期尚未开始（" + start.ToString("yyyy-MM-dd") + " 起）。"
            : "当前不在学期内：学期已结束，不再产生课前提醒。";
    }

    /// <summary>设置页「退出课表提醒」：找不到托盘图标或托盘菜单时的退出入口。</summary>
    private void QuitApp_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "退出后不再产生课前提醒，直到你再次打开课表提醒。确定要退出吗？",
            "课表提醒", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            Log.Info("用户从设置页退出程序");
            ExitApp();
        }
    }

    /// <summary>测试提醒：立刻弹一条气泡，用于在不满足提醒时刻时确认通知效果。</summary>
    private void TestReminder_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        var todayCourses = _schedule.CoursesOn(now.Date);

        // 优先用「今天还没上的下一节课」做样本；否则用今天第一节课；今天没课就用内置示例
        var course = todayCourses.FirstOrDefault(c => _schedule.SlotEnd(c.Slot) > now.TimeOfDay)
                     ?? todayCourses.FirstOrDefault();

        Course sample;
        TimeSpan start;
        if (course != null)
        {
            sample = course;
            start = _schedule.SlotStart(course.Slot);
        }
        else
        {
            sample = new Course
            {
                Weekday = ((int)now.DayOfWeek + 6) % 7 + 1,
                Slot = 1,
                Name = "示例课程：高等数学",
                Room = "教三-201"
            };
            start = new TimeSpan(8, 10, 0);
        }

        StartNotifier();
        _notifier?.ShowReminder(
            new PendingReminder { Course = sample, ClassStart = start, DueAt = now, Key = "test-" + now.Ticks },
            _schedule.Settings.PlaySound);

        SettingsStatusText.Text = "已发送测试提醒（" + now.ToString("HH:mm:ss") + "），请看屏幕右下角气泡。";
        Log.Info("用户触发测试提醒：" + sample.Name);
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        // 先全部校验，校验不通过不碰内存里的设置
        if (!int.TryParse(AdvanceMinutesBox.Text.Trim(), out var advance) || advance < 1 || advance > 180)
        {
            MessageBox.Show(this, "提前量请填写 1~180 之间的整数（分钟）。", "设置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var firstMonday = FirstMondayPicker.SelectedDate;
        var endDate = EndDatePicker.SelectedDate;

        if (firstMonday.HasValue && firstMonday.Value.DayOfWeek != DayOfWeek.Monday)
        {
            var weekdayText = _schedule.WeekdayText(((int)firstMonday.Value.DayOfWeek + 6) % 7 + 1);
            MessageBox.Show(this, "学期第一周必须从周一开始，当前选的是周" + weekdayText + "。", "设置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (firstMonday.HasValue && endDate.HasValue &&
            endDate.Value.Date <= firstMonday.Value.Date)
        {
            MessageBox.Show(this, "学期结束日期必须晚于第一周周一。", "设置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var next = new Settings
        {
            AdvanceMinutes = advance,
            PlaySound = PlaySoundBox.IsChecked == true,
            Semester = new Semester
            {
                FirstMonday = firstMonday,
                EndDate = endDate
            }
        };

        try
        {
            _schedule.UpdateSettings(next);
        }
        catch (Exception ex)
        {
            Log.Error("保存设置失败", ex);
            MessageBox.Show(this, "设置保存失败：" + ex.Message, "设置",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SettingsStatusText.Text = "已保存（" + DateTime.Now.ToString("HH:mm:ss") + "）";
        UpdateWeekInfo();
        RefreshToday();
        RefreshWeek();
        Log.Info("设置已保存：提前 " + advance + " 分钟");
    }
}
