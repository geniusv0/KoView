using System.Globalization;
using System.Runtime.InteropServices;

namespace KoVideo;

internal sealed class Mpv : IDisposable
{
    private nint handle;

    public Mpv(nint window)
    {
        handle = mpv_create();
        if (handle == 0)
            throw new InvalidOperationException("libmpv를 초기화할 수 없습니다.");

        try
        {
            Option("wid", unchecked((uint)window.ToInt64()).ToString(CultureInfo.InvariantCulture));
            Option("vo", "gpu");
            Option("hwdec", "auto-safe");
            Option("osc", "no");
            Option("osd-align-x", "right");
            Option("osd-align-y", "top");
            Option("osd-color", "#FFFFFFFF");
            Option("osd-scale-by-window", "no");
            Option("osd-font-size", "28");
            Option("osd-margin-x", "8");
            Option("osd-margin-y", "8");
            Option("input-default-bindings", "no");
            Option("input-vo-keyboard", "no");
            Option("input-cursor", "no");
            Option("keep-open", "yes");
            Option("load-scripts", "no");
            Check(mpv_initialize(handle));
            Check(mpv_request_log_messages(handle, "warn"));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Option(string name, string value) => Check(mpv_set_option_string(handle, name, value));

    public void Set(string name, string value) => Check(mpv_set_property_string(handle, name, value));

    public void Command(params string[] arguments)
    {
        var encoded = new nint[arguments.Length + 1];
        nint argv = Marshal.AllocHGlobal(encoded.Length * nint.Size);
        try
        {
            for (int i = 0; i < arguments.Length; i++)
                encoded[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
            Marshal.Copy(encoded, 0, argv, encoded.Length);
            Check(mpv_command(handle, argv));
        }
        finally
        {
            foreach (nint argument in encoded)
                Marshal.FreeCoTaskMem(argument);
            Marshal.FreeHGlobal(argv);
        }
    }

    public double Number(string name) =>
        TryNumber(name, out double value) ? value : 0;

    public string? Get(string name)
    {
        nint value = mpv_get_property_string(handle, name);
        if (value == 0)
            return null;
        try { return Marshal.PtrToStringUTF8(value); }
        finally { mpv_free(value); }
    }

    public bool TryNumber(string name, out double value) =>
        GetDouble(handle, name, 5, out value) >= 0 && double.IsFinite(value);

    public bool Flag(string name) => GetFlag(handle, name, 3, out int value) >= 0 && value != 0;

    public string? NetworkError { get; private set; }

    public void ClearNetworkError() => NetworkError = null;

    public bool Poll(out bool ready, out bool videoReconfigured, out string? error)
    {
        ready = false;
        error = null;
        var entry = Marshal.PtrToStructure<MpvEvent>(mpv_wait_event(handle, 0));
        if (entry.Id == 2 && entry.Data != 0 &&
            Marshal.PtrToStringUTF8(Marshal.PtrToStructure<LogMessage>(entry.Data).Text) is { } line)
        {
            var kind = ClassifyNetworkError(line);
            if (kind is not null && NetworkError != "denied")
                NetworkError = kind;
        }
        videoReconfigured = entry.Id == 17; // MPV_EVENT_VIDEO_RECONFIG
        if (entry.Id == 8)
            ready = true;
        if (entry.Id == 7 && entry.Data != 0)
        {
            var end = Marshal.PtrToStructure<EndFile>(entry.Data);
            if (end.Reason == 4)
                error = Message(end.Error);
        }
        return entry.Id != 0;
    }

    internal static string? ClassifyNetworkError(string message)
    {
        if (message.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("401 (Unauthorized)", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("403 (Forbidden)", StringComparison.OrdinalIgnoreCase)) return "denied";
        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timeout expired", StringComparison.OrdinalIgnoreCase)) return "timeout";
        if (message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("I/O error", StringComparison.OrdinalIgnoreCase)) return "disconnected";
        return null;
    }

    public void Dispose()
    {
        if (handle == 0)
            return;
        mpv_terminate_destroy(handle);
        handle = 0;
    }

    private static string Message(int error) =>
        Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? $"mpv 오류 {error}";

    private static void Check(int result)
    {
        if (result < 0)
            throw new InvalidOperationException(Message(result));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEvent
    {
        public int Id, Error;
        public ulong ReplyUserdata;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EndFile
    {
        public int Reason, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LogMessage { public nint Prefix, Level, Text; }

    private const string Library = "libmpv-2.dll";

    [DllImport(Library)] private static extern nint mpv_create();
    [DllImport(Library)] private static extern int mpv_initialize(nint ctx);
    [DllImport(Library)] private static extern void mpv_terminate_destroy(nint ctx);
    [DllImport(Library)] private static extern int mpv_command(nint ctx, nint args);
    [DllImport(Library)] private static extern nint mpv_error_string(int error);
    [DllImport(Library)] private static extern void mpv_free(nint value);
    [DllImport(Library)] private static extern nint mpv_get_property_string(nint ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library)] private static extern nint mpv_wait_event(nint ctx, double timeout);
    [DllImport(Library)] private static extern int mpv_request_log_messages(nint ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string level);
    [DllImport(Library)] private static extern int mpv_set_option_string(nint ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Library)] private static extern int mpv_set_property_string(nint ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Library, EntryPoint = "mpv_get_property")] private static extern int GetDouble(nint ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double value);
    [DllImport(Library, EntryPoint = "mpv_get_property")] private static extern int GetFlag(nint ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out int value);
}
