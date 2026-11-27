using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

public sealed record MobyCallState(string State, bool Camera, bool Mic, string? Message, ulong Frames = 0,
    bool MicRequested = false, bool Automatic = false, string? MicMessage = null);

/// <summary>Owns the bridge processes and the transient microphone request independently of the panel page.</summary>
public sealed class MobyCall(ConfigStore config, AdbReverse adb, PhoneMic mic, ILogger<MobyCall> log) : BackgroundService
{
    private readonly object _gate = new();
    private Process? _camera;
    private string? _serial;
    private long _started, _lastFrameAt;
    private ulong _lastFrame;
    private bool _withMic;
    private long _micWaitingSince;
    private string? _micStartProblem;
    private MobyVideoDemand? _videoDemand;
    private bool _automaticArmed = true;
    private bool _automaticCamera;
    private long _automaticRetryAt;
    private MobyCallState _state = new("off", false, false, null);

    private bool IsAutomatic => OperatingSystem.IsWindows() && config.Get().MobyOnDemandCameraDll is { Length: > 0 };

    public MobyCallState Snapshot()
    {
        lock (_gate)
        {
            var state = _state with { MicRequested = _withMic, Automatic = IsAutomatic && _automaticArmed };
            if (state.Automatic && state.State == "off") state = state with { State = "standby" };
            return _camera is null && _lastFrameAt != 0 && Environment.TickCount64 - _lastFrameAt < 2_000
                ? state with { Camera = true, Frames = _lastFrame }
                : state;
        }
    }
    internal bool OwnsCamera { get { lock (_gate) return _camera is not null; } }

    internal MobyCallState? ArmAutomatic()
    {
        lock (_gate)
        {
            if (!IsAutomatic) return null;
            _automaticArmed = true;
            _automaticRetryAt = 0;
            if (_camera is null && _state.State == "error") _state = new("off", false, false, null);
            return Snapshot();
        }
    }

    internal bool PollAutomatic(bool usbConnected)
    {
        if (!OperatingSystem.IsWindows()) return false;
        lock (_gate)
        {
            if (!_automaticArmed || !IsAutomatic) return false;
            var now = Environment.TickCount64;
            if (now < _automaticRetryAt) return false;
            long requestedAt;
            try
            {
                _videoDemand ??= new();
                requestedAt = _videoDemand.Read();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _automaticRetryAt = now + 10_000;
                Fail("The on-demand camera could not read its Windows stream request.");
                log.LogWarning(ex, "Could not read Moby camera demand");
                return false;
            }
            if (_camera is not null)
            {
                if (_automaticCamera && MobyVideoDemand.ShouldSleep(requestedAt, now)) StopLocked();
                return false;
            }
            if (!usbConnected || !MobyVideoDemand.ShouldWake(requestedAt, now)) return false;
            _automaticRetryAt = now + 10_000;
            return true;
        }
    }

