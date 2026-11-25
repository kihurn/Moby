using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>Read the Windows counters in one query. Unsupported drivers and counters yield null.</summary>
internal sealed class MobyCounters : IDisposable
{
    private readonly nint _query, _clock, _read, _write;

    public MobyCounters()
    {
        if (PdhOpenQueryW(null, 0, out _query) != 0) return;
        _clock = Add(@"\Processor Information(_Total)\Processor Frequency");
        _read = Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _write = Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        PdhCollectQueryData(_query);
    }

    public (double? Mhz, double? Read, double? Write) Sample() =>
        _query != 0 && PdhCollectQueryData(_query) == 0 ? (Value(_clock), Value(_read), Value(_write)) : default;

    private nint Add(string name) => PdhAddEnglishCounterW(_query, name, 0, out var counter) == 0 ? counter : 0;

    private static double? Value(nint counter) =>
        counter != 0 && PdhGetFormattedCounterValue(counter, 0x200, 0, out var value) == 0
        && value.Status is 0 or 1 && double.IsFinite(value.Number) && value.Number >= 0 ? value.Number : null;

    public void Dispose() { if (_query != 0) PdhCloseQuery(_query); }

    [StructLayout(LayoutKind.Explicit)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Number;
    }

    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? source, nint userData, out nint query);
    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, nint type, out CounterValue value);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(nint query);
}
