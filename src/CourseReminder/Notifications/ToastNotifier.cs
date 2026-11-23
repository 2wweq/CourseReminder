using System;
using System.Linq;
using System.Media;
using CourseReminder.Data;
using CourseReminder.Models;
using CourseReminder.Services;
using CourseReminder.Views;

namespace CourseReminder.Notifications;

/// <summary>
/// 通知器：右下角自绘提醒卡片 + SystemSounds 提示音。
/// 不使用系统托盘：直接开窗口、点 X 最小化到任务栏，退出只走主窗口「设置」页的按钮。
/// 点击卡片 → 弹今日剩余课程窗口。
/// </summary>
public sealed class ToastNotifier : IDisposable
{
    private readonly ScheduleService _schedule;

    /// <summary>点击提醒卡片：查看今日剩余课程。</summary>
    public event Action? ShowTodayRequested;

    public ToastNotifier(ScheduleService schedule)
    {
        _schedule = schedule;
    }

    public void ShowReminder(PendingReminder reminder, bool playSound)
    {
        var c = reminder.Course;
        var startText = reminder.ClassStart.ToString(@"hh\:mm");
        var range = _schedule.SlotRangeText(c.Slot);
        var weekday = _schedule.WeekdayText(c.Weekday);
        var (title, body) = BuildToastText(c, startText, range, weekday);

        try
        {
            ToastWindow.ShowToast(title, body, () => ShowTodayRequested?.Invoke());

            if (playSound)
            {
                SystemSounds.Asterisk.Play();
            }

            Log.Info("已发送提醒：" + c.Name + "（" + startText + "）");
        }
        catch (Exception ex)
        {
            Log.Error("发送提醒失败", ex);
        }
    }

    /// <summary>提醒卡片的标题与正文（纯文本组装，便于自检）。</summary>
    public static (string Title, string Body) BuildToastText(Course c, string startText, string range, string weekday)
    {
        var title = "课前提醒：" + c.Name;
        var body =
            "今天（周" + weekday + "）" + startText + " 上课\n" +
            "地点：" + c.Room + "\n" +
            "节次：第 " + c.Slot + " 大节（" + range + "）";
        return (title, body);
    }

    /// <summary>点击通知后展示的今日剩余课程。</summary>
    public string BuildTodayRemainText(DateTime now)
    {
        var list = _schedule.TodayRemaining(now);
        if (list.Count == 0)
        {
            return "今天没有剩余课程了。";
        }

        var lines = list.Select(c =>
        {
            var range = _schedule.SlotRangeText(c.Slot);
            return range + "  " + c.Name + "  @ " + c.Room;
        });

        return "今日剩余课程：\n" + string.Join("\n", lines);
    }

    public void Dispose()
    {
        // 没有系统资源需要释放；保留方法以免调用方改动。
    }
}