    public MobyCallState Start(bool withMic = true, bool automatic = false)
    {
        lock (_gate)
        {
            var cfg = config.Get();
            if (!OperatingSystem.IsWindows()) return Fail("Call capture needs Windows.");
            if (automatic && (!_automaticArmed || !IsAutomatic)) return Snapshot();
            if (_camera is not null)
            {
                if (!withMic || (_withMic && _micStartProblem is null)) return Snapshot();
                _withMic = true;
                _micWaitingSince = 0;
                _micStartProblem = PrepareRequestedMic(cfg.PhoneMic, _serial, AdbReverse.FindAdb(cfg.AdbPath));
                _state = _state with { MicMessage = _micStartProblem };
                return Snapshot();
            }
            if (mic.OwnedByCall) return _state;
            _withMic = withMic;
            _micWaitingSince = 0;
            _micStartProblem = null;
            if (adb.DeviceList is not [var serial]) return Fail("Connect one phone over USB or select it in Settings.");
            if (cfg.MobyCameraPath is not { } exe || !Path.IsPathFullyQualified(exe) || !File.Exists(exe)
                || !string.Equals(Path.GetFileName(exe), "Plugcam.exe", StringComparison.OrdinalIgnoreCase))
                return Fail("Set up the portable Plugcam camera on the PC.");

            var directory = Path.GetDirectoryName(exe)!;
            var bundledAdb = Path.Combine(directory, "resources", "adb.exe");
            var sharedAdb = AdbReverse.FindAdb(cfg.AdbPath);
            var onDemandDll = cfg.MobyOnDemandCameraDll;
            bool supportsExposure = File.Exists(Path.Combine(directory, "moby-exposure.txt"));
            if (cfg.MobyCameraExposureEv != 0 && !supportsExposure)
                return Fail("Exposure compensation needs the Moby exposure build of Plugcam. Select it in Settings.");
            if (supportsExposure && sharedAdb is not null) bundledAdb = sharedAdb;
            if (onDemandDll is { Length: > 0 })
            {
                if (!Path.IsPathFullyQualified(onDemandDll) || !File.Exists(onDemandDll))
                    return Fail("The on-demand Plugcam DLL is unavailable.");
                if (sharedAdb is not null) bundledAdb = sharedAdb;
            }
            if (!File.Exists(bundledAdb) || !string.Equals(bundledAdb, sharedAdb, StringComparison.OrdinalIgnoreCase))
                return Fail("Set PhoneBox's ADB path to the adb.exe bundled with Plugcam.");
            if (Process.GetProcessesByName("Plugcam") is { Length: > 0 } running)
            {
                foreach (var process in running) process.Dispose();
                return Fail("Exit the manually opened Plugcam app from the Windows tray, then start capture here.");
            }
            try
            {
                using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var usbSerial = AdbReverse.Adb(bundledAdb, "-d get-serialno", probe.Token, anyExit: true).GetAwaiter().GetResult().Trim();
                if (usbSerial != serial) return Fail("Connect the selected phone over USB and disconnect other USB phones.");
                if (!File.Exists(Path.Combine(directory, "portable.txt"))) return Fail("Use the portable Plugcam package.");
                if (!IsAutomatic && CameraFrame() == 0)
                    return Fail("A Windows app is holding an inactive Plugcam Camera. Close its camera preview or exit the app, then retry.");
                PrepareCamera(directory, serial);
                var start = new ProcessStartInfo(exe) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
                if (supportsExposure)
                {
                    start.Environment["PLUGCAM_ADB"] = bundledAdb;
                    start.Environment["MOBY_CAMERA_EXPOSURE_EV"] = cfg.MobyCameraExposureEv.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                }
                if (onDemandDll is { Length: > 0 })
                {
                    start.Environment["PLUGCAM_VCAM_DLL"] = onDemandDll;
                    start.Environment["PLUGCAM_ADB"] = bundledAdb;
                }
                start.ArgumentList.Add("--minimized");
                _camera = Process.Start(start) ?? throw new IOException("Plugcam did not launch.");
                _automaticCamera = automatic;
                JobObject.Track(_camera);
                _serial = serial;
                _started = Environment.TickCount64;
                _lastFrameAt = 0;
                _lastFrame = 0;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to start Moby camera");
                return StopLocked("Could not start Plugcam. Check the portable installation on the PC.");
            }
            if (withMic) _micStartProblem = PrepareRequestedMic(cfg.PhoneMic, serial, bundledAdb);
            _state = new("starting", false, false, null, MicMessage: _micStartProblem);
            return Snapshot();
        }
    }

    public MobyCallState Stop()
    {
        lock (_gate)
        {
            _automaticArmed = false;
            return StopLocked();
        }
    }
    internal MobyCallState StartError(string error, bool withMic)
    {
        lock (_gate)
        {
            _withMic = withMic;
            return Fail(error);
        }
    }

    private MobyCallState Fail(string message) => _state = new("error", false, false, message);
    private const string MissingMicOutput = "Start Virtual Cables and set the phone mic playback endpoint to the cable's full name.";

