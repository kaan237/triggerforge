using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static Tactile.Audio.CoreAudioInterop;

namespace Tactile.Audio;

internal sealed class WasapiLoopback : IAudioCapture
{
    private const string StreamError = "Ses yakalama durdu.";
    private const string EndpointChanged = "Varsayılan ses çıkışı değişti veya bağlantısı kesildi.";
    private readonly Lock _lifetime = new();
    private readonly ManualResetEvent _stop;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private bool _stopClosed;
    private volatile bool _running;
    private volatile bool _disposed;

    private WasapiLoopback(AudioTarget target)
    {
        Target = target;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "WASAPI loopback",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _stop = new ManualResetEvent(false);
    }

    public AudioTarget Target { get; }
    public bool Running => _running && !_disposed;
    public event AudioDataHandler? Data;
    public event Action<string>? Failed;

    internal static WasapiLoopback Start(AudioTarget target)
    {
        WasapiLoopback? capture = null;
        try
        {
            capture = new WasapiLoopback(target);
            capture._thread.Start();
            capture._started.Task.GetAwaiter().GetResult();
            return capture;
        }
        catch (Exception ex)
        {
            capture?.Dispose();
            if (ex is AudioUnavailableException)
                throw;
            throw new AudioUnavailableException("Ses yakalama başlatılamadı.", ex);
        }
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            _disposed = true;
            if (!_stopClosed)
            {
                if ((_thread.ThreadState & System.Threading.ThreadState.Unstarted) != 0)
                    CloseStopEvent();
                else
                    _stop.Set();
            }
        }

