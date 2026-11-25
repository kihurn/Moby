using PhoneBox.Agent;
using Xunit;

public sealed class MobyCallTests
{
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
