using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KoVideo;

internal sealed class DesktopSurface : IDisposable
{
    private readonly nint parent;
    private readonly string monitorName;
    public nint Handle { get; private set; }

    public DesktopSurface(Screen monitor)
    {
        monitorName = monitor.DeviceName;
        nint progman = FindWindow("Progman", null);
        if (progman == 0)
            throw new InvalidOperationException("Windows 바탕화면을 찾을 수 없습니다.");

        SendMessageTimeout(progman, 0x052C, 0xD, 1, 2, 1000, out _);
        nint desktopWorker = FindWindowEx(progman, 0, "WorkerW", null);
        if (desktopWorker != 0)
            parent = desktopWorker;
        else
        {
            nint worker = 0;
            EnumWindows((window, _) =>
            {
                if (FindWindowEx(window, 0, "SHELLDLL_DefView", null) == 0)
                    return true;
                worker = FindWindowEx(0, window, "WorkerW", null);
                return false;
            }, 0);
            parent = worker;
        }

        if (parent == 0)
            throw new InvalidOperationException("아이콘 뒤에 영상을 표시할 수 있는 바탕화면 창을 찾을 수 없습니다.");

        try
        {
            // Create the built-in STATIC class directly without WinForms registering a derived class.
            Handle = CreateWindowEx(0, "STATIC", null,
                0x40000000 | 0x04000000 | 0x02000000, 0, 0, 1, 1, parent, 0, 0, 0);
            if (Handle == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "바탕화면 재생 창을 만들 수 없습니다.");
            ResizeToMonitor(monitor);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool IsAlive => Handle != 0 && IsWindow(Handle) && IsWindow(parent);

    public Screen? Monitor => Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == monitorName);

    public void ResizeToMonitor(Screen monitor)
    {
        var bounds = monitor.Bounds;
        var area = new WindowRect { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
        // Translate both corners to the Explorer window's coordinates, including negative screen origins.
        MapWindowPoints(0, parent, ref area, 2);
        if (!SetWindowPos(Handle, 0, area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top,
            0x0010 | 0x0040 | 0x0004u))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "바탕화면 재생 창을 배치할 수 없습니다.");
    }

    public void Dispose()
    {
        if (Handle != 0 && IsWindow(Handle))
            DestroyWindow(Handle);
        Handle = 0;
    }

    private delegate bool EnumCallback(nint hwnd, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string? title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int MapWindowPoints(nint from, nint to, ref WindowRect points, uint count);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
