using System.Runtime.InteropServices;

namespace Tactile.Input.Windows;

/// <summary>
/// Mute of the default recording device, through Core Audio. The device is looked up on every
/// call, so a changed default or an unplugged microphone is picked up without notifications.
/// </summary>
public sealed class WindowsMicrophone : IMicrophone
{
    private const int ClsctxAll = 0x17;
    private const int SOk = 0;

    private readonly Lock _gate = new();

    public bool? IsMuted
    {
        get
        {
            lock (_gate)
            {
                return Use(v => v.GetMute(out int muted) == SOk ? muted != 0 : (bool?)null);
            }
        }
    }

    public bool? ToggleMute()
    {
        lock (_gate)
        {
            return Use(v =>
            {
                if (v.GetMute(out int muted) != SOk)
                {
                    return null;
                }

                bool next = muted == 0;
                Guid context = Guid.Empty;
                return v.SetMute(next ? 1 : 0, ref context) == SOk ? next : (bool?)null;
            });
        }
    }

    private static bool? Use(Func<IAudioEndpointVolume, bool?> action)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? volume = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            // E_NOTFOUND when there is no recording device at all.
            if (enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out device) != SOk || device is null)
            {
                return null;
            }

            Guid iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, ClsctxAll, 0, out volume) != SOk || volume is not IAudioEndpointVolume v)
            {
                return null;
            }

            return action(v);
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            Release(volume);
            Release(device);
            Release(enumerator);
        }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.ReleaseComObject(com);
        }
    }

    private enum DataFlow
    {
        Render = 0,
        Capture = 1,
    }

    private enum Role
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    // Methods follow the vtable order of mmdeviceapi.h; unused ones keep their slot.
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(DataFlow dataFlow, int stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice? endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(nint client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? iface);

        [PreserveSig]
        int OpenPropertyStore(int access, out nint properties);

        [PreserveSig]
        int GetId(out nint id);

        [PreserveSig]
        int GetState(out int state);
    }

    // Vtable order of endpointvolume.h.
    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(nint notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(nint notify);

        [PreserveSig]
        int GetChannelCount(out uint count);

        [PreserveSig]
        int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float levelDb);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float levelDb);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute(int mute, ref Guid eventContext);

        [PreserveSig]
        int GetMute(out int mute);

        [PreserveSig]
        int GetVolumeStepInfo(out uint step, out uint stepCount);

        [PreserveSig]
        int VolumeStepUp(ref Guid eventContext);

        [PreserveSig]
        int VolumeStepDown(ref Guid eventContext);

        [PreserveSig]
        int QueryHardwareSupport(out uint mask);

        [PreserveSig]
        int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }
}
