namespace LnScope.Devices.LuigsNeumann.Protocol;

/// <summary>LN NextLevel ASCII command IDs from the 2024-07-01 document. IDs match the earlier binary protocol.</summary>
public static class LnCommands
{
    // System information.
    public const int QueryFirmwareVersionInterface = 0x0810;
    public const int QueryFirmwareVersionStageController = 0x015C;
    public const int QuerySystemStateAxisList = 0x0820;

    // Position and status.
    public const int QueryPositionStatus = 0x0800;  // µm, float
    public const int QueryStepsStatus = 0x0801;     // Integer microsteps.

    // Counters.
    public const int SetReferenceCounter = 0x0462;
    public const int SetUserCounter = 0x0463;

    // Output stage parameters.
    public const int QuerySlowPositioningVelocity = 0x0190;
    public const int SetSlowPositioningVelocity = 0x018F;
    public const int QueryFastPositioningVelocity = 0x0143;
    public const int SetFastPositioningVelocity = 0x0144;
    public const int QueryManipulatorPitch = 0x014D;
    public const int QueryMicroStepsPerTurn = 0x0467;
    public const int QueryStartStopRampLength = 0x0145;
    public const int SetStartStopRampLength = 0x003A;

    // Virtual end switches.
    public const int SetVirtualStopSwitchActive = 0x0465;
    public const int QueryVirtualStopSwitchActive = 0x0466;

    // InterfaceManager: movement with automatic feedback.
    public const int QueryActiveChannelIm = 0x0900;
    public const int QueryIntervalIm = 0x0901;
    public const int SetIntervalIm = 0x0902;
    public const int GotoFloatPositionIm = 0x0910;   // Target in micrometres.
    public const int GotoAbsolutePositionIm = 0x0920; // Target in microsteps.

    // AutoPositionManager: notifications for any position change, including manual controller input.
    public const int QueryAutoPositionDefinition = 0x0930;
    public const int SetAutoPositionDefinition = 0x0931;
    public const int QueryAutoPositionType = 0x0932;
    public const int SetAutoPositionType = 0x0933;
    public const int QueryAutoPositionInterval = 0x0934;
    public const int SetAutoPositionInterval = 0x0935;

    // PCS Manager: automatic pressure reports.
    public const int SetAutomaticOnOffPcs = 0x0940;
    public const int QueryAutoIntervalPcs = 0x0941;
    public const int SetAutoIntervalPcs = 0x0942;
    public const int QueryChannelGroupPcs = 0x0943;
    public const int SetChannelGroupPcs = 0x0944;
    public const int PcsDataTunnel = 0x0400;
}
