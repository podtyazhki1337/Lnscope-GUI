using System.Diagnostics;

namespace LnScope.Devices.LuigsNeumann.Transport;

/// <summary>Controller-required quiet time before every write, including no-ACK commands and Stop.</summary>
public static class CommandPacing
{
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(2);

    public static async Task BeforeWriteAsync(CancellationToken ct = default)
    {
        var start = Stopwatch.GetTimestamp();
        var remaining = MinimumDelay;
        do
        {
            await Task.Delay(remaining, ct).ConfigureAwait(false);
            remaining = MinimumDelay - Stopwatch.GetElapsedTime(start);
        } while (remaining > TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();
    }
}
