using System.ComponentModel;
using System.Diagnostics;
using static Tactile.Audio.CoreAudioInterop;

namespace Tactile.Audio;

/// <summary>Creates the Windows loopback audio system.</summary>
public static class AudioSystemFactory
{
    /// <summary>Creates an audio system without opening a device.</summary>
    public static IAudioSystem Create() => new WindowsAudioSystem();

    private sealed class WindowsAudioSystem : IAudioSystem
    {
        public IAudioCapture Start(AudioTarget target)
        {
            if (!OperatingSystem.IsWindows())
                throw new AudioUnavailableException("Ses yakalama yalnızca Windows'ta kullanılabilir.");
            if (target is AppAudio && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
                throw new AudioUnavailableException("Uygulama sesi yakalama bu Windows sürümünde yok.");
            if (target is not (SystemAudio or AppAudio))
                throw new AudioUnavailableException("Ses yakalama kaynağı geçersiz.");

            return WasapiLoopback.Start(target);
        }

        public IReadOnlyList<AudioApp> ListApps()
        {
            if (!OperatingSystem.IsWindows())
                return [];

            IMMDeviceEnumerator? devices = null;
            IMMDevice? device = null;
            IAudioSessionManager2? manager = null;
            IAudioSessionEnumerator? sessions = null;
            try
            {
                devices = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                device = DefaultRender(devices);
                manager = Activate<IAudioSessionManager2>(device);
                Check(manager.GetSessionEnumerator(out sessions), "Ses oturumları açılamadı.");
                Check(sessions.GetCount(out int count), "Ses oturumları sayılamadı.");
                Dictionary<string, AudioApp> apps = new(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    AudioApp? app = ReadApp(sessions, i);
                    if (app is not null &&
                        (!apps.TryGetValue(app.ProcessName, out var other) || app.ProcessId < other.ProcessId))
                        apps[app.ProcessName] = app;
                }
                return apps.Values.OrderBy(app => app.ProcessName, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception)
            {
                return [];
            }
            finally
            {
                Release(sessions);
                Release(manager);
                Release(device);
                Release(devices);
            }
        }

        private static AudioApp? ReadApp(IAudioSessionEnumerator sessions, int index)
        {
            const string error = "Ses oturumu okunamadı.";
            Check(sessions.GetSession(index, out var value), error);
            try
            {
                var session = (IAudioSessionControl2)value;
                Check(session.GetState(out int state), error);
                if (state != 1)
                    return null;
                int systemSounds = session.IsSystemSoundsSession();
                Check(systemSounds, error);
                if (systemSounds == 0)
                    return null;
                Check(session.GetProcessId(out uint pid), error);
                if (pid == 0 || pid == Environment.ProcessId)
                    return null;

                string name;
                try
                {
                    using Process process = Process.GetProcessById(checked((int)pid));
                    name = process.ProcessName;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    // A session can outlive its process.
                    return null;
                }

                Check(session.GetDisplayName(out nint display), error);
                string displayName = TakeString(display);
                return new AudioApp((int)pid, name + ".exe", string.IsNullOrWhiteSpace(displayName) ? name : displayName);
            }
            finally
            {
                Release(value);
            }
        }
    }
}
