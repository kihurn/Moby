using PhoneBox.Agent;
using Xunit;

public sealed class MobyCallTests
{
    [Theory]
    [InlineData(false, false, false, null, 0L, "starting", null)]
    [InlineData(true, false, true, null, 19_999L, "live", null)]
    [InlineData(true, false, true, null, 20_000L, "live", "The mic did not reach the cable.")]
    [InlineData(true, false, true, "Playback endpoint missing.", 0L, "live", "Playback endpoint missing.")]
    [InlineData(true, false, false, "Playback endpoint missing.", 20_000L, "live", null)]
    [InlineData(true, true, true, "Playback endpoint missing.", 20_000L, "live", null)]
    public void VideoReadinessIsIndependentOfRequestedMicrophone(bool camera, bool micCapture, bool micRequested,
        string? micProblem, long micWaitMs, string expectedState, string? expectedMicMessage)
    {
        // A muted mic still has live capture but sends no audio to the playback endpoint.
        var state = MobyCall.CaptureState(camera, micAudible: false, micCapture, frames: 12, micRequested, micProblem, micWaitMs);

        Assert.Equal(expectedState, state.State);
        Assert.Equal(camera, state.Camera);
        Assert.False(state.Mic);
        Assert.Equal(micRequested, state.MicRequested);
        Assert.Equal(expectedMicMessage, state.MicMessage);
        Assert.Null(state.Message);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.65, 0.7)]
    [InlineData(3.0, 2.0)]
    [InlineData(-3.0, -2.0)]
    public void CameraExposureIsNormalizedForThePhone(double requested, double expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { mobyCameraExposureEv = requested }));
            var store = new ConfigStore(path);
            Assert.Equal(expected, store.Get().MobyCameraExposureEv);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0, 100_000, false, true)]
    [InlineData(98_001, 100_000, true, false)]
    [InlineData(98_000, 100_000, false, false)]
    [InlineData(40_001, 100_000, false, false)]
    [InlineData(40_000, 100_000, false, true)]
    [InlineData(100_001, 100_000, false, true)]
    public void VideoDemandDistinguishesWakeFromIdleGrace(long requestAt, long now, bool wake, bool sleep)
    {
        Assert.Equal(wake, MobyVideoDemand.ShouldWake(requestAt, now));
        Assert.Equal(sleep, MobyVideoDemand.ShouldSleep(requestAt, now));
    }

    [Theory]
    [InlineData("POST", true)]
    [InlineData("GET", false)]
    public void CallControlIsOnlyAvailableToTheUsbPanel(string method, bool usbAllowed)
    {
        Assert.Equal(usbAllowed, UsbLink.Allowed(method, "/api/moby/call/start"));
        Assert.Equal(usbAllowed, UsbLink.Allowed(method, "/api/moby/call/stop"));
        Assert.False(Lan.AllowedOverLan("/api/moby/call/start"));
        Assert.False(Lan.IsWebhook("/api/moby/call/start"));
        Assert.False(UsbLink.Allowed(method, "/api/moby/call/unrecognized"));
    }
}
