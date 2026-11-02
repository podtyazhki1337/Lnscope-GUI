# Python migration — updated October 2, 2026

The interface and its appearance remain WPF/C#. `MotionBackend` in `devices.json`
defaults to `python`; an absent key in an older configuration also selects Python.

## What runs in Python

`GUI/PythonBackend/sm10.py` contains SM-10 driver operations: status and C1/C2
counter reads, coordinate and hardware limit decoding, absolute and relative
motion, Fast/Slow selection, command parameter packing, and target validation.
Forward/inverse axis links, linked targets, direction checks against limit
switches, conversion of entered C2 targets into C1 using fresh coordinates, and
the positioner simulator have also moved to Python.

`motion.py` is the main controller: axis reservation, checking the entire linked
operation before the first command, motion dispatch, polling, feedback, limits,
key release, timeouts, and emergency lockout. `jogging.py` defines L/M/H and
combines held keys. `links.py`, `limits.py`, and `coordinates.py` contain their
respective functions. C# `MotionService` forwards operations to this controller
instead of running its own algorithm alongside Python.

**For colleagues:** see the [guide to changing functions without rebuilding C#](../GUI/PythonBackend/README.md).
The guide and tests are shipped in `PythonBackend` beside GUI.exe.

## What remains in C#

WPF, keyboard event capture and the UI timer, ownership of the physical port,
camera capture, KERN SDK, DirectShow, and bundled FFmpeg recording remain in C#.
The legacy ASCII driver also remains in C#. At the date of this migration report,
the full SM-10 command console was still implemented in C#; its catalogue,
payload construction, and response decoding subsequently moved to Python on
October 5, as described in [SM10_COMMANDS.md](SM10_COMMANDS.md).

The shared COM port, frame/CRC construction and validation, ACK/no-ACK policy,
request arbitration, and emergency Stop remain in C#. C# additionally checks
motion permission, physical-axis membership, and the transmitted float coordinate
range. Python has no direct port access, allowing Stop to be sent if Python
hangs or exits. Decisions about limits, links, feedback loss, and normal stopping
run in Python; an independent C# path stops devices if the Python process crashes
or hangs. The original C# algorithm remains only for rollback in
`LegacyMotionService` and is not called in Python mode.

Every controller command has a **minimum 2 ms delay inside the shared COM-port
queue**: reads, moves, no-ACK commands, and normal/emergency Stop. This also
applies to the advanced console and C# mode. Windows scheduling may lengthen
the delay but does not shorten it below 2 ms. The existing post-Stop pause is separate.

## Running and building

Starting the GUI creates the main Python motion controller. Each connected
logical group also starts its own hidden Python driver process. Both groups
continue to share one serialized COM transport. Python communication uses JSON
Lines over private stdin/stdout; there is no network server. Driver processes
exit on Disconnect; the main process exits when the application closes. After
a failure, disconnect and reconnect all groups manually; unfinished moves are
not retried. Connection generations reject late commands and responses from
previous connections.

CPython 3.13.15 is bundled in `GUI/ThirdParty/python`, with its original license.
`GUI.csproj` copies the runtime and Python code into Debug/Release and publish
outputs. On another Windows x64 computer, copy **the entire `artifacts/LNscope`
folder** and run `GUI.exe`. Published builds do not need separate Python, .NET,
or FFmpeg installations. Camera drivers, KERN SDK, and USB/COM drivers remain
hardware requirements, as before the migration.

Build: `./publish.ps1`. C#/IPC checks: `./run.ps1 -Check`.
Pure Python logic checks:

```powershell
./GUI/ThirdParty/python/python.exe -I -X utf8 ./GUI.SmokeTests/python_checks.py
./GUI/ThirdParty/python/python.exe -I -X utf8 ./GUI/PythonBackend/check.py
```

Tests do not access real ports. They cover coordinates/addresses, linked targets,
Fast/Slow, rejection before the first command when a follower is at a limit,
Stop for both groups on a limit, worker termination or timeout, prevention of
further motion, reconnection, operation with an empty PATH, the GUI, and MP4
creation/decoding. They also cover main-controller failure, manual recovery,
the minimum 2 ms delay, and behavior changes after editing Python without a C# build.

Debug and Release built without warnings. The published `artifacts/LNscope/GUI.exe`
was opened and checked through the interface: simulator Connect, X from 0 to
10 µm, then Disconnect. Main XAML files matched the backup; saved Debug and
published profiles also matched byte for byte. Real motion after migration and
operation on a separate clean PC had not been tested. Python does not change
saved axis maps, limits, or MotionEnabled.

## Rollback

Complete snapshot from before migration, including uncommitted Git changes and
the published application:

`C:\Users\podtyazhki\Desktop\PHD\Spheroids\Cellpicking_gui\Lnscope-rollback-20261001-081504`

- `LNscope/GUI.exe`: previous self-contained application. Run it from that folder after closing the new application; do not open two applications on the same COM port.
- `source/`: previous source and interface. Restore into a new project folder to preserve the migration result.
- `rider-devices.json`: saved Debug/Rider profile.
- `SHA256.json`: verified manifest of 497 saved files.

For a quick driver switch in the new build, close the application, set
`"MotionBackend": "csharp"` in `devices.json` beside the EXE, and reopen it.
This enables the original drivers and C# calculations. Use the saved `LNscope`
folder for a complete rollback.
