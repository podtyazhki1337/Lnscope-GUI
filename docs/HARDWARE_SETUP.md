# Hardware setup

Photos in the local installation archive (`Setup_docs`) show a KERN ODC 825 camera (USB 2.0)
and an L&N SM-10 touch compact control panel. Photos alone cannot reliably
establish the mapping between physical channels and X/Y/Z/D axes.

On September 29, 2026, the installation's axis map was established by comparing
controller responses with all six coordinates reported by the user from the
control panel: **stage X/Y/Z = 1/2/3; manipulator X/Y/Z = 7/8/9**. The panel
shows **counter 2** (0131), not counter 1 (0101). `DisplayUserCoordinates` is
enabled for both groups in the local profile. The main coordinate and Target
field use counter 2; C1 is shown separately. Before Go, both counters are read
again and `targetC1 = targetShown + C1 - C2` is computed. The hardware API,
software limits, and axis links continue to use C1. Relative steps do not depend
on the zero offset. Limits are not expanded automatically: some current C1
coordinates are outside the previously configured limits, so motion may be rejected.

## In the application

See [LIMITS_AND_JOG.md](LIMITS_AND_JOG.md) for red limit indicators, shared Stop,
and L/M/H modes. Hardware limit-switch states are read from the device; numeric
software limits remain a separate setting.

1. Open **Hardware setup** and click **Refresh devices**. Refreshing does not start video capture or send motion commands.
2. Select the **KERN ODC 825 + L&N SM-10** preset. It sets driver types; its axis addresses are suggestions that require verification. Motion is disabled and limits are unset.
3. For KERN, select **KERN / MicroscopeVIS** and **KERN ODC825**. Windows USB / DirectShow is available for UVC cameras. Apply selects the driver based on the chosen device. Resolutions are read after Connect. FPS = 0 removes the software cap; a positive value sets an upper limit. Actual frame rate depends on the camera and exposure.
4. For the shared controller box, enable **Stage and manipulator share one controller / cable** and select one **Controller COM port**. The SM-10 preset enables this option. The application shares one serial connection between axis groups. Physical axis addresses must not overlap between groups. For separate controllers, clear the checkbox and specify separate ports.
5. Click Apply. Settings are saved in `devices.json` beside the application; connections do not start automatically. SM-10 Connect reads positions and status without motion, homing, or enabling motors.
6. Verify axis addresses, directions, and permitted coordinates in micrometers on the installation. Enter finite Min/Max values before enabling motion. These software limits depend on the controller's coordinate system.

Disconnect the positioner groups before opening settings. Applying settings
cleanly stops camera capture and recording.

## Cameras and USB

The `windows-dshow` driver supports UVC/DirectShow cameras. The connected KERN
ODC825 (VID 0547 / PID F510) uses WinUSB and does not appear in DirectShow.
Its `kern-native` driver uses the installed library
`C:\Program Files\KERN&SOHN\MicroscopeVIS2.0Pro\x64\kerncam.dll`
(or x86 for a 32-bit process). Install MicroscopeVIS2.0Pro on another PC; this
library is not included in the LNscope distribution. Close MicroscopeVIS before
Connect to release the camera. No Windows driver replacement is required.

`AllowLegacyReplyChecksum` is enabled for the tested COM3 controller: its
responses end in `BBBB` instead of a computed CRC. Hardware setup provides this
checkbox for each group; groups sharing a port must use the same value.
Compatibility mode is disabled by default. Command, length, structure, and
value validity are still checked, but fixed `BBBB` cannot verify data integrity.
Valid computed CRCs are also accepted; other incorrect CRCs are rejected.

Official ODC 825 specifications: 2592×1944 up to 7 fps, 1296×972 up to 28 fps,
and 648×486 up to 101 fps. The application uses modes actually reported by the
installed driver. Source: [KERN technical data](https://www.kern-sohn.com/data/produktdatenauszug/Technische-Datenblaetter/TD_ODC-825_en.pdf).

FFmpeg is bundled. No separate installation or EXE selection is needed. Copy
the entire `artifacts/LNscope` folder, not just GUI.exe. Exposure and gain
control through the generic driver are not implemented yet.

The Frame rate field is available before connection. Enter 0 for the selected
mode's maximum rate, or a positive number. Enter, Tab, or leaving the field
commits the value. For a Windows camera, changes during capture take effect
after Stop live → Start live. The requested value is saved separately from
actual FPS, which is limited by the camera mode. Recording uses the current
capture rate. Editing the field during recording does not change the current
file's timeline.

## SM-10

The `sm10-binary` driver uses 115200 baud, 8N1, position commands 0101/0131,
status 0120 and motion 0048/0049. The command catalog includes single-axis
Stop 00FF with an acknowledgment. The emergency path sends group Stop A0FF
without waiting for a reply. CRC-16/1021 with an initial value
of zero is calculated over the data. Floats are little-endian; command IDs and
CRC are big-endian. Motion commands are not automatically retried after a timeout.

Sources: `CD-SM10V20-TAS7-TAS10/SM10 Interface description V1.0.pdf` and the
German original `SM10 Beschreibung des seriellen Protokolls V1.0.pdf` in
Setup_docs. The English description of the eight-byte status omits a reserved
byte. The implementation follows the German original, page 18: motion state is
at byte index 6. The legacy NextLevel ASCII protocol remains a separate driver
and is not selected for SM-10.

A communication error locks the connection until all groups on that COM port
are disconnected and reconnected. There is no automatic protocol fallback.
If the controller is configured differently, verify its mode against the documentation.

## Verification and outstanding checks

September 28, 2026: the build and automated checks passed. Checks covered
CRC/packets, camera/mode parsing, read-only connection on a test bus, target
limits, axis addressing, Stop, reconnection, and MP4 recording/decoding without
FFmpeg in PATH.

At that initial discovery check, KERN and COM ports were absent; only USB2.0 HD
UVC WebCam and OBS Virtual Camera were available. Real KERN capture, SM-10
communication, axis directions, stopping, communication loss, and long recordings
required verification with the installation connected. Tests did not move hardware.
For subsequent real-device reads and capture checks, see
[CONNECTION_CHECK_2026-09-29.md](CONNECTION_CHECK_2026-09-29.md).

`ICamera` and `IPositioner` separate the GUI from hardware. Supporting a different
camera with its own SDK or another manipulator requires a driver implementing
these interfaces; a universal USB connection does not replace the manufacturer's protocol.
