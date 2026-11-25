using PhoneBox.Agent;
using Xunit;

public sealed class DeskPresenceTests
{
    private const int Delay = 10_000;

    [Fact]
    public void AbsenceNeedsAPositiveBaselineFreshSamplesAndInputIdle()
    {
        var desk = new DeskPresencePolicy();
        desk.Observe(DeskReading.Absent, 1_000);
        desk.Observe(DeskReading.Absent, 6_000);
        desk.Observe(DeskReading.Absent, 11_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(11_000, 20_000, Delay, false));

        desk.Observe(DeskReading.Present, 13_000);
        desk.Observe(DeskReading.Absent, 15_000);
        desk.Observe(DeskReading.Absent, 20_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(24_000, 20_000, Delay, false));
        desk.Observe(DeskReading.Absent, 25_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(25_000, 50, Delay, false));
        desk.Observe(DeskReading.Absent, 30_000);
        desk.Observe(DeskReading.Absent, 35_000);
        desk.Observe(DeskReading.Absent, 40_000);
        Assert.Equal(DeskAction.Standby, desk.Evaluate(40_000, 11_000, Delay, false));
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
        Assert.Equal(DeskAction.None, desk.Evaluate(25_000, 25_000, Delay, true));
        desk.Observe(DeskReading.Absent, 26_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(26_000, 26_000, Delay, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyReturnModeWakesOnTwoPresentFrames(bool returnMode)
    {
        var desk = AwayDesk();
        desk.Observe(DeskReading.Present, 16_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(16_000, 21_000, Delay, returnMode));
        desk.Observe(DeskReading.Present, 19_000);
        Assert.Equal(returnMode ? DeskAction.Wake : DeskAction.None, desk.Evaluate(19_000, 24_000, Delay, returnMode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MouseOrKeyboardWakesInDarknessEvenIfInputWasSeveralSecondsAgo(bool returnMode)
    {
        var desk = AwayDesk();
        desk.Observe(DeskReading.Unknown, 16_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(16_000, 21_000, Delay, returnMode));
        Assert.Equal(DeskAction.Wake, desk.Evaluate(21_000, 4_000, Delay, returnMode));
    }

    [Fact]
    public void PhoneInputWakesAndRequiresNewAbsence()
    {
        var desk = AwayDesk();
        desk.PhoneInput(17_000);
        Assert.Equal(DeskAction.Wake, desk.Evaluate(18_000, 24_000, Delay, false));
        desk.SetAway(false, 18_000);
        desk.Observe(DeskReading.Absent, 19_000);
        Assert.Equal(DeskAction.None, desk.Evaluate(19_000, 25_000, Delay, false));
    }

    private static DeskPresencePolicy AwayDesk()
    {
        var desk = new DeskPresencePolicy();
        desk.SetAway(true, 15_000);
        return desk;
    }
}
