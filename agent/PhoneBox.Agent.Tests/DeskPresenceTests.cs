using PhoneBox.Agent;
using Xunit;

public sealed class DeskPresenceTests
{
    private const int Delay = 10_000;

    [Fact]
    public void ContinuedAbsenceDimsFirstAndThenAllowsStandby()
    {
        var desk = new DeskPresencePolicy();
        desk.Observe(DeskReading.Present, 1_000);
        desk.Observe(DeskReading.Absent, 5_000);
        desk.Observe(DeskReading.Absent, 10_000);
        desk.Observe(DeskReading.Absent, 15_000);
        Assert.Equal(DeskAction.Dim, desk.Evaluate(15_000, 20_000, Delay, Delay, true));
        desk.SetAway(true, 15_000);
        desk.Observe(DeskReading.Absent, 20_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(24_999, 30_000, Delay, Delay, true));
        desk.Observe(DeskReading.Absent, 25_000);
        Assert.Equal(DeskAction.Standby, desk.Evaluate(25_000, 30_000, Delay, Delay, true));
        desk.SetStandby();
        Assert.Equal(DeskAction.None, desk.Evaluate(25_500, 30_000, Delay, Delay, true));
    }

    [Fact]
    public void UnknownWhileDimmedRestartsTheStandbyDelay()
    {
        var desk = AwayDesk();
        desk.Observe(DeskReading.Unknown, 19_000);
        desk.Observe(DeskReading.Absent, 24_000);
        desk.Observe(DeskReading.Absent, 28_000);
        desk.Observe(DeskReading.Absent, 32_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(33_000, 38_000, Delay, Delay, false));
        Assert.Equal(DeskAction.Standby, desk.Evaluate(34_000, 39_000, Delay, Delay, false));
    }

    [Fact]
    public void AbsenceNeedsAPositiveBaselineFreshSamplesAndInputIdle()
    {
        var desk = new DeskPresencePolicy();
        desk.Observe(DeskReading.Absent, 1_000);
        desk.Observe(DeskReading.Absent, 6_000);
        desk.Observe(DeskReading.Absent, 11_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(11_000, 20_000, Delay, Delay, false));

        desk.Observe(DeskReading.Present, 13_000);
        desk.Observe(DeskReading.Absent, 15_000);
        desk.Observe(DeskReading.Absent, 20_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(24_000, 20_000, Delay, Delay, false));
        desk.Observe(DeskReading.Absent, 25_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(25_000, 50, Delay, Delay, false));
        desk.Observe(DeskReading.Absent, 30_000);
        desk.Observe(DeskReading.Absent, 35_000);
        desk.Observe(DeskReading.Absent, 40_000);
        Assert.Equal(DeskAction.Dim, desk.Evaluate(40_000, 11_000, Delay, Delay, false));
    }

    [Fact]
    public void UnknownBreaksTheTimerWithoutForgettingTheConfirmedDeskBaseline()
    {
        var desk = new DeskPresencePolicy();
        desk.Observe(DeskReading.Present, 1_000);
        desk.Observe(DeskReading.Absent, 4_000);
        desk.Observe(DeskReading.Unknown, 7_000);
        desk.Observe(DeskReading.Absent, 10_000);
        desk.Observe(DeskReading.Absent, 15_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(19_999, 30_000, Delay, 5_000, true));
        desk.Observe(DeskReading.Absent, 20_000);
        Assert.Equal(DeskAction.Dim, desk.Evaluate(20_000, 30_000, Delay, 5_000, true));
    }

    [Fact]
    public void UnknownAndStaleFramesBreakTheAbsenceSequence()
    {
        var desk = new DeskPresencePolicy();
        desk.Observe(DeskReading.Present, 1_000);
        desk.Observe(DeskReading.Absent, 3_000);
        desk.Observe(DeskReading.Absent, 7_000);
        desk.Observe(DeskReading.Unknown, 10_000);
        desk.Observe(DeskReading.Absent, 12_000);
        desk.Observe(DeskReading.Absent, 17_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(25_000, 25_000, Delay, Delay, true));
        desk.Observe(DeskReading.Absent, 26_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(26_000, 26_000, Delay, Delay, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyReturnModeRestoresDimmedMonitorOnFirstPresentFrame(bool returnMode)
    {
        var desk = AwayDesk();
        desk.Observe(DeskReading.Present, 16_000);
        Assert.Equal(returnMode ? DeskAction.Wake : DeskAction.None, desk.Evaluate(16_000, 21_000, Delay, Delay, returnMode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StandbyRequestsDisplayWakeOnlyOnConfirmedReturnInReturnMode(bool returnMode)
    {
        var desk = AwayDesk();
        desk.SetStandby();
        Assert.True(desk.PhoneDimmed);
        desk.PhoneInput(17_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(17_000, 22_000, Delay, Delay, returnMode));
        Assert.False(desk.PhoneDimmed);
        desk.Observe(DeskReading.Unknown, 18_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(18_000, 23_000, Delay, Delay, returnMode));
        desk.Observe(DeskReading.Present, 19_000);
        Assert.Equal(returnMode ? DeskAction.WakeDisplay : DeskAction.None,
            desk.Evaluate(19_000, 24_000, Delay, Delay, returnMode));
        Assert.True(desk.InStandby);
        Assert.Equal(DeskAction.Wake, desk.Evaluate(21_000, 500, Delay, Delay, returnMode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MouseOrKeyboardWakesInDarknessEvenIfInputWasSeveralSecondsAgo(bool returnMode)
    {
        var desk = AwayDesk();
        desk.Observe(DeskReading.Unknown, 16_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(16_000, 21_000, Delay, Delay, returnMode));
        Assert.Equal(DeskAction.Wake, desk.Evaluate(21_000, 4_000, Delay, Delay, returnMode));
    }

    [Fact]
    public void PhoneInputWakesAndRequiresNewAbsence()
    {
        var desk = AwayDesk();
        desk.PhoneInput(17_000);
        Assert.Equal(DeskAction.Wake, desk.Evaluate(18_000, 24_000, Delay, Delay, false));
        desk.SetAway(false, 18_000);
        desk.Observe(DeskReading.Absent, 19_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(19_000, 25_000, Delay, Delay, false));
    }

    private static DeskPresencePolicy AwayDesk()
    {
        var desk = new DeskPresencePolicy();
        desk.SetAway(true, 15_000);
        return desk;
    }
}
