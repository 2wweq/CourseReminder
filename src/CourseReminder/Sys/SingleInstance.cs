using System;
using System.Threading;

namespace CourseReminder.Sys;

/// <summary>
/// 命名互斥锁保证单实例；上次崩溃遗留的互斥锁可安全接管。
/// 另有一个命名事件用于「第二实例唤起已有窗口」。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "CourseReminder_SingleInstance_Mutex";
    private const string ActivateEventName = "CourseReminder_Activate_Event";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activateEvent;

    /// <summary>尝试成为首个实例；false 表示已有实例在运行。</summary>
    public static bool TryAcquireFirst()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            return true;
        }

        try
        {
            // 上次异常退出会留下“被遗弃”的互斥锁，这里接管所有权
            if (_mutex.WaitOne(TimeSpan.Zero))
            {
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            // WaitOne 已成功获得所有权
            return true;
        }

        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    /// <summary>首个实例启动后台监听：收到第二实例的信号后执行 onActivate（可能在非 UI 线程）。</summary>
    public static void StartActivationListener(Action onActivate)
    {
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }
        catch (Exception ex)
        {
            Data.Log.Error("创建激活事件失败，二次启动将无法唤起窗口", ex);
            return;
        }

        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _activateEvent.WaitOne();
                }
                catch
                {
                    // 事件被释放（程序退出）或出现异常，结束监听
                    return;
                }

                try
                {
                    onActivate();
                }
                catch (Exception ex)
                {
                    Data.Log.Error("响应激活信号失败", ex);
                }
            }
        })
        {
            IsBackground = true,
            Name = "CourseReminder.ActivateListener"
        };
        thread.Start();
    }

    /// <summary>第二实例调用：通知已有实例把主窗口显示到前台。返回是否通知成功。</summary>
    public static bool SignalExistingInstance()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ActivateEventName);
            ev.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // 旧版本（无激活监听）或监听线程未就绪：无法唤醒，但实例确实在运行
            return false;
        }
        catch (Exception ex)
        {
            Data.Log.Error("通知已有实例失败", ex);
            return false;
        }
    }

    public static void Release()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // 非持有者或已释放时忽略
        }

        _mutex?.Dispose();
        _mutex = null;

        try
        {
            _activateEvent?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _activateEvent = null;
    }
}
