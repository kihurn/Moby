using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

public sealed record MobyCallState(string State, bool Camera, bool Mic, string? Message, ulong Frames = 0);

/// <summary>Owns the bridge processes and the transient microphone request independently of the panel page.</summary>
public sealed class MobyCall(ConfigStore config, AdbReverse adb, PhoneMic mic, ILogger<MobyCall> log) : BackgroundService
{
    private readonly object _gate = new();
    private Process? _camera;
    private string? _serial;
    private long _started, _lastFrameAt;
    private ulong _lastFrame;
    private MobyCallState _state = new("off", false, false, null);

    public MobyCallState Snapshot() { lock (_gate) return _state; }
    internal bool OwnsCamera { get { lock (_gate) return _camera is not null; } }

    public MobyCallState Start()
    {
        lock (_gate)
        {
            if (_camera is not null || mic.OwnedByCall) return _state;
            var cfg = config.Get();
            if (!OperatingSystem.IsWindows()) return Fail("Call capture needs Windows.");
            if (cfg.PhoneMic.Enabled) return Fail("Turn off the standalone phone mic in General before starting a Call session.");
            if (adb.DeviceList is not [var serial]) return Fail("Connect one phone over USB or select it in Settings.");
            if (cfg.MobyCameraPath is not { } exe || !Path.IsPathFullyQualified(exe) || !File.Exists(exe)
                || !string.Equals(Path.GetFileName(exe), "Plugcam.exe", StringComparison.OrdinalIgnoreCase))
                return Fail("Set up the portable Plugcam camera on the PC.");

            var directory = Path.GetDirectoryName(exe)!;
            var bundledAdb = Path.Combine(directory, "resources", "adb.exe");
            var sharedAdb = AdbReverse.FindAdb(cfg.AdbPath);
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
                if (string.IsNullOrWhiteSpace(cfg.PhoneMic.Endpoint)
                    || !PhoneDeviceSetup.PlaybackDevices().Any(name => name.Equals(cfg.PhoneMic.Endpoint, StringComparison.OrdinalIgnoreCase)))
                    return Fail("Start Virtual Cables and set the phone mic playback endpoint to the cable's full name.");
                if (!File.Exists(Path.Combine(directory, "portable.txt"))) return Fail("Use the portable Plugcam package.");
                PrepareCamera(directory, serial);
                var start = new ProcessStartInfo(exe) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--minimized");
                _camera = Process.Start(start) ?? throw new IOException("Plugcam did not launch.");
                JobObject.Track(_camera);
                _serial = serial;
                _started = Environment.TickCount64;
                _lastFrameAt = 0;
                _lastFrame = 0;
                mic.PrepareCall(cfg.PhoneMic, serial, bundledAdb);
                return _state = new("starting", false, false, null);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to start Moby camera");
                return StopLocked("Could not start Plugcam. Check the portable installation on the PC.");
            }
        }
    }

    public MobyCallState Stop() { lock (_gate) return StopLocked(); }
    internal MobyCallState StartError(string error) { lock (_gate) return Fail(error); }

    private MobyCallState Fail(string message) => _state = new("error", false, false, message);

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
                    var now = Environment.TickCount64;
                    var frame = CameraFrame();
                    if (frame != 0 && frame != _lastFrame) { _lastFrame = frame; _lastFrameAt = now; }
                    bool camera = _lastFrameAt != 0 && now - _lastFrameAt < 2_000;
                    if (camera) mic.RequestForCall(true);
                    bool audio = mic.Live;
                    if (camera && audio) _started = now;
                    if (now - _started > 20_000)
                    {
                        StopLocked(audio ? "Plugcam did not deliver frames. Check the USB camera." : mic.CaptureProblem ?? "The mic did not reach the cable.");
                        continue;
                    }
                    _state = new(camera && audio ? "live" : "starting", camera, audio, null, _lastFrame);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { Stop(); }
    }

    private static ulong CameraFrame()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try
        {
            // Plugcam 0.2.2 Softcam Header: active byte at 12 and frame counter at 16. Read no image data.
            using var mapping = MemoryMappedFile.OpenExisting("Plugcam Camera/SharedMemory", MemoryMappedFileRights.Read);
            using var header = mapping.CreateViewAccessor(0, 24, MemoryMappedFileAccess.Read);
            return header.ReadByte(12) != 0 ? header.ReadUInt64(16) : 0;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
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
