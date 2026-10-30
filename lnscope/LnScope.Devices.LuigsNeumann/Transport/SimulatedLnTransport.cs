using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LnScope.Devices.LuigsNeumann.Protocol;

namespace LnScope.Devices.LuigsNeumann.Transport;

/// <summary>SM-10/SM-11 simulator that handles commands and emits position reports during movement, enabling GUI development without a microscope.</summary>
public sealed class SimulatedLnTransport : ILnTransport
{
    private sealed class SimulatedAxis
    {
        public double Position;
        public double Target;
        public bool IsMoving;
        public CancellationTokenSource? Movement;
    }

    private readonly Dictionary<int, SimulatedAxis> _axes = new();
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private readonly LnFrameReader _reader = new();
    private readonly double _slowSpeed;
    private readonly double _fastSpeed;
    private int _feedbackIntervalMs = 300;

    /// <param name="hardwareAxes">Axis addresses present in the simulated controller.</param>
    /// <param name="slowSpeedUmPerSecond">Movement rate in slow mode.</param>
    /// <param name="fastSpeedUmPerSecond">Movement rate in fast mode.</param>
    public SimulatedLnTransport(
        IEnumerable<int> hardwareAxes,
        double slowSpeedUmPerSecond = 50,
        double fastSpeedUmPerSecond = 2000)
    {
        foreach (var axis in hardwareAxes)
        {
            _axes[axis] = new SimulatedAxis();
        }

        _slowSpeed = slowSpeedUmPerSecond;
        _fastSpeed = fastSpeedUmPerSecond;
    }

    public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var bytes = data.ToArray();

        foreach (var frame in _reader.Feed(bytes, bytes.Length))
        {
            Handle(frame);
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<byte[]> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (await _incoming.Reader.WaitToReadAsync(ct))
        {
            while (_incoming.Reader.TryRead(out var chunk))
            {
                yield return chunk;
            }
        }
    }

    private void Send(LnFrame frame) => _incoming.Writer.TryWrite(frame.Encode());

    private void Handle(LnFrame frame)
    {
        switch (frame.Command)
        {
            case LnCommands.QueryFirmwareVersionInterface:
                Send(LnFrame.Create(frame.Command, 3, 5, 3));
                break;

            case LnCommands.QuerySystemStateAxisList:
                Send(LnFrame.Create(frame.Command, 1, BuildAxisList()));
                break;

            case LnCommands.QueryPositionStatus:
                SendPositionStatus(frame.GetInt(0));
                break;

            case LnCommands.SetIntervalIm:
                _feedbackIntervalMs = frame.GetInt(0);
                Send(LnFrame.Create(frame.Command, 1));
                break;

            case LnCommands.GotoFloatPositionIm:
                StartMovement(frame.GetInt(0), frame.GetDouble(1), frame.GetInt(2) == 1);
                Send(LnFrame.Create(frame.Command, 1));
                break;

            default:
                // Reject unknown commands with a negative acknowledgment, as the physical controller does.
                Send(LnFrame.Create(frame.Command, 0));
                break;
        }
    }

    private string BuildAxisList()
    {
        var list = new char[72];
        Array.Fill(list, '0');

        foreach (var axis in _axes.Keys)
        {
            if (axis >= 1 && axis <= list.Length)
            {
                list[axis - 1] = '1';
            }
        }

        return new string(list);
    }

    private void SendPositionStatus(int axisNumber)
    {
        if (!_axes.TryGetValue(axisNumber, out var axis))
        {
            return;
        }

        Send(new LnFrame(LnFrame.DefaultGroup, LnCommands.QueryPositionStatus,
        [
            axisNumber.ToString(CultureInfo.InvariantCulture),
            axis.Position.ToString("0.###", CultureInfo.InvariantCulture),
            axis.Position.ToString("0.###", CultureInfo.InvariantCulture),
            "0",                              // StopSwitchState
            "1",                              // PowerState
            "0",                              // HomerState
            "1",                              // PresentState
            "0",                              // MoveDirection
            "1",                              // HandwheelResolution
            axis.IsMoving ? "1" : "0",        // MotorRunningState
            "0"                               // ExtendedState
        ]));
    }

    private void StartMovement(int axisNumber, double target, bool fast)
    {
        if (!_axes.TryGetValue(axisNumber, out var axis))
        {
            return;
        }

        axis.Movement?.Cancel();
        var cts = new CancellationTokenSource();
        axis.Movement = cts;
        axis.Target = target;
        axis.IsMoving = true;

        var speed = fast ? _fastSpeed : _slowSpeed;

        _ = Task.Run(async () =>
        {
            var interval = _feedbackIntervalMs;
            var step = speed * interval / 1000.0;

            try
            {
                SendPositionStatus(axisNumber); // First report immediately after starting.

                while (!cts.Token.IsCancellationRequested && Math.Abs(axis.Target - axis.Position) > step)
                {
                    await Task.Delay(interval, cts.Token);
                    axis.Position += Math.Sign(axis.Target - axis.Position) * step;
                    SendPositionStatus(axisNumber);
                }

                axis.Position = axis.Target;
            }
            catch (OperationCanceledException)
            {
                // Movement was interrupted by a new command.
            }
            finally
            {
                axis.IsMoving = false;
                SendPositionStatus(axisNumber); // Final report after stopping.
            }
        }, cts.Token);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var axis in _axes.Values)
        {
            axis.Movement?.Cancel();
        }

        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
