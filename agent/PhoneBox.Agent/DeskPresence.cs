using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhoneBox.Agent;

internal enum DeskReading { Unknown, Absent, Present }
internal enum DeskAction { None, Dim, Standby, Wake }

internal sealed class DeskPresencePolicy
{
    private const long FreshMs = 7_000;
    private bool _seenPresent;
    private long? _sampleAt, _absentSince, _awaySince, _phoneInput;
    private int _absentCount, _presentCount;

    public bool Away => _awaySince.HasValue;
    public bool InStandby { get; private set; }

    public void Observe(DeskReading reading, long now)
    {
        if (_sampleAt is not { } last || now - last > FreshMs) ClearEvidence();
        _sampleAt = now;
        switch (reading)
        {
            case DeskReading.Present:
                ClearAbsence();
                _seenPresent = true;
                _presentCount++;
                break;
            case DeskReading.Absent:
                _presentCount = 0;
                if (!_seenPresent && !Away) break;
                _absentSince ??= now;
                _absentCount++;
                break;
            default:
                ClearEvidence();
                break;
        }
    }

    public void PhoneInput(long now)
    {
        _phoneInput = now;
        ClearSequence();
    }

    public DeskAction Evaluate(long now, long pcIdle, int delay, int standbyDelay, bool cameraWake)
    {
        var pcInput = now - Math.Max(0, pcIdle);
        var lastInput = Math.Max(pcInput, _phoneInput ?? long.MinValue);
        var fresh = _sampleAt is { } sampleAt && now - sampleAt <= FreshMs;
        if (_awaySince is { } awaySince)
        {
            if (lastInput >= awaySince || (cameraWake && fresh && _presentCount >= 2)) return DeskAction.Wake;
            if (!InStandby && fresh && _absentCount >= 3 && _absentSince is { } absentSince
                && now - Math.Max(awaySince, absentSince) >= standbyDelay) return DeskAction.Standby;
            return DeskAction.None;
        }
        if (_absentSince is { } began && lastInput >= began) ClearAbsence();
        return fresh && _absentCount >= 3 && _absentSince is { } since
            && now - since >= delay && now - lastInput >= delay ? DeskAction.Dim : DeskAction.None;
    }

    public void SetAway(bool away, long now)
    {
        _awaySince = away ? now : null;
        InStandby = false;
        if (!away) ClearEvidence();
    }

    public void SetStandby() => InStandby = true;

    public void ClearEvidence()
    {
        _seenPresent = false;
        _sampleAt = null;
        ClearSequence();
    }

    private void ClearSequence() { ClearAbsence(); _presentCount = 0; }
    private void ClearAbsence() { _absentSince = null; _absentCount = 0; }
}

public sealed record DeskPresenceState(bool Enabled, bool Sample, bool Dim, int CameraRelease = 0, bool MonitorStandby = false);

/// <summary>Coordinates Windows display standby with readings from the authenticated Android panel.</summary>
public sealed class DeskPresence(ConfigStore config, AdbReverse adb, MobyCall call, PhoneMic mic, ILogger<DeskPresence> log) : BackgroundService
{
    private readonly object _gate = new();
    private readonly DeskPresencePolicy _policy = new();
    private DeskPresenceState _state = new(false, false, false);
    private string? _phone = null;
    private TaskCompletionSource<bool>? _cameraReleased;
    private int _releaseId;
#if WINDOWS
    private const int SystemRequired = 1;
    private const int DisplayRequired = 0;
    private readonly WindowsDeckNative _monitors = new();
    private readonly Dictionary<string, int> _savedBrightness = [];
    private SafeFileHandle? _stayAwake;
    private bool _keepDisplayOn;
    private long _restoreUntil, _restoreAt;
    private long _retryAt;
#endif

    public DeskPresenceState Snapshot() { lock (_gate) return _state; }

    internal async Task<MobyCallState> StartCall(CancellationToken ct)
    {
        TaskCompletionSource<bool>? release = null;
        lock (_gate)
        {
            if (_cameraReleased is not null) return new("starting", false, false, "Waiting for the phone camera.");
            if (_state.Sample)
            {
                _cameraReleased = release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _state = _state with { Sample = false, CameraRelease = ++_releaseId };
                _policy.ClearEvidence();
            }
        }
        try
        {
            if (release is not null && !await release.Task.WaitAsync(TimeSpan.FromSeconds(5), ct)) return call.Snapshot();
            ct.ThrowIfCancellationRequested();
            return call.Start();
        }
        catch (TimeoutException)
        {
            return call.StartError("The phone did not confirm camera release. Open PhoneBox on the phone and retry.");
        }
        finally
        {
            if (release is not null) lock (_gate)
            {
                _cameraReleased = null;
                _state = _state with { CameraRelease = 0 };
            }
        }
    }

    internal MobyCallState StopCall()
    {
        lock (_gate) _cameraReleased?.TrySetResult(false);
        return call.Stop();
    }

    internal void ConfirmCameraRelease(string phone, int id)
    {
        lock (_gate) if (phone == _phone && id == _releaseId) _cameraReleased?.TrySetResult(true);
    }

