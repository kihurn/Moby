using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

public class PhoneMetricsTests
{
    [Fact]
    public void ReadsCpuDeltaAvailableRamAndCurrentTemperatures()
    {
        var result = PhoneMetrics.Parse("""
            cpu 100 0 20 800 10 0 5 0 12 0
            MemTotal: 8000000 kB
            MemFree: 500000 kB
            MemAvailable: 3000000 kB
              level: 82
              temperature: 334
            Cached temperatures:
              Temperature{mValue=51.2, mType=0, mName=CPU, mStatus=0}
            Current temperatures from HAL:
              Temperature{mValue=42.2, mType=0, mName=CPU, mStatus=0}
              Temperature{mValue=43.4, mType=0, mName=CPU2, mStatus=0}
            Current cooling devices from HAL:
            cpu 120 0 30 860 20 0 5 0 15 0
            """);

        Assert.Equal(30, result.CpuPercent);
        Assert.Equal(5000000, result.RamUsedKb);
        Assert.Equal(8000000, result.RamTotalKb);
        Assert.Equal(82, result.BatteryPercent);
        Assert.Equal(33.4, result.BatteryTempC);
        Assert.Equal(43.4, result.CpuTempC);
    }

    [Fact]
    public void LeavesMissingAndInvalidReadingsUnavailable()
    {
        var result = PhoneMetrics.Parse("""
            cpu 100 0 20 800 10 0 5 0
            MemTotal: 8000000 kB
            MemAvailable: 9000000 kB
              level: -1
            Cached temperatures:
              Temperature{mValue=51.2, mType=0, mName=CPU, mStatus=0}
            """);

        Assert.Null(result.CpuPercent);
        Assert.Null(result.RamUsedKb);
        Assert.Null(result.BatteryPercent);
        Assert.Null(result.CpuTempC);
    }
}