    private string? PrepareRequestedMic(PhoneMicConfig cfg, string? serial, string? adbPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cfg.Endpoint)
                || !PhoneDeviceSetup.PlaybackDevices().Contains(cfg.Endpoint, StringComparer.OrdinalIgnoreCase))
                return MissingMicOutput;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            log.LogWarning(ex, "Could not list phone mic playback endpoints");
            return "The mic playback endpoint could not be checked. Check the Windows audio device.";
        }
        if (cfg.Enabled) return null;
        if (serial is null || adbPath is null) return "The shared USB connection is unavailable for the mic.";
        mic.PrepareCall(cfg, serial, adbPath);
        return null;
    }

    internal static MobyCallState CaptureState(bool camera, bool micAudible, bool micCapture, ulong frames,
        bool micRequested, string? micProblem, long micWaitMs)
    {
        string? micMessage = micRequested && !micCapture
            ? micProblem ?? (camera && micWaitMs >= 20_000 ? "The mic did not reach the cable." : null)
            : null;
        return new(camera ? "live" : "starting", camera, micAudible, null, frames, micRequested, MicMessage: micMessage);
    }

    private MobyCallState StopLocked(string? error = null)
    {
        mic.RequestForCall(false);
        if (_camera is not null)
        {
            try
            {
                if (!_camera.HasExited) _camera.Kill();
                if (!_camera.WaitForExit(1500)) return Fail("Plugcam is still stopping. Press Stop again.");
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not stop Plugcam");
                return Fail("Could not stop Plugcam. Press Stop again.");
            }
            _camera.Dispose();
            _camera = null;
        }
        _serial = null;
        _automaticCamera = false;
        _lastFrameAt = 0;
        _lastFrame = 0;
        _micWaitingSince = 0;
        _micStartProblem = null;
        bool released = !mic.OwnedByCall || mic.Released;
        if (released) mic.FinishCall();
        return error is null ? _state = new(released ? "off" : "stopping", false, false, null) : Fail(error);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                lock (_gate)
                {
                    var now = Environment.TickCount64;
                    var frame = CameraFrame();
                    if (frame is > 0 && frame != _lastFrame)
                    {
                        if (!IsAutomatic || _lastFrame != 0) _lastFrameAt = now;
                        _lastFrame = frame.Value;
                    }
                    if (frame is null or 0) { _lastFrame = 0; _lastFrameAt = 0; }
                    if (_camera is null)
                    {
                        if (mic.OwnedByCall && mic.Released) mic.FinishCall();
                        if (_state.State == "stopping" && mic.Released) _state = new("off", false, false, null);
                        continue;
                    }
                    if (_camera.HasExited) { StopLocked("Plugcam closed. Start capture again."); continue; }
                    if (adb.DeviceList is not [var serial] || serial != _serial)
                    {
                        StopLocked("USB disconnected. Reconnect the phone and start capture again.");
                        continue;
                    }
                    bool camera = _lastFrameAt != 0 && now - _lastFrameAt < 2_000;
                    if (camera)
                    {
                        mic.RequestForCall(true);
                        _started = now;
                    }
                    else if (now - _started > 20_000)
                    {
                        StopLocked("Plugcam did not deliver frames. Check the USB camera.");
                        continue;
                    }
                    bool micCapture = mic.CaptureLive;
                    if (!_withMic || !camera || micCapture) _micWaitingSince = 0;
                    else if (_micWaitingSince == 0) _micWaitingSince = now;
                    _state = CaptureState(camera, micCapture && !mic.Muted, micCapture, _lastFrame, _withMic,
                        _micStartProblem ?? mic.CaptureProblem, _micWaitingSince == 0 ? 0 : now - _micWaitingSince);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            Stop();
            _videoDemand?.Dispose();
        }
    }

    private ulong? CameraFrame()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            // Plugcam 0.2.2 Softcam Header: active byte at 12 and frame counter at 16. Read no image data.
            string name = IsAutomatic ? MobyVideoDemand.FrameMapping : "Plugcam Camera/SharedMemory";
            using var mapping = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var header = mapping.CreateViewAccessor(0, 24, MemoryMappedFileAccess.Read);
            return header.ReadByte(12) != 0 ? header.ReadUInt64(16) : 0;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void PrepareCamera(string directory, string serial)
    {
        var data = Directory.CreateDirectory(Path.Combine(directory, "data"));
        var settings = Path.Combine(data.FullName, "settings.json");
        var profile = File.Exists(settings) ? JsonNode.Parse(File.ReadAllText(settings))?.AsObject() ?? new() : new JsonObject();
        profile["onboardingDone"] = true;
        profile["phone"] = serial;
        profile["facing"] = "front";
        profile["cameraId"] = null;
        profile["fps"] = 30;
        profile["vcamWidth"] = 1280;
        profile["vcamHeight"] = 720;
        profile["bitrateMbps"] = 2;
        profile["autoStartCamera"] = true;
        profile["checkUpdates"] = false;
        profile["launchAtLogin"] = false;
        File.WriteAllText(settings + ".moby-tmp", profile.ToJsonString());
        File.Move(settings + ".moby-tmp", settings, overwrite: true);
    }
}
