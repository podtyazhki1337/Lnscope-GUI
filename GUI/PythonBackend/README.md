# LNscope Python functions: guide for colleagues

This folder contains the **executable logic** loaded by GUI.exe. Changing motion
behavior does not require Rider, Visual Studio, or a C# rebuild.

## Where to change behavior

| File | Functions |
| --- | --- |
| `motion.py` | `MotionController.move`, `jog`, `poll`, `feedback`, `watchdog`, `cancel`, `stop`, `trip`: the complete motion and stopping lifecycle |
| `jogging.py` | L/M/H `PROFILES`; `requests` combines directions from held keys |
| `links.py` | `members`: leader/follower axes, inverse coefficients, and link conflicts |
| `limits.py` | `blocks`, `target`: limit switches, motion away from a limit, and target validation |
| `sm10.py` | `Positioner`: read-only connection, C1/C2 coordinates, motion/Stop commands, and simulation |
| `commands.py` | All 82 documented IDs, GUI categories, parameter validation, packets, and response decoding for the advanced console; 81 executable commands |
| `coordinates.py` | Numeric coordinate validation |
| `tests/` | Logic checks using simulated hardware |
| `backend.py`, `motion_server.py` | JSON IPC with C#; usually no changes needed |

## Applying a change to a published application

1. Close GUI.exe and back up the `.py` file you will edit.
2. Open **`PythonBackend` beside the GUI.exe you actually run**, for example `artifacts/LNscope/PythonBackend/jogging.py`.
3. Edit the function and save the file as UTF-8.
4. Run the following in PowerShell from the `LNscope` folder:

   ```powershell
   ./ThirdParty/python/python.exe -I -X utf8 ./PythonBackend/check.py
   ```

5. Open GUI.exe again. Code loads from `.py`; no EXE/DLL rebuild is needed. Check the change in simulation first.

For example, to reduce the H step, replace this entry in `jogging.py`:

```python
"H": {"multiplier": 20.0, "speed": 1},
```

with:

```python
"H": {"multiplier": 10.0, "speed": 1},
```

With a base step of 5 µm, holding a direction key in H mode now requests 50 µm
instead of 100 µm. The GUI label comes from the same Python module. `speed: 0`
means Slow; `1` means Fast. These are application modes, not reconfiguration of
the physical controller's L/M/H buttons.

## Editing the source project

Edit `GUI/PythonBackend` in the repository. `publish.ps1` copies these files into
the published build. Before publishing again, bring colleagues' edits from
`artifacts/LNscope/PythonBackend` back into the source folder, or publishing will
replace them. To copy only Python source files into the published build, run
`./update-python.ps1` from the project root; this does not compile C#.

## Control architecture

`MotionController` runs in one asyncio loop. It checks all linked axes before
motion, then sends commands through `host`. Polling, events, and cancellation
are processed while waiting for hardware responses. Normal Stop waits for the
current exchange; emergency Stop does not wait for the motion queue. Connection
generation numbers prevent stale responses from affecting a device after reconnection.

For custom tests, provide `MotionController` with an asynchronous
`host(op, **args)` adapter and an `emit(name, data)` function; see
`tests/test_motion.py`. Add new UI operations through `MotionController.call`
and the existing IPC contract, or compose existing operations, rather than
calling new functions directly from the UI.

## What remains in C#

WPF and data presentation; COM/SDK adapters, frame validation and ACK/no-ACK
handling; IPC transport; and an independent emergency Stop if Python crashes or
hangs. The adapter also checks permitted axis addresses and configured bounds.
Every physical COM transmission has a minimum 2 ms delay inside the shared port
queue, including no-ACK commands and normal/emergency Stop. This also applies
to the Python console and rollback mode; no extra Python sleep is needed.
Camera capture, frame delivery to FFmpeg, and the legacy ASCII driver remain in C#.
The advanced SM-10 console uses `commands.py`; C# retains its UI, COM transport,
emergency Stop, and independent packet validation. The original C# controller
is retained as `LegacyMotionService` only for `MotionBackend: csharp` rollback
mode; Python mode does not call it.

Normal motion, polling, links, limits, keyboard steps, and stopping run from
this folder. Do not edit `LegacyMotionService` to change Python-mode behavior.

## Advanced commands through Python

Open **SM-10 commands** in the main window after disconnecting the positioner
groups. Select a category or search by name/hex ID. **Use device axes** inserts
the group's addresses; a single-axis command uses the first configured axis,
which can be edited manually. Four-axis commands require exactly four addresses
on the same controller.

`commands.catalogue()` defines visible commands and help text; `build()` creates
the payload; `describe()` decodes the response. `call("console_execute", args,
exchange)` executes one command through the supplied adapter. Browsing the
catalogue and previewing packets do not open the port. Changes to these
functions take effect after restarting the GUI without rebuilding. The
independent C# guard deliberately rejects undocumented IDs, incompatible
formats, and values outside documented ranges; extending the protocol itself
requires updating that guard.

The advanced console is a manual service tool without the main MotionController's
automatic software limits or links. Future automation should use MotionController
and its feedback checks. During continuous motion, only Stop is allowed on the
bus. An error locks the connection; if motion axes were involved, an independent
emergency Stop is attempted. Commands are not retried automatically. 01E8 is
blocked pending clarification of its format by the manufacturer.

## Video processing

`tracking.py` implements manual initialization, two-landmark tracking, and loss
thresholds; `vision.py` handles video and logging. Open the camera live-view window; Segment and Track run the bundled models.
Open test video provides temporary file playback for model verification. See `docs/OFFLINE_TRACKING.md` in the source project. This mode has no
hardware access. When first upgrading to this version, copy the entire
application folder: bundled OpenCV and NumPy were added alongside the new `.py`
files. Updating only Python source files does not install these dependencies.

`segmentation.py` runs bundled ONNX models and image-space identities without motor access. See `docs/VIDEO_ANALYSIS_AND_LOGGING.md` for the unified window and coordinate CSV format.
