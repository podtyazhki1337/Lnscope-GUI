using System.Collections.Concurrent;
using LnScope.Devices.LuigsNeumann.Protocol;
using LnScope.Devices.LuigsNeumann.Transport;

namespace LnScope.Devices.LuigsNeumann;

/// <summary>Request/reply layer over the transport. The controller also sends unsolicited InterfaceManager and AutoPositionManager messages, so reading is continuous and replies are matched by command code.</summary>
public sealed class LnConnection : IAsyncDisposable
{
    private readonly ILnTransport _transport;
    private readonly LnFrameReader _reader = new();
    private readonly SemaphoreSlim _requestLock = new(1, 1);

    private readonly ConcurrentDictionary<int, ConcurrentQueue<TaskCompletionSource<LnFrame>>> _pending = new();

    private CancellationTokenSource? _cts;
    private Task? _readLoop;

    public LnConnection(ILnTransport transport) => _transport = transport;

    /// <summary>Messages received without a corresponding request.</summary>
    public event EventHandler<LnFrame>? FrameReceived;

    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public async Task OpenAsync(CancellationToken ct = default)
    {
        await _transport.OpenAsync(ct);
        _cts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in _transport.ReadAsync(ct))
            {
                foreach (var frame in _reader.Feed(chunk, chunk.Length))
                {
                    Dispatch(frame);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal termination during disconnect.
        }
    }

    private void Dispatch(LnFrame frame)
    {
        if (_pending.TryGetValue(frame.Command, out var queue) && queue.TryDequeue(out var waiter))
        {
            waiter.TrySetResult(frame);
            return;
        }

        FrameReceived?.Invoke(this, frame);
    }

    /// <summary>Send a command and wait for a reply with the same command code.</summary>
    public async Task<LnFrame> RequestAsync(LnFrame request, CancellationToken ct = default)
    {
        await _requestLock.WaitAsync(ct);

        try
        {
            var waiter = new TaskCompletionSource<LnFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.GetOrAdd(request.Command, _ => new ConcurrentQueue<TaskCompletionSource<LnFrame>>()).Enqueue(waiter);

            await _transport.WriteAsync(request.Encode(), ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ResponseTimeout);

            await using (timeout.Token.Register(() => waiter.TrySetCanceled()))
            {
                try
                {
                    return await waiter.Task;
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Controller did not respond to command 0x{request.Command:X4} within {ResponseTimeout.TotalMilliseconds:0} ms.");
                }
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>Send raw bytes without awaiting a reply, for protocol switching.</summary>
    public Task SendRawAsync(byte[] data, CancellationToken ct = default) => _transport.WriteAsync(data, ct);

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected cancellation during shutdown.
            }
        }

        _cts?.Dispose();
        _requestLock.Dispose();
        await _transport.DisposeAsync();
    }
}
