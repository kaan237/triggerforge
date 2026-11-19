using Tactile.Core;
using System.ComponentModel;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Tactile.Drivers;

internal enum HidHideProbe { Ok, NotFound, Busy, Failed }

/// <summary>One open control handle; HidHide allows only one system-wide, so keep it short.</summary>
internal interface IHidHideSession : IDisposable
{
    List<string> GetBlacklist();
    void SetBlacklist(IReadOnlyList<string> list);
    List<string> GetWhitelist();
    void SetWhitelist(IReadOnlyList<string> list);
    bool GetActive();
    void SetActive(bool active);
}

internal interface IHidHideControl
{
    HidHideProbe Probe();

    /// <summary>Null when the driver is missing or the handle is held elsewhere.</summary>
    IHidHideSession? Open();
}

/// <summary>REG_MULTI_SZ style UTF-16 lists used by the HidHide IOCTLs. Pure.</summary>
internal static class MultiSz
{
    public static byte[] Encode(IEnumerable<string> items)
    {
        var sb = new StringBuilder();
        foreach (string s in items)
        {
            if (s.Length == 0 || s.Contains('\0'))
                throw new ArgumentException("MULTI_SZ entries must be non-empty and contain no NUL.");
            sb.Append(s).Append('\0');
        }

        if (sb.Length == 0)
            sb.Append('\0');
        sb.Append('\0');
        return Encoding.Unicode.GetBytes(sb.ToString());
    }

    /// <summary>Keeps every non-empty string, even past a stray empty one, so a rewrite never drops entries.</summary>
    public static List<string> Decode(ReadOnlySpan<byte> bytes)
    {
        string all = Encoding.Unicode.GetString(bytes[..(bytes.Length & ~1)]);
        return [.. all.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }
}

internal static class NtPath
{
    /// <summary>C:\x\y.exe → \Device\HarddiskVolumeN\x\y.exe (HidHide whitelist form).</summary>
    public static string? FromWin32(string path, Func<string, string?> queryDosDevice, int depth = 0)
    {
        if (path.Length < 3 || path[1] != ':' || path[2] != '\\' || depth > 2)
            return null;
        string? target = queryDosDevice(path[..2]);
        if (string.IsNullOrEmpty(target))
            return null;
        string rest = path[2..];
        // A subst drive maps to another DOS path (\??\D:\folder); resolve that in turn.
        if (target.StartsWith(@"\??\", StringComparison.Ordinal))
            return FromWin32(target[4..].TrimEnd('\\') + rest, queryDosDevice, depth + 1);
        return target.TrimEnd('\\') + rest;
    }

    public static string? QueryDosDevice(string drive)
    {
        var buf = new char[1024];
        int n = Native.QueryDosDevice(drive, buf, buf.Length);
        if (n == 0)
            return null;
        int end = Array.IndexOf(buf, '\0');
        return new string(buf, 0, end < 0 ? n : end);
    }
}

internal sealed unsafe class WindowsHidHideControl : IHidHideControl
{
    private const string DevicePath = @"\\.\HidHide";
    private const uint GetWhitelistCode = 0x80016000;
    private const uint SetWhitelistCode = 0x80016004;
    private const uint GetBlacklistCode = 0x80016008;
    private const uint SetBlacklistCode = 0x8001600C;
    private const uint GetActiveCode = 0x80016010;
    private const uint SetActiveCode = 0x80016014;

    // Serialises our own opens; the driver allows only one handle at a time.
    private static readonly Lock Gate = new();

    public HidHideProbe Probe()
    {
        lock (Gate)
        {
            using SafeFileHandle h = OpenRaw(out int error);
            if (!h.IsInvalid)
                return HidHideProbe.Ok;
            return error switch
            {
                Native.ErrorFileNotFound or Native.ErrorPathNotFound => HidHideProbe.NotFound,
                Native.ErrorAccessDenied or Native.ErrorSharingViolation => HidHideProbe.Busy,
                _ => HidHideProbe.Failed,
            };
        }
    }

    public IHidHideSession? Open()
    {
        Gate.Enter();
        SafeFileHandle h = OpenRaw(out _);
        if (h.IsInvalid)
        {
            h.Dispose();
            Gate.Exit();
            return null;
        }

        return new Session(h);
    }

    private static SafeFileHandle OpenRaw(out int error)
    {
        SafeFileHandle h = Native.Open(DevicePath, Native.GenericRead, Native.FileShareRead | Native.FileShareWrite | Native.FileShareDelete);
        error = h.IsInvalid ? System.Runtime.InteropServices.Marshal.GetLastPInvokeError() : 0;
        return h;
    }

    private sealed class Session(SafeFileHandle handle) : IHidHideSession
    {
        public List<string> GetBlacklist() => GetList(GetBlacklistCode);
        public void SetBlacklist(IReadOnlyList<string> list) => Set(SetBlacklistCode, MultiSz.Encode(list));
        public List<string> GetWhitelist() => GetList(GetWhitelistCode);
        public void SetWhitelist(IReadOnlyList<string> list) => Set(SetWhitelistCode, MultiSz.Encode(list));

        public bool GetActive()
        {
            byte v = 0;
            if (!Native.DeviceIoControl(handle, GetActiveCode, null, 0, &v, 1, out _, 0))
                throw Fail();
            return v != 0;
        }

        public void SetActive(bool active) => Set(SetActiveCode, [active ? (byte)1 : (byte)0]);

        private List<string> GetList(uint code)
        {
            // First call without a buffer only reports the needed size.
            Native.DeviceIoControl(handle, code, null, 0, null, 0, out int needed, 0);
            if (needed <= 0)
                return [];
            var buf = new byte[needed];
            int got;
            fixed (byte* p = buf)
            {
                if (!Native.DeviceIoControl(handle, code, null, 0, p, buf.Length, out got, 0))
                    throw Fail();
            }

            return MultiSz.Decode(buf.AsSpan(0, got));
        }

        private void Set(uint code, byte[] data)
        {
            fixed (byte* p = data)
            {
                if (!Native.DeviceIoControl(handle, code, p, data.Length, null, 0, out _, 0))
                    throw Fail();
            }
        }

        private static DriverException Fail() => new(new LocalizedMessage("Could not apply the HidHide setting.", "HidHide ayarı uygulanamadı."), new Win32Exception());

        public void Dispose()
        {
            handle.Dispose();
            Gate.Exit();
        }
    }
}