    internal void Report(string phone, string sample)
    {
        lock (_gate)
        {
            if (!_state.Enabled || phone != _phone) return;
            var now = Environment.TickCount64;
            if (sample == "input") { _policy.PhoneInput(now); return; }
            if (!_state.Sample) return;
            _policy.Observe(sample switch
            {
                "present" => DeskReading.Present,
                "absent" => DeskReading.Absent,
                _ => DeskReading.Unknown,
            }, now);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
#if WINDOWS
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var options = config.Get().DeskPresence;
                var phone = adb.DeviceList is [var connected] ? connected : null;
                var enabled = options.Mode != "disabled";
                lock (_gate)
                {
                    var callActive = call.OwnsCamera || mic.Enabled;
                    var now = Environment.TickCount64;
                    if (phone != _phone) { _phone = phone; _policy.ClearEvidence(); }
                    if (!enabled || callActive || _cameraReleased is not null)
                    {
                        if (_policy.Away) Wake(now);
                        else _policy.ClearEvidence();
                    }
                    else
                    {
                        var action = _policy.Evaluate(now, (long)(Idle.Seconds() * 1000), options.AbsenceSeconds * 1000,
                            options.StandbySeconds * 1000, options.Mode == "return");
                        if (action == DeskAction.Wake) Wake(now);
                        else if (action == DeskAction.Dim && _savedBrightness.Count == 0) Dim(now);
                        else if (action == DeskAction.Standby && phone is not null && now >= _retryAt) Standby(now);
                    }
                    if (!_policy.Away && _savedBrightness.Count > 0 && now >= _restoreAt) RestoreBrightness(now);
                    _state = new(enabled && !callActive, enabled && !callActive && _cameraReleased is null && phone is not null
                        && (!_policy.InStandby || options.Mode == "return"), _policy.Away,
                        _cameraReleased is null ? 0 : _releaseId, _policy.InStandby);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { lock (_gate) { if (_policy.Away) Wake(Environment.TickCount64); } }
#else
        _ = (config, adb, call, mic, log);
        await Task.CompletedTask;
#endif
    }

#if WINDOWS
    private void Dim(long now)
    {
        _policy.SetAway(true, now);
        _retryAt = 0;
        try
        {
            KeepSystemAwake();
            _keepDisplayOn = PowerSetRequest(_stayAwake!, DisplayRequired);
            foreach (var monitor in _monitors.Monitors(force: true))
            {
                if (monitor.Brightness is not > 1) continue;
                var original = monitor.Brightness.Value;
                var dim = Math.Clamp(original / 4, 1, 15);
                if (_monitors.Brightness(monitor.Id, dim)) _savedBrightness[monitor.Id] = original;
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Desk presence could not dim the Windows displays"); }
    }

    private void Standby(long now)
    {
        _retryAt = now + 30_000;
        try
        {
            KeepSystemAwake();
            if (_keepDisplayOn && !PowerClearRequest(_stayAwake!, DisplayRequired))
                throw new InvalidOperationException("Windows did not release the display stay-awake request.");
            _keepDisplayOn = false;
            Shortcuts.SetMonitorPower(2);
            _policy.SetStandby();
            log.LogInformation("Desk presence put the displays into standby");
        }
        catch (Exception ex)
        {
            ReleaseStayAwake();
            log.LogWarning(ex, "Desk presence could not enter display standby");
        }
    }

    private void Wake(long now)
    {
        try { if (_policy.InStandby) Shortcuts.SetMonitorPower(-1); }
        catch (Exception ex) { log.LogWarning(ex, "Desk presence could not request display wake"); }
        finally
        {
            ReleaseStayAwake();
            _policy.SetAway(false, now);
            _restoreUntil = now + 10_000;
            RestoreBrightness(now);
        }
    }

    private void RestoreBrightness(long now)
    {
        _restoreAt = now + 1_000;
        foreach (var (id, original) in _savedBrightness.ToArray())
        {
            try { if (_monitors.Brightness(id, original)) { _savedBrightness.Remove(id); continue; } }
            catch (Exception ex) { log.LogDebug(ex, "Desk presence could not restore monitor {Monitor}", id); }
            if (now < _restoreUntil) continue;
            _savedBrightness.Remove(id);
            log.LogWarning("Desk presence could not restore monitor {Monitor} to brightness {Brightness}", id, original);
        }
    }

    private void KeepSystemAwake()
    {
        if (_stayAwake is not null) return;
        var reason = Marshal.StringToHGlobalUni("Moby desk presence");
        try { _stayAwake = PowerCreateRequest(new PowerReason { Flags = 1, Reason = reason }); }
        finally { Marshal.FreeHGlobal(reason); }
        if (!_stayAwake.IsInvalid && PowerSetRequest(_stayAwake, SystemRequired)) return;
        ReleaseStayAwake();
        throw new InvalidOperationException("Windows did not grant the system stay-awake request.");
    }

    private void ReleaseStayAwake()
    {
        if (_stayAwake is null) return;
        if (!_stayAwake.IsInvalid)
        {
            if (_keepDisplayOn) PowerClearRequest(_stayAwake, DisplayRequired);
            PowerClearRequest(_stayAwake, SystemRequired);
        }
        _keepDisplayOn = false;
        _stayAwake.Dispose();
        _stayAwake = null;
    }

    // REASON_CONTEXT with the simple-string union member. The unused fields reserve space for the detailed member.
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerReason
    {
        public uint Version, Flags;
        public nint Reason;
        public uint ReservedId, ReservedCount;
        public nint ReservedStrings;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle PowerCreateRequest(in PowerReason context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafeFileHandle handle, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(SafeFileHandle handle, int requestType);
#endif
}
