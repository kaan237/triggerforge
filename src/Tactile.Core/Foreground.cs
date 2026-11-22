using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tactile.Core;

/// <summary>Tells which program is in front.</summary>
public interface IForegroundSource
{
    /// <summary>Raised with the new exe file name, e.g. <c>game.exe</c>.</summary>
    event Action<string>? Changed;

    string? Current { get; }
    bool IsFullscreen => false;

    (bool Fullscreen, string? Exe) GetFullscreenWindow() => IsFullscreen ? (true, Current) : default;
}

/// <summary>Most recent first, case-insensitive, no duplicates.</summary>
public sealed class RecentNames
{
    private readonly Lock _lock = new();
    private readonly LinkedList<string> _items = new();
    private readonly int _capacity;

    public RecentNames(int capacity = 10) => _capacity = capacity;

    public IReadOnlyList<string> Items
    {
        get
        {
            lock (_lock)
            {
                return _items.ToArray();
            }
        }
    }

    public void Add(string name)
    {
        lock (_lock)
        {
            LinkedListNode<string>? existing = _items.Find(name) ?? FindIgnoreCase(name);
            if (existing is not null)
            {
                _items.Remove(existing);
            }

            _items.AddFirst(name);
            while (_items.Count > _capacity)
            {
                _items.RemoveLast();
            }
        }
    }

    private LinkedListNode<string>? FindIgnoreCase(string name)
    {
        for (LinkedListNode<string>? n = _items.First; n is not null; n = n.Next)
        {
            if (string.Equals(n.Value, name, StringComparison.OrdinalIgnoreCase))
            {
                return n;
            }
        }

        return null;
    }
}

/// <summary>Polls the foreground window every 250 ms. Our own windows are ignored.</summary>
public sealed partial class WindowsForegroundSource : IForegroundSource, IDisposable
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int WindowStyle = -16;
    private const int Caption = 0x00C00000;
    private const int ThickFrame = 0x00040000;
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly Timer _timer;
    private readonly int _ownPid = Environment.ProcessId;
    private readonly RecentNames _recent = new();
    private sealed record ForegroundProcess(uint Pid, string Exe);
    private volatile ForegroundProcess? _current;
    private int _polling;

    public WindowsForegroundSource()
    {
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, Interval);
    }

    public event Action<string>? Changed;

    public string? Current => _current?.Exe;

    public bool IsFullscreen => GetFullscreenWindow().Fullscreen;

    public unsafe (bool Fullscreen, string? Exe) GetFullscreenWindow()
    {
        nint window = GetForegroundWindow();
        if (window == 0 || !IsWindowVisible(window) || IsIconic(window))
        {
            return default;
        }

        _ = GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0 || pid == _ownPid)
        {
            return default;
        }

        char* name = stackalloc char[64];
        int length = GetClassName(window, name, 64);
        nint monitor = MonitorFromWindow(window, 0);
        var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info) || !GetWindowRect(window, out Rect rect) ||
            !CoversMonitor(new ReadOnlySpan<char>(name, length), IsZoomed(window), GetWindowLong(window, WindowStyle), rect, info.Monitor))
        {
            return default;
        }

        ForegroundProcess? current = _current;
        return (true, current?.Pid == pid ? current.Exe : ExeName(pid));
    }

    internal static bool CoversMonitor(ReadOnlySpan<char> windowClass, bool maximized, int style, Rect bounds, Rect monitor) =>
        !maximized && (style & Caption) != Caption && (style & ThickFrame) == 0 &&
        windowClass is not ("" or "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "XamlExplorerHostIslandWindow" or "MultitaskingViewFrame" or "Windows.UI.Core.CoreWindow") &&
        bounds.Left == monitor.Left && bounds.Top == monitor.Top && bounds.Right == monitor.Right && bounds.Bottom == monitor.Bottom;

    /// <summary>Last 10 distinct exe names seen in front, most recent first.</summary>
    public IReadOnlyList<string> Recent => _recent.Items;

    public void Dispose() => _timer.Dispose();

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            nint hwnd = GetForegroundWindow();
            if (hwnd == 0)
            {
                return;
            }

            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            ForegroundProcess? previous = _current;
            if (pid == 0 || pid == _ownPid || pid == previous?.Pid)
            {
                return;
            }

            string? exe = ExeName(pid);
            if (exe is null)
            {
                return;
            }

            _current = new ForegroundProcess(pid, exe);
            if (string.Equals(exe, previous?.Exe, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _recent.Add(exe);
            Changed?.Invoke(exe);
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private static string? ExeName(uint pid)
    {
        nint process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process != 0)
        {
            try
            {
                Span<char> buffer = stackalloc char[1024];
                int size = buffer.Length;
                bool ok;
                unsafe
                {
                    fixed (char* p = buffer)
                    {
                        ok = QueryFullProcessImageName(process, 0, p, ref size);
                    }
                }

                if (ok)
                {
                    return Path.GetFileName(new string(buffer[..size]));
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }

        // Elevated or protected processes refuse the query; the process list still has the name.
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName + ".exe";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor, Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassName(nint window, char* name, int capacity);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* buffer, ref int size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
