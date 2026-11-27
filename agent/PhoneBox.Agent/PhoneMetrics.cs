using System.Globalization;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

internal static class PhoneMetrics
{
    // Two aggregate samples measure CPU activity over roughly a second.
    private const string Command = """
        head -n 1 /proc/stat; grep -E '^Mem(Total|Available):' /proc/meminfo; dumpsys battery; dumpsys thermalservice; sleep 1; head -n 1 /proc/stat
        """;

    internal sealed record Snapshot(double? CpuPercent, long? RamUsedKb, long? RamTotalKb,
        int? BatteryPercent, double? BatteryTempC, double? CpuTempC);

    internal static async Task<Snapshot?> ReadAsync(string adb, string serial, CancellationToken ct)
    {
        try
        {
            var output = await AdbReverse.Adb(adb, $"-s {serial} shell \"{Command}\"", ct);
            return Parse(output);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            return null;
        }
    }

    internal static Snapshot Parse(string output)
    {
        var total = Number(output, "MemTotal");
        var available = Number(output, "MemAvailable");
        var used = total > 0 && available >= 0 && available <= total ? total - available : null;
        var battery = Number(output, "level");
        var batteryTemp = Number(output, "temperature");
        var cpu = Regex.Matches(output, @"^cpu[ \t]+([0-9 \t]+)\r?$", RegexOptions.Multiline);
        var cpuPercent = cpu.Count == 2 ? CpuLoad(cpu[0].Groups[1].Value, cpu[1].Groups[1].Value) : null;

        // The cached thermal section can lag behind the current HAL readings.
        const string liveHeader = "Current temperatures from HAL:";
        var liveStart = output.IndexOf(liveHeader, StringComparison.Ordinal);
        double? cpuTemp = null;
        if (liveStart >= 0)
        {
            foreach (Match match in Regex.Matches(output[(liveStart + liveHeader.Length)..],
                         @"Temperature\{mValue=(-?\d+(?:\.\d+)?), mType=0,"))
            {
                if (double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    cpuTemp = Math.Max(cpuTemp ?? value, value);
            }
        }

        return new(cpuPercent, used, total > 0 ? total : null,
            battery is >= 0 and <= 100 ? (int)battery.Value : null, batteryTemp / 10.0, cpuTemp);
    }

    private static long? Number(string output, string key)
    {
        var match = Regex.Match(output, $@"^[ \t]*{key}:[ \t]*(-?\d+)\b", RegexOptions.Multiline);
        return long.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static double? CpuLoad(string first, string last)
    {
        var start = first.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var end = last.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (start.Length < 8 || end.Length < 8) return null;
        double total = 0, active = 0;
        // Guest ticks are already counted in user/nice. Idle and I/O wait are excluded from busy time.
        for (var i = 0; i < 8; i++)
        {
            if (!long.TryParse(start[i], out var before) || !long.TryParse(end[i], out var after) || after < before)
                return null;
            var delta = after - before;
            total += delta;
            if (i is not (3 or 4)) active += delta;
        }
        return total > 0 ? active * 100 / total : null;
    }
}
