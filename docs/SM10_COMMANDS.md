# SM-10 commands

The **SM-10 commands** button in the main window opens a separate console for
the documented binary protocol. It operates the shared controller box connected
to the stage and manipulators. Normal positioner connections must be disconnected,
and LNconfig must release the COM port. The camera is independent of this port.

## Implemented commands

The catalogue contains all **82 identifiers** in the index of SM10 Interface
description V1.0, pages 33–34. Packet construction, validation, and the appropriate
response handling are implemented for **81 commands**. Trackball command 01E8
is displayed but blocked because the documentation contradicts itself.

| Function | Commands, hexadecimal |
|---|---|
| Continuous motion ±, fast/slow | 0012–0015 |
| Absolute and relative position | 0048–004B |
| Steps, step size, resolution | 0140, 0141, 0147, 044F, 0146 |
| Home, return, cancel Home | 0104, 0022, 013F |
| Zero and stored positions | 0024, 00F0, 0132, 0110, 010A |
| Axis power and Stop | 0034, 0035, 00FF |
| Speeds, mode, ramp, direction, coefficient | 003A, 003C, 003D, 0134, 0135, 0139, 013C, 0144, 0158, 018F, 0191, 019F, 042F, 0430 |
| Position and status | 0101, 0131, 0120, 011E, 011F |
| Drive parameter reads | 012F, 0130, 0138, 013D, 0143, 014B, 014D, 0159, 0160, 0161, 0190, 0192, 01A2, 0431 |
| Group actions with a 72-bit mask | A012–A015, A022, A024, A034, A035, A0F0, A0FF, A104, A110, A10A, A132, A13F, A140, A141 |
| Four positions/displacements in one packet | A048–A04B |
| Four-axis reads | A101, A131, A120 |

The independent C# catalogue and validators are in
`lnscope/LnScope.Devices.LuigsNeumann/Sm10/Sm10Commands.cs`. Programmatic access
uses `Sm10Commands.Get(id).Build(axes, values)` and `ISm10Bus.RequestAsync`.
For no-ACK commands, the call returns an empty result after writing to the port,
without waiting for acknowledgement. The GUI console's Python implementation
is described in the October 5 update below.

## Usage

1. Open the console, select COM3, and click **Open port**. Opening does not perform a handshake, polling, homing, or configuration reads.
2. Select a command and read its parameter help. Supply one address for a single-axis command, a list for a mask, or exactly four distinct addresses in one box for a four-axis packet (ranges 1–18, 19–36, 37–54, 55–72). The documentation does not define zero-filled unused slots, so that assumption is not used.
3. Separate values with spaces or semicolons; use a decimal point. For example: axes `1 2 3 4`, positions `10 -20 30 40`.
4. **Preview packet** displays bytes without sending. **Send once** sends exactly one packet.
5. Read requests and Stop are enabled by default. Explicitly enable advanced commands for movement or parameter changes. This low-level console does not apply the normal workspace's software travel limits; the operator chooses addresses and values.
6. **Save log** exports requests and responses. There are no macros, automatic retries, or automatic connections.

Continuous motion 0012–0015/A012–A015 blocks every other command except Stop.
After normal Stop, a minimum 160 ms pause is observed for the standard ramp.
This does not confirm stopping with a different ramp setting: the axis must
actually be stationary before reversing direction.

## Responses and errors

- Single-axis commands: ACK with zero data length. ACK confirms acceptance, not completion of motion.
- Single-axis queries: ACK with the expected data length and CRC validation.
- Group actions: no response. The log records transmission only.
- Group queries: documented SYN, a 20-byte response, CRC validation, and returned-address checks.
- Errors/timeouts lock the connection. The queue stops sending. Reconnection is manual.
- If the console sent motion during the session, an error triggers one group Stop attempt for the affected axes. Closing/Disconnect also does so if it has not already been attempted. Stop is not automatically retried.
- **STOP + abort pending** interrupts response waiting (cancellation is checked between reads; the read timeout is 50 ms), blocks new normal commands, and sends A0FF without waiting for ACK. The port timeout bounds the write wait. A failed communication channel prevents guaranteeing a physical stop.

## Documentation inconsistencies

- 01E8 Trackball: both English and German versions specify length 02 and a one-byte Steps field in the syntax, but a two-byte signed Steps value in the description. The command is registered as unavailable pending a clarified firmware-specific format.
- A048–A04B: the four-axis section defines 21 data bytes (15 hex), although the introduction limits packets to 20 bytes. These four commands allow 21 bytes.
- 0190: the English text specifies length 02 for a byte; the German original specifies 01. One byte is used.
- 0120: the eight-byte structure follows the German original, including two reserved bytes before step resolution.
- The version inquiry mentioned in the text has no defined ID/packet in the index; no version command was invented. LNconfig's physical/virtual address-map reads were likewise not replaced with undocumented packets.

## Verification

Automated tests use a simulated byte port: coverage of all 82 IDs, mask and
number order, CRC, single/group responses, no-response commands, timeouts,
blocking subsequent requests, Stop interrupting a response wait, and cancelling
queued motion. No commands were sent to real equipment during implementation.
Compatibility of every command with the specific firmware and its mechanical
behavior remains unconfirmed.

## Position memory in the Motion tab

Each device has slots 1–5. **Save** sends `A10A` for all its configured axes;
**Go** sends `A110` with the selected speed 1–15. The mask excludes other devices'
axes; motion links do not apply to Go. Saving again overwrites the slot. These
are group commands without ACK: the log confirms transmission, not successful
storage or arrival. The pre-transmission COM delay remains at least 2 ms.

Source: SM10 Interface description V1.0, SavePosition / GotoPosition (page 10)
and BC_SavePosition / BC_GotoPosition (page 25). BC_SavePosition's prose mentions
“6”, but the parameter range explicitly states 1–5; the interface uses that range.

The protocol cannot read a slot's coordinates. Hardware Go therefore cannot
pre-check its endpoint against software bounds. During return, the application
polls all axes and checks both limit switches and software bounds. Violations
or feedback loss stop all active motion. Software checks have polling latency
and do not replace hardware switches. Shared Stop cancels return. Completion
is determined from axis status rather than comparison with an unknown endpoint.
Use known slots saved for the current axis configuration.

Saving is blocked during application motion or detected device motion. The
physical keypad operates independently; do not move axes with it while saving.
Python SM-10 and simulation are supported. Simulated slots last until
reconnection; calling an unsaved slot raises an error.

## Python console: October 5, 2026 update

The catalogue of all 82 IDs, payload construction, user-value validation, and
response decoding now run in `GUI/PythonBackend/commands.py`. The GUI receives
the catalogue from Python, supports categories and search, and can insert
configured stage/manipulator addresses. Preview uses Python without COM requests.
Each Send once authorizes exactly one corresponding bus request. Transport,
the 2 ms delay, CRC/ACK validation, and independent emergency Stop remain in C#.

All 81 executable commands were tested through both languages using a simulator:
IDs, attributes, payloads, and CRC matched the independent C# guard. Tests also
covered responses, rejection of writes without advanced mode, and blocked 01E8.
No hardware commands were sent during development.
