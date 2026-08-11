using System.Diagnostics;
using System.Runtime.InteropServices;
using ProjectTimeTracker.Core;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Control;
using Windows.Media.Devices;
using Windows.Media.Render;

namespace ProjectTimeTracker.Windows.Services;

public sealed class IdleProtectionMonitor(
    IForegroundActivityMonitor foregroundMonitor) : IIdleProtectionMonitor
{
    private static readonly Guid AudioSessionManager2Id =
        new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private const uint DeviceStateActive = 0x00000001;
    private const uint ClsContextAll = 23;
    private const int RpcChangedMode = unchecked((int)0x80010106);

    private readonly ForegroundAudioQualificationPolicy _foregroundPolicy = new();
    private readonly object _stateGate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private Task? _mediaInitialization;
    private AudioStateMonitor? _communicationsDuckingMonitor;
    private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
    private bool _callsEnabled = true;
    private bool _videoEnabled = true;
    private bool _started;
    private bool _disposed;

    public event EventHandler<IdleProtectionState>? StateChanged;

    public IdleProtectionState CurrentState { get; private set; } =
        IdleProtectionState.NotStarted;

    public void Configure(bool callsEnabled, bool videoEnabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateGate)
        {
            _callsEnabled = callsEnabled;
            _videoEnabled = videoEnabled;
            if (!callsEnabled)
            {
                _foregroundPolicy.Reset();
            }

            var allowedReasons = IdleProtectionReason.None;
            if (callsEnabled)
            {
                allowedReasons |= IdleProtectionReason.CommunicationAudio |
                                  IdleProtectionReason.ForegroundAudio;
            }

            if (videoEnabled)
            {
                allowedReasons |= IdleProtectionReason.VideoPlayback;
            }

            var filtered = CurrentState with
            {
                ActiveReasons = CurrentState.ActiveReasons & allowedReasons,
                ObservedUtc = DateTimeOffset.UtcNow,
            };
            PublishStateCore(filtered);
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _cancellation = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cancellation.Token));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _communicationsDuckingMonitor = null;
        _mediaManager = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        InitializeWindowsMonitors();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                Poll();
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            PublishState(new IdleProtectionState(
                IdleProtectionReason.None,
                CallsAvailable: false,
                VideoAvailable: false,
                IsInitialized: true,
                DateTimeOffset.UtcNow));
        }
    }

    private void InitializeWindowsMonitors()
    {
        try
        {
            // AudioStateMonitor reports whether monitored streams are currently
            // full, ducked, or muted; Full does not mean that a stream exists.
            // Watching ordinary media reveals Windows communications ducking
            // without touching capture devices. Calls in apps that bypass
            // system ducking are covered by the sustained foreground fallback.
            _communicationsDuckingMonitor = AudioStateMonitor.CreateForRenderMonitoring(
                AudioRenderCategory.Media,
                AudioDeviceRole.Default);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }

        // Requesting the optional media-session manager can be slow on some
        // Windows installations. Do not delay render-audio protection or its
        // availability status while GSMTC initializes.
        _mediaInitialization = InitializeMediaManagerAsync();
    }

    private async Task InitializeMediaManagerAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (_stateGate)
            {
                if (!_disposed)
                {
                    _mediaManager = manager;
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private void Poll()
    {
        bool callsEnabled;
        bool videoEnabled;
        lock (_stateGate)
        {
            callsEnabled = _callsEnabled;
            videoEnabled = _videoEnabled;
        }

        var media = ObserveMediaPlayback();
        var foregroundProcess = NormalizeProcessName(
            foregroundMonitor.GetCurrentActivity()?.ProcessName);
        var render = ObserveRenderSessions(
            foregroundProcess,
            media.ExplicitMusicOrImageSources);
        var communicationAudio = callsEnabled &&
            IsCommunicationRenderActive();
        var foregroundAudio = callsEnabled && _foregroundPolicy.Observe(
            foregroundProcess,
            render.ForegroundProcessActive,
            media.IsExplicitMusicOrImageFor(foregroundProcess),
            Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

        var reasons = IdleProtectionReason.None;
        if (communicationAudio)
        {
            reasons |= IdleProtectionReason.CommunicationAudio;
        }

        if (foregroundAudio)
        {
            reasons |= IdleProtectionReason.ForegroundAudio;
        }

        if (videoEnabled && media.VideoPlaying)
        {
            reasons |= IdleProtectionReason.VideoPlayback;
        }

        PublishState(new IdleProtectionState(
            reasons,
            CallsAvailable: _communicationsDuckingMonitor is not null || render.QuerySucceeded,
            VideoAvailable: _mediaManager is not null,
            IsInitialized: true,
            DateTimeOffset.UtcNow));
    }

    private bool IsCommunicationRenderActive()
    {
        try
        {
            return _communicationsDuckingMonitor?.SoundLevel is
                SoundLevel.Low or SoundLevel.Muted;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            _communicationsDuckingMonitor = null;
            return false;
        }
    }

    private MediaObservation ObserveMediaPlayback()
    {
        if (_mediaManager is null)
        {
            return MediaObservation.Unavailable;
        }

        try
        {
            var videoPlaying = false;
            var ignoredSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var session in _mediaManager.GetSessions())
            {
                var playback = session.GetPlaybackInfo();
                if (playback.PlaybackStatus !=
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    continue;
                }

                if (playback.PlaybackType is MediaPlaybackType.Music or MediaPlaybackType.Image)
                {
                    var source = NormalizeIdentity(session.SourceAppUserModelId);
                    if (source.Length > 0)
                    {
                        ignoredSources.Add(source);
                    }

                    continue;
                }

                videoPlaying = true;
            }

            return new MediaObservation(videoPlaying, ignoredSources);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            _mediaManager = null;
            return MediaObservation.Unavailable;
        }
    }

    private static RenderObservation ObserveRenderSessions(
        string foregroundProcess,
        IReadOnlySet<string> explicitMusicOrImageSources)
    {
        var initialized = CoInitializeEx(IntPtr.Zero, 0);
        var shouldUninitialize = initialized >= 0;
        if (initialized < 0 && initialized != RpcChangedMode)
        {
            return RenderObservation.Unavailable;
        }

        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            if (enumerator.EnumAudioEndpoints(
                    EDataFlow.Render,
                    DeviceStateActive,
                    out devices) < 0 ||
                devices.GetCount(out var deviceCount) < 0)
            {
                return RenderObservation.Unavailable;
            }

            var foregroundActive = false;
            for (uint deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
            {
                IMMDevice? device = null;
                try
                {
                    if (devices.Item(deviceIndex, out device) < 0)
                    {
                        continue;
                    }

                    ObserveDeviceSessions(
                        device,
                        foregroundProcess,
                        explicitMusicOrImageSources,
                        ref foregroundActive);
                }
                finally
                {
                    ReleaseCom(device);
                }
            }

            return new RenderObservation(
                foregroundActive,
                QuerySucceeded: true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            return RenderObservation.Unavailable;
        }
        finally
        {
            ReleaseCom(devices);
            ReleaseCom(enumerator);
            if (shouldUninitialize)
            {
                CoUninitialize();
            }
        }
    }

    private static void ObserveDeviceSessions(
        IMMDevice device,
        string foregroundProcess,
        IReadOnlySet<string> explicitMusicOrImageSources,
        ref bool foregroundActive)
    {
        object? managerObject = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            var interfaceId = AudioSessionManager2Id;
            if (device.Activate(
                    ref interfaceId,
                    ClsContextAll,
                    IntPtr.Zero,
                    out managerObject) < 0 ||
                managerObject is not IAudioSessionManager2 manager ||
                manager.GetSessionEnumerator(out sessions) < 0 ||
                sessions.GetCount(out var sessionCount) < 0)
            {
                return;
            }

            for (var index = 0; index < sessionCount; index++)
            {
                IAudioSessionControl? session = null;
                try
                {
                    if (sessions.GetSession(index, out session) < 0 ||
                        session.GetState(out var state) < 0 ||
                        state != AudioSessionState.Active ||
                        session is not IAudioSessionControl2 details ||
                        details.IsSystemSoundsSession() == 0 ||
                        details.GetProcessId(out var processId) < 0)
                    {
                        continue;
                    }

                    var processName = GetProcessName(processId);
                    if (processName.Length == 0 ||
                        IsExplicitMusicOrImage(processName, explicitMusicOrImageSources))
                    {
                        continue;
                    }

                    if (string.Equals(
                            processName,
                            foregroundProcess,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        foregroundActive = true;
                    }
                }
                finally
                {
                    ReleaseCom(session);
                }
            }
        }
        finally
        {
            ReleaseCom(sessions);
            ReleaseCom(managerObject);
        }
    }

    private void PublishState(IdleProtectionState state)
    {
        lock (_stateGate)
        {
            PublishStateCore(state);
        }
    }

    private void PublishStateCore(IdleProtectionState state)
    {
        var previous = CurrentState;
        CurrentState = state;
        if (previous.ActiveReasons == state.ActiveReasons &&
            previous.CallsAvailable == state.CallsAvailable &&
            previous.VideoAvailable == state.VideoAvailable &&
            previous.IsInitialized == state.IsInitialized)
        {
            return;
        }

        StateChanged?.Invoke(this, state);
    }

    private static string GetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return NormalizeProcessName(process.ProcessName);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string NormalizeProcessName(string? processName) =>
        string.IsNullOrWhiteSpace(processName)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(processName.Trim()).ToLowerInvariant();

    private static string NormalizeIdentity(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());

    private static bool IsExplicitMusicOrImage(
        string normalizedProcessName,
        IEnumerable<string> sources)
    {
        var processIdentity = NormalizeIdentity(normalizedProcessName);
        return processIdentity.Length > 0 && sources.Any(source =>
            source.Contains(processIdentity, StringComparison.OrdinalIgnoreCase));
    }

    private static void ReleaseCom(object? value)
    {
        try
        {
            if (value is not null && Marshal.IsComObject(value))
            {
                _ = Marshal.ReleaseComObject(value);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrencyModel);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private sealed record MediaObservation(
        bool VideoPlaying,
        IReadOnlySet<string> ExplicitMusicOrImageSources)
    {
        public static MediaObservation Unavailable { get; } = new(
            VideoPlaying: false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public bool IsExplicitMusicOrImageFor(string normalizedProcessName) =>
            IdleProtectionMonitor.IsExplicitMusicOrImage(
                normalizedProcessName,
                ExplicitMusicOrImageSources);
    }

    private sealed record RenderObservation(
        bool ForegroundProcessActive,
        bool QuerySucceeded)
    {
        public static RenderObservation Unavailable { get; } = new(
            ForegroundProcessActive: false,
            QuerySucceeded: false);
    }

    private enum EDataFlow
    {
        Render,
    }

    private enum AudioSessionState
    {
        Inactive,
        Active,
        Expired,
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumeratorComObject;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, int role, out IMMDevice device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-C0A9942782D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            uint classContext,
            IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [PreserveSig]
        int OpenPropertyStore(uint access, out IntPtr properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(ref Guid sessionId, uint streamFlags, out IntPtr sessionControl);

        [PreserveSig]
        int GetSimpleAudioVolume(ref Guid sessionId, uint streamFlags, out IntPtr audioVolume);

        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);

        [PreserveSig]
        int RegisterSessionNotification(IntPtr notification);

        [PreserveSig]
        int UnregisterSessionNotification(IntPtr notification);

        [PreserveSig]
        int RegisterDuckNotification(IntPtr sessionId, IntPtr notification);

        [PreserveSig]
        int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int sessionCount);

        [PreserveSig]
        int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig]
        int GetState(out AudioSessionState state);

        [PreserveSig]
        int GetDisplayName(out IntPtr displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        int GetIconPath(out IntPtr iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingId);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr client);

        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2 : IAudioSessionControl
    {
        [PreserveSig]
        new int GetState(out AudioSessionState state);

        [PreserveSig]
        new int GetDisplayName(out IntPtr displayName);

        [PreserveSig]
        new int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        new int GetIconPath(out IntPtr iconPath);

        [PreserveSig]
        new int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        new int GetGroupingParam(out Guid groupingId);

        [PreserveSig]
        new int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

        [PreserveSig]
        new int RegisterAudioSessionNotification(IntPtr client);

        [PreserveSig]
        new int UnregisterAudioSessionNotification(IntPtr client);

        [PreserveSig]
        int GetSessionIdentifier(out IntPtr sessionIdentifier);

        [PreserveSig]
        int GetSessionInstanceIdentifier(out IntPtr sessionInstanceIdentifier);

        [PreserveSig]
        int GetProcessId(out uint processId);

        [PreserveSig]
        int IsSystemSoundsSession();

        [PreserveSig]
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }
}