        if (Thread.CurrentThread != _thread && _thread.IsAlive)
        {
            try
            {
                _thread.Join(450);
            }
            catch (ThreadInterruptedException ex)
            {
                Debug.WriteLine(ex);
            }
        }
    }

    private void CloseStopEvent()
    {
        _stop.Dispose();
        _stopClosed = true;
    }

    private void Run()
    {
        string? failure = null;
        try
        {
            Capture();
        }
        catch (Exception ex)
        {
            var error = ex as AudioUnavailableException
                ?? new AudioUnavailableException(_started.Task.IsCompleted ? StreamError : "Ses yakalama başlatılamadı.", ex);
            if (!_started.TrySetException(error))
                failure = error.Message;
        }
        finally
        {
            _running = false;
            lock (_lifetime)
                CloseStopEvent();
        }

        if (failure is not null && !_disposed)
        {
            try
            {
                Failed?.Invoke(failure);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
    }

    private void Capture()
    {
        using AutoResetEvent ready = new(false);
        using TargetProcess? process = Target is AppAudio app ? FindProcess(app.ProcessName) : null;
        IMMDeviceEnumerator? devices = null;
        IMMDevice? endpoint = null;
        EndpointWatcher? watcher = null;
        nint notification = 0;
        IAudioClient? client = null;
        IAudioCaptureClient? reader = null;
        nint mmcss = 0;
        bool started = false;

        try
        {
            if (process is null)
            {
                devices = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                endpoint = DefaultRender(devices);
                string id = EndpointId(endpoint);
                var callback = new EndpointWatcher(id);
                // Registration does not AddRef the notification interface.
                notification = Marshal.GetComInterfaceForObject<EndpointWatcher, IMMNotificationClient>(callback);
                Check(devices.RegisterEndpointNotificationCallback(notification), "Ses çıkışı değişiklikleri izlenemiyor.");
                watcher = callback;

                // Close the gap between selecting the endpoint and subscribing for changes.
                var current = DefaultRender(devices);
                try
                {
                    Check(endpoint.GetState(out uint state), EndpointChanged);
                    if (state != DeviceActive || EndpointId(current) != id)
                        throw new AudioUnavailableException(EndpointChanged);
                }
                finally
                {
                    Release(current);
                }
            }

            bool eventDriven = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063);
            AudioFormatConverter format = Initialize(endpoint, process, eventDriven, ref client);
            if (eventDriven)
                Check(client!.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "Ses bekleme olayı ayarlanamadı.");

            Guid captureIid = typeof(IAudioCaptureClient).GUID;
            Check(client!.GetService(in captureIid, out var captureService), "Ses yakalama arabirimi açılamadı.");
            reader = RequireInterface<IAudioCaptureClient>(captureService);

            mmcss = AvSetMmThreadCharacteristics("Pro Audio", out _);
            CheckTarget(watcher, process);
            Check(client.Start(), "Ses akışı başlatılamadı.");
            started = true;
            _running = true;
            _started.SetResult();

            WaitHandle[] signals = process is null ? [_stop, ready] : [_stop, ready, process];
            while (true)
            {
                int signal = WaitHandle.WaitAny(signals, eventDriven ? 100 : 5);
                if (signal == 0)
                    return;
                CheckTarget(watcher, process);
                if (!eventDriven || signal == 1)
                    Drain(reader, format, watcher, process);
            }
        }
        finally
        {
            if (started)
                _ = client!.Stop();
            if (watcher is not null)
                _ = devices!.UnregisterEndpointNotificationCallback(notification);
            Release(reader);
            Release(client);
            Release(endpoint);
            Release(devices);
            if (notification != 0)
                Marshal.Release(notification);
            if (mmcss != 0)
                _ = AvRevertMmThreadCharacteristics(mmcss);
        }
    }

    private static string EndpointId(IMMDevice endpoint)
    {
        Check(endpoint.GetId(out nint id), "Ses çıkışı kimliği okunamadı.");
        return TakeString(id);
    }

    private static TargetProcess FindProcess(string executable)
    {
        string name = executable?.Trim() ?? string.Empty;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (name.Length == 0)
            throw new AudioUnavailableException("Uygulamanın dosya adı boş.");

        Process[] matches = Process.GetProcessesByName(name);
        try
        {
            foreach (var process in matches.OrderBy(process => process.Id))
            {
                const uint synchronize = 0x00100000;
                SafeWaitHandle handle = OpenProcess(synchronize, 0, (uint)process.Id);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    if (error is 87 or 1168)
                        continue;
                    throw new AudioUnavailableException($"{executable} işlemi izlenemiyor.", new Win32Exception(error));
                }

                var target = new TargetProcess(process.Id, handle);
                if (!target.WaitOne(0))
                    return target;
                target.Dispose();
            }
            throw new AudioUnavailableException($"{executable} çalışmıyor.");
        }
        finally
        {
            foreach (var process in matches)
                process.Dispose();
        }
    }

    private static void CheckTarget(EndpointWatcher? watcher, TargetProcess? process)
    {
        if (watcher?.Changed == true)
            throw new AudioUnavailableException(EndpointChanged);
        if (process?.WaitOne(0) == true)
            throw new AudioUnavailableException("Uygulama kapandı. Ses yakalama durdu.");
    }

    private static unsafe AudioFormatConverter Initialize(IMMDevice? endpoint, TargetProcess? process,
        bool eventDriven, ref IAudioClient? client)
    {
        const string error = "Ses yakalama biçimi ayarlanamadı.";
        uint flags = Loopback | (eventDriven ? EventCallback : 0);
        WaveFormat format = new(WaveFormat.Float, 2, AudioFormatConverter.OutputRate, 32);
        client = endpoint is not null ? Activate<IAudioClient>(endpoint) : ActivateProcess(process!.Id);
        uint conversion = endpoint is not null ? ConvertPcm | DefaultSrcQuality : 0;
        // Process loopback needs an explicit buffer length (20 ms, in 100 ns units); the device path uses its default.
        long duration = endpoint is null ? 200_000 : 0;
        int result = client.Initialize(0, flags | conversion, duration, 0, (nint)(&format), 0);
        if (result != UnsupportedFormat && (endpoint is not null || result != unchecked((int)0x80070057)))
        {
            Check(result, error);
            return new AudioFormatConverter(format);
        }

        // A failed Initialize can leave the client unusable for a second attempt.
        Release(client);
        client = null;
        if (endpoint is null)
        {
            client = ActivateProcess(process!.Id);
            format = new WaveFormat(WaveFormat.Pcm, 2, AudioFormatConverter.OutputRate, 16);
            Check(client.Initialize(0, flags, duration, 0, (nint)(&format), 0), error);
            return new AudioFormatConverter(format);
        }

        client = Activate<IAudioClient>(endpoint);
        Check(client.GetMixFormat(out nint mix), error);
        try
        {
            format = Marshal.PtrToStructure<WaveFormat>(mix);
            if (format.Tag == WaveFormat.Extensible && format.ExtraSize >= 22)
            {
                Guid subtype = Marshal.PtrToStructure<Guid>(mix + 24);
                format.Tag = subtype == new Guid("00000003-0000-0010-8000-00AA00389B71") ? WaveFormat.Float :
                    subtype == new Guid("00000001-0000-0010-8000-00AA00389B71") ? WaveFormat.Pcm : WaveFormat.Extensible;
            }
            var converter = new AudioFormatConverter(format);
            Check(client.Initialize(0, flags, 0, 0, mix, 0), error);
            return converter;
        }
        finally
        {
            Marshal.FreeCoTaskMem(mix);
        }
    }

    private static unsafe IAudioClient ActivateProcess(int pid)
    {
        const string error = "Uygulama sesi yakalama arabirimi açılamadı.";
        var parameters = new ProcessActivation { Type = 1, ProcessId = (uint)pid, Mode = 0 };
        var variant = new BlobVariant { Type = 65, Size = (uint)sizeof(ProcessActivation), Data = (nint)(&parameters) };
        var callback = new ActivationCompletion();
        nint completion = Marshal.GetComInterfaceForObject<ActivationCompletion, IActivateAudioInterfaceCompletionHandler>(callback);
        nint operation = 0;
        try
        {
            Guid iid = typeof(IAudioClient).GUID;
            Check(ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", in iid, in variant, completion, out operation), error);
            if (!callback.Completed.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new AudioUnavailableException("Uygulama sesi yakalama isteği zaman aşımına uğradı.");

            object result = Marshal.GetObjectForIUnknown(operation);
            try
            {
                var activation = (IActivateAudioInterfaceAsyncOperation)result;
                Check(activation.GetActivateResult(out int status, out var client), error);
                if (status < 0)
                    Release(client);
                Check(status, error);
                return RequireInterface<IAudioClient>(client);
            }
            finally
            {
                Release(result);
            }
        }
        finally
        {
            if (operation != 0)
                Marshal.Release(operation);
            Marshal.Release(completion);
        }
    }

    private void Drain(IAudioCaptureClient reader, AudioFormatConverter format, EndpointWatcher? watcher, TargetProcess? process)
    {
        while (!_disposed)
        {
            CheckTarget(watcher, process);
            Check(reader.GetNextPacketSize(out uint available), StreamError);
            if (available == 0)
                return;

            int result = reader.GetBuffer(out nint data, out uint frames, out uint flags, 0, 0);
            Check(result, StreamError);
            if (result == BufferEmpty)
                return;

            ReadOnlySpan<float> packet;
            try
            {
                packet = format.ConvertPacket(data, checked((int)frames), (flags & Silent) != 0);
            }
            finally
            {
                Check(reader.ReleaseBuffer(frames), StreamError);
            }

            if (!_disposed && !packet.IsEmpty)
                Data?.Invoke(packet);
        }
    }

    private sealed class TargetProcess : WaitHandle
    {
        internal TargetProcess(int id, SafeWaitHandle handle)
        {
            Id = id;
            SafeWaitHandle = handle;
        }

        internal int Id { get; }
    }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationCompletion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ActivateCompleted(nint operation)
        {
            Completed.TrySetResult();
            return 0;
        }
    }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class EndpointWatcher(string watchedId) : IMMNotificationClient, IAgileObject
    {
        internal volatile bool Changed;

        public int OnDeviceStateChanged(string id, uint state)
        {
            if (id == watchedId && state != DeviceActive)
                Changed = true;
            return 0;
        }

        public int OnDeviceRemoved(string id)
        {
            if (id == watchedId)
                Changed = true;
            return 0;
        }

        public int OnDefaultDeviceChanged(int flow, int role, string? id)
        {
            if (flow == Render && role == ConsoleRole && id != watchedId)
                Changed = true;
            return 0;
        }

        public int OnDeviceAdded(string id) => 0;
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }
}
