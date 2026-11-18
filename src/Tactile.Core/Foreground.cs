using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tactile.Core;

/// <summary>Tells which program is in front.</summary>
public interface IForegroundSource
{
    /// <summary>Raised with the new exe file name, e.g. <c>game.exe</c>.</summary>
    event Action<string>? Changed;

    string? Current { get; }
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
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly Timer _timer;
    private readonly int _ownPid = Environment.ProcessId;
    private readonly RecentNames _recent = new();
    private uint _lastPid;
    private volatile string? _current;
    private int _polling;

    public WindowsForegroundSource()
    {
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, Interval);
    }

    public event Action<string>? Changed;

    public string? Current => _current;

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
            if (pid == 0 || pid == _ownPid || pid == _lastPid)
            {
                return;
            }

            string? exe = ExeName(pid);
            if (exe is null)
            {
                return;
            }

            _lastPid = pid;
            if (string.Equals(exe, _current, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _current = exe;
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
