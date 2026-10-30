using LnScope.Core.Devices;
using LnScope.Devices.LuigsNeumann.Protocol;
using LnScope.Devices.LuigsNeumann.Transport;

namespace LnScope.Devices.LuigsNeumann;

/// <summary>Luigs &amp; Neumann NextLevel ASCII positioner driver. Axis addresses, travel limits and port settings come from the installation configuration.</summary>
public sealed class LnManipulator : IPositioner
{
    private readonly LnManipulatorOptions _options;
    private readonly Dictionary<string, LnAxisOptions> _axesById;
    private readonly Dictionary<int, string> _axisIdByHardware;
    private readonly ILnTransport? _transport;

    private LnConnection? _connection;
    private DeviceState _state = DeviceState.Disconnected;

    public LnManipulator(LnManipulatorOptions options, ILnTransport? transport = null)
    {
        _options = options;

        _axesById = options.Axes.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        _axisIdByHardware = options.Axes.ToDictionary(a => a.HardwareAxis, a => a.Id);

        _transport = transport;

        Axes = options.Axes
            .Select(a => new AxisInfo(a.Id, a.Unit, a.MinPosition, a.MaxPosition))
            .ToArray();
    }

    private static ILnTransport CreateTransport(LnManipulatorOptions options) =>
        options.Transport.ToLowerInvariant() switch
        {
            "serial" => new SerialLnTransport(options.PortName, options.BaudRate),
            "simulated" => new SimulatedLnTransport(options.Axes.Select(a => a.HardwareAxis)),
            _ => throw new ArgumentException($"Unknown transport '{options.Transport}'.")
        };

    public string Id => _options.Id;

    public IReadOnlyList<AxisInfo> Axes { get; }

    public DeviceState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<DeviceState>? StateChanged;

    public event EventHandler<AxisPosition>? PositionChanged;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (State == DeviceState.Ready) return;
        if (_connection is not null) await DisconnectAsync(ct);
        State = DeviceState.Connecting;

        try
        {
            // A disconnected connection owns and disposes its transport. Create
            // a fresh transport for each ordinary connection attempt.
            _connection = new LnConnection(_transport ?? CreateTransport(_options))
            {
                ResponseTimeout = TimeSpan.FromMilliseconds(_options.ResponseTimeoutMs)
            };

            _connection.FrameReceived += OnUnsolicitedFrame;
            await _connection.OpenAsync(ct);

            if (_options.SwitchToAsciiOnConnect)
            {
                await _connection.SendRawAsync(LnFrame.SwitchToAsciiSequence(), ct);
                await Task.Delay(200, ct);
            }

            // Verify the connection by requesting the interface firmware version.
            await _connection.RequestAsync(LnFrame.Create(LnCommands.QueryFirmwareVersionInterface), ct);

            await _connection.RequestAsync(
                LnFrame.Create(LnCommands.SetIntervalIm, _options.FeedbackIntervalMs), ct);

            State = DeviceState.Ready;
        }
        catch
        {
            State = DeviceState.Faulted;
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_connection is not null)
        {
            _connection.FrameReceived -= OnUnsolicitedFrame;
            await _connection.DisposeAsync();
            _connection = null;
        }

        State = DeviceState.Disconnected;
    }

    public async Task MoveAbsoluteAsync(string axisId, double position, MoveSpeed speed, CancellationToken ct = default)
    {
        var axis = ResolveAxis(axisId);
        var target = ClampToLimits(axis, position);

        var response = await Connection.RequestAsync(
            LnFrame.Create(LnCommands.GotoFloatPositionIm, axis.HardwareAxis, target, speed == MoveSpeed.Fast ? 1 : 0),
            ct);

        if (!response.IsAcknowledged)
        {
            // A negative acknowledgment means InterfaceManager has no free channel; it supports at most ten simultaneous axes.
            throw new InvalidOperationException(
                $"Controller rejected movement of axis {axisId}: no free InterfaceManager channel.");
        }
    }

    public async Task MoveRelativeAsync(string axisId, double delta, MoveSpeed speed, CancellationToken ct = default)
    {
        var current = await ReadPositionAsync(axisId, ct);
        await MoveAbsoluteAsync(axisId, current.Reference + delta, speed, ct);
    }

    /// <summary>No dedicated Stop is documented in the ASCII protocol, so this requests the current position. It does not replace a physical emergency stop.</summary>
    public async Task StopAsync(string axisId, CancellationToken ct = default)
    {
        var current = await ReadPositionAsync(axisId, ct);
        await MoveAbsoluteAsync(axisId, current.Reference, MoveSpeed.Fast, ct);
    }

    public async Task<AxisPosition> ReadPositionAsync(string axisId, CancellationToken ct = default)
    {
        var axis = ResolveAxis(axisId);

        var response = await Connection.RequestAsync(
            LnFrame.Create(LnCommands.QueryPositionStatus, axis.HardwareAxis), ct);

        return ParsePositionStatus(response) ?? throw new InvalidOperationException(
            $"Controller returned an invalid position response for axis {axisId}.");
    }

    private void OnUnsolicitedFrame(object? sender, LnFrame frame)
    {
        if (frame.Command != LnCommands.QueryPositionStatus)
        {
            return;
        }

        var position = ParsePositionStatus(frame);

        if (position is not null)
        {
            PositionChanged?.Invoke(this, position);
        }
    }

    /// <summary>Reply 0x0800 contains the axis, reference coordinate, user coordinate and eight status flags.</summary>
    private AxisPosition? ParsePositionStatus(LnFrame frame)
    {
        if (frame.Fields.Count < 11)
        {
            return null;
        }

        var hardwareAxis = frame.GetInt(0);

        if (!_axisIdByHardware.TryGetValue(hardwareAxis, out var axisId))
        {
            return null;
        }

        return new AxisPosition(
            axisId,
            frame.GetDouble(1),
            frame.GetDouble(2),
            IsMoving: frame.GetInt(9) != 0,
            StopSwitchActive: frame.GetInt(3) != 0,
            DateTimeOffset.UtcNow);
    }

    private LnAxisOptions ResolveAxis(string axisId) =>
        _axesById.TryGetValue(axisId, out var axis)
            ? axis
            : throw new ArgumentException($"Axis '{axisId}' is not defined in the configuration.", nameof(axisId));

    private static double ClampToLimits(LnAxisOptions axis, double position)
    {
        if (axis.MinPosition is { } min && position < min)
        {
            return min;
        }

        if (axis.MaxPosition is { } max && position > max)
        {
            return max;
        }

        return position;
    }

    private LnConnection Connection =>
        _connection ?? throw new InvalidOperationException("Manipulator is not connected.");

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
