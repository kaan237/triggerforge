using System.Runtime.InteropServices;
using static Tactile.Audio.CoreAudioInterop;

namespace Tactile.Audio;

public enum AudioFlow { Render, Capture }

public enum AudioRole { Console, Multimedia, Communications }

public readonly record struct AudioDefaultSlot(AudioFlow Flow, AudioRole Role);

public readonly record struct AudioDefaultEndpoint(string Id, string? FriendlyName);

public interface IAudioDefaults
{
    IReadOnlyDictionary<AudioDefaultSlot, AudioDefaultEndpoint> ReadDefaults(bool includeNames);
    void Restore(string deviceId, AudioRole role);
}

public sealed class AudioDefaults : IAudioDefaults
{
    private const int NoEndpoint = unchecked((int)0x80070490);
    private const ushort WideString = 31;
    private static readonly AudioFlow[] Flows = Enum.GetValues<AudioFlow>();
    private static readonly AudioRole[] Roles = Enum.GetValues<AudioRole>();
    // PKEY_Device_FriendlyName
    private static readonly PropertyKey FriendlyName = new()
    {
        Format = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        Id = 14,
    };

    public IReadOnlyDictionary<AudioDefaultSlot, AudioDefaultEndpoint> ReadDefaults(bool includeNames)
    {
        var defaults = new Dictionary<AudioDefaultSlot, AudioDefaultEndpoint>();
        var devices = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            foreach (AudioFlow flow in Flows)
            {
                foreach (AudioRole role in Roles)
                {
                    int result = devices.GetDefaultAudioEndpoint((int)flow, (int)role, out var device);
                    if (result == NoEndpoint)
                        continue;

                    Check(result, "Varsayılan ses cihazı okunamadı.");
                    try
                    {
                        Check(device.GetId(out nint id), "Ses cihazının kimliği okunamadı.");
                        string deviceId = TakeString(id);
                        defaults.Add(new(flow, role), new(deviceId, includeNames ? ReadName(device) : null));
                    }
                    finally
                    {
                        Release(device);
                    }
                }
            }

            return defaults;
        }
        finally
        {
            Release(devices);
        }
    }

    public void Restore(string deviceId, AudioRole role)
    {
        var policy = RequireInterface<IPolicyConfig>(new PolicyConfigClient());
        try
        {
            Check(policy.SetDefaultEndpoint(deviceId, role), "Varsayılan ses cihazı değiştirilemedi.");
        }
        finally
        {
            Release(policy);
        }
    }

    private static string? ReadName(IMMDevice device)
    {
        Check(device.OpenPropertyStore(0, out var store), "Ses cihazının özellikleri açılamadı.");
        try
        {
            Check(store.GetValue(in FriendlyName, out PropertyVariant value), "Ses cihazının adı okunamadı.");
            try
            {
                return value.Type == WideString ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Release(store);
        }
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient;

    // Only SetDefaultEndpoint is called. The preceding slots preserve the native vtable order.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, AudioRole role);
    }
}
