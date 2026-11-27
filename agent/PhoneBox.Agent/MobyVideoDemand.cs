using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace PhoneBox.Agent;

internal sealed class MobyVideoDemand : IDisposable
{
    internal const string FrameMapping = "Moby On-Demand Camera/SharedMemory";
    private const string DemandMapping = "Moby On-Demand Camera/Demand";
    private readonly MemoryMappedFile _mapping;

    [SupportedOSPlatform("windows")]
    internal MobyVideoDemand()
    {
        _mapping = MemoryMappedFile.CreateOrOpen(DemandMapping, sizeof(long));
        using var view = _mapping.CreateViewAccessor(0, sizeof(long));
        view.Write(0, 0L);
    }

    internal long Read()
    {
        using var view = _mapping.CreateViewAccessor(0, sizeof(long), MemoryMappedFileAccess.Read);
        return view.ReadInt64(0);
    }

    internal static bool ShouldWake(long requestedAt, long now) =>
        requestedAt > 0 && requestedAt <= now && now - requestedAt < 2_000;

    internal static bool ShouldSleep(long requestedAt, long now) =>
        requestedAt <= 0 || requestedAt > now || now - requestedAt >= 60_000;

    public void Dispose() => _mapping.Dispose();
}
