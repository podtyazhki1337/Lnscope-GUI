# LNscope GUI

WPF / .NET 10 application for manipulator and stage control, keyboard jogging,
axis links, camera preview, and recording with bundled FFmpeg.

## Running the application

The self-contained Windows x64 build is `artifacts/LNscope/GUI.exe`. Copy the
entire `LNscope` folder: it includes the .NET and Python runtimes and recording
component. The target computer does not need separate .NET, Python, or FFmpeg
installations. Run `./publish.ps1` to recreate this folder.

The main motion controller, polling, limits, axis links, L/M/H modes, and
positioner operations run in Python. Colleagues can edit the `.py` files next
to the EXE without rebuilding C#; see the [editing guide](GUI/PythonBackend/README.md).
The interface and independent emergency Stop remain in C#. Every COM command
has a minimum 2 ms delay before transmission. Migration scope, verification, and
the rollback backup are documented in [PYTHON_BACKEND.md](docs/PYTHON_BACKEND.md).

From PowerShell in the repository root:

```powershell
./run.ps1
```

Building requires the .NET 10 SDK. The script searches `%USERPROFILE%/.dotnet`
first, then PATH, supporting the SDK installation on this computer. Launching a
non-self-contained `GUI.exe` directly may not find a per-user runtime. Close the
application before rebuilding.

```powershell
./run.ps1 -Check
```

Checks use simulators regardless of the configured transport or driver. They
cover numeric input, connection, relative motion, link persistence, frame
capture, rejection of disabled commands, and shutdown. The test configuration
is saved to `GUI.SmokeTests/bin/.../smoke-config.json`.

## Video analysis and coordinate logs

The camera live-view window includes Segment, Track, and temporary test-video
playback. Each recording gets its own timestamped folder with the MP4 and a
sparse event CSV (position changes and sent commands). See
[VIDEO_ANALYSIS_AND_LOGGING.md](docs/VIDEO_ANALYSIS_AND_LOGGING.md) for usage and
the current experimental model limitations. Restored native-frame annotations
are described in [FULL_FRAME_ANNOTATIONS.md](docs/FULL_FRAME_ANNOTATIONS.md).

## Using simulation

1. Click Connect for the desired manipulator or stage.
2. Enter a relative step in the axis row and use −/+, or enter an absolute target and click Go.
3. For the camera, use Connect → Start live → Open live view.
4. Keyboard mappings are on the Keyboard tab. Apply activates changes; Save configuration persists them.
5. Select a folder in Recording settings and click Record. Capture starts automatically if stopped. Stop recording finalizes the MP4. An empty folder setting uses the current user's Videos/LNscope directory.

FFmpeg is bundled in `GUI/ThirdParty/ffmpeg` and copied automatically during
build and publish. There is no EXE selector, PATH lookup, or startup download.
The legacy `ffmpegPath` configuration key is ignored. Version, checksums,
license, and source information are in `GUI/ThirdParty/ffmpeg/NOTICE.md`.

The supplied `GUI/devices.json` uses simulators only. The application reads and
saves `devices.json` beside the running build. GUI changes are not written back
to the source `GUI/devices.json`; updating that source file may replace the
configuration in the build folder. A separate user profile is needed before release.

## Readiness snapshot: September 28, 2026

This section records the state on that date. Later hardware checks and changes
are documented in [HARDWARE_SETUP.md](docs/HARDWARE_SETUP.md),
[CONNECTION_CHECK_2026-09-29.md](docs/CONNECTION_CHECK_2026-09-29.md),
[LIMITS_AND_JOG.md](docs/LIMITS_AND_JOG.md), and
[PYTHON_BACKEND.md](docs/PYTHON_BACKEND.md).

At this stage, the application was a working simulation prototype; readiness
for experiments on real equipment had not been confirmed. Verification covered
a warning-free build, a simulated manipulator connection, a 10 µm X step, live
camera preview, and closing the window during capture. Automated checks passed.
Bundled FFmpeg recording was tested with an empty PATH: MP4 creation and complete
decoding, repeated Record, Stop live, closing during recording, padded image
rows, and concurrent Stop calls. Encoder failures were reported as errors
rather than successful saves.

### Checks before operating the installation

- **Camera:** Windows/USB DirectShow selection and KERN ODC 825 support were added; see the [connection guide](docs/HARDWARE_SETUP.md). Full control requires the SDK/driver, an ICamera implementation, and checks of modes, buffer format/stride, FPS, exposure, gain, and recovery after disconnection.
- **L&N:** confirm controller model and protocol, COM ports, axis addresses, directions, units, and limits on the installation. Check timeouts and reconnection. Disconnect → Connect in simulation was fixed and is tested automatically; reopening an externally supplied ILnTransport still depends on that implementation.
- **Stop and links:** SM-10 binary uses hardware command 00FF. In the legacy ASCII driver, Stop moves to the last read Reference position, and releasing a key stops new step requests. This is not a verified hardware stop. Check actual coordinates, stopping, communication loss, and closing during motion. In this historical implementation, MotionService mirrored the requested displacement before applying the leader's limit; coefficients other than ±1 required an inverse transformation.
- **Recording:** check duration and frame count with unstable FPS or queue overflow, real camera mode changes, full disks, and long recordings. MP4 uses a constant frame rate rather than frame timestamps. Start and Stop are serialized; each recording owns its queue, process, and writer task. MP4 finalization is awaited before releasing the process.

### Checks before distributing to other users

- Validate configuration before creating devices: duplicate IDs/keys, unknown axes/drivers, null values, invalid ranges, empty lists, and useful error messages.
- Store user profiles outside the build directory, save atomically, and persist image settings. Export the experiment log; at this stage it held the last 500 entries in memory.
- Define a shared software Stop and block further motion after faults; verify reconnection and linked motion at limits.
- Test the self-contained distribution on a clean Windows installation and the target monitors/DPI settings.

### Interface changes and fixes

Shared styles in Theme.xaml, device cards, axis field labels, a roomier layout,
collapsible recording settings, a SIMULATION indicator, the latest event, and
configuration saving in the header were added. The camera window has a startup
screen. The log marshals background events to the UI thread; links retain their
state; step/target values are checked for finiteness and range. Closing stops
the camera/recorder and releases devices. FFmpeg queue waits have a timeout,
dropped-frame counting uses failed TryWrite calls, and filenames include milliseconds.
