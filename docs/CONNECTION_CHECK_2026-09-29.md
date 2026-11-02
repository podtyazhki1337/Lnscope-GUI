# Connection checks: September 29, 2026

This is a historical diagnostic report. The latest findings are listed first;
earlier provisional mappings below are retained to document the investigation.

## Update: fixes and repeat checks

### Axis mapping confirmed against the control panel

The user reported stage X/Y/Z = −1898 / 59 / 14532 and manipulator X/Y/Z =
12580 / −15329 / 17883. Additional read-only queries of addresses 7–9 returned
C2 = 12580.121 / −15329.485 / 17883.289 and
C1 = 12947.875 / −15593.820 / 19532.082. All stage values matched C2 at addresses
1–3. The final map in the Debug and published profiles was therefore
**stage 1/2/3, manipulator 7/8/9**. C2 is displayed with a label; C1 and its
limits are shown separately. Go conversion and retained limit checking were
tested only with a simulated transport, including a C2 zero change between
screen refresh and Go. References to an unconfirmed map below describe earlier
diagnostic stages.

The final published-GUI check after the fix showed both groups Ready at the
same time, with manipulator 12580.121 / −15329.485 / 17883.289 and stage
−1898.848 / 59.385 / 14532.974 µm. At the end of that check, the application was
left open with both groups connected. Only read requests were sent to real
hardware; no Go, steps, or other motion commands were sent. New conversion tests
and the complete smoke suite passed; Debug and self-contained Release were rebuilt.

### Earlier connection and camera fixes

After authorization to continue diagnosis, the driver successfully opened COM3.
The axis 1 position response was `06010104265F87C6BBBB`. Connect failed because
responses used a fixed trailing `BBBB` instead of CRC. Explicitly enabled
`AllowLegacyReplyChecksum` compatibility was added and enabled for COM3 in the
local Debug and published builds. Strict CRC validation remains the default;
response structure and values are still checked in compatibility mode.

At this stage, only 0101, 0131, and 0120 reads for addresses 1–6 were tested.
Reported positions were −17327.574, −17479.600, 53978.824, 0, 0, and −21665.574 µm;
statuses were `0001000101010000`, `0001000101010000`, `0001000100010000`,
`0000000000000000`, `0000000000000000`, and `0001000100010000`. Zero values at
addresses 4–5 did not confirm the presence of axes. Logical stage/manipulator
mapping was not yet established; some coordinates exceeded the current software
limits. The complete controller configuration was not read. No motion commands
were sent.

KERN ODC825 was discovered through `kerncam.dll` from the installed
MicroscopeVIS2.0Pro. The `kern-native` driver was added. Real 1280×960 capture,
Stop/Start, recording through CameraViewModel, and decoding the resulting MP4
with bundled FFmpeg were checked (6 frames, no queue drops). The camera also
reported 2592×1944 and 640×480 modes. Measured frame rate under those conditions
was about 2.86 fps; the image was almost black. Exposure settings and the optical
scene were not checked. The SDK remains an external dependency supplied by
MicroscopeVIS; FFmpeg is bundled.

The published `artifacts/LNscope/GUI.exe` was also checked through its interface:
both groups became Ready simultaneously on COM3; the camera became Ready and
Start live produced about 2.9 fps. Disconnecting the first group did not
disconnect the second. An old published profile assigned addresses 1–3 to both
groups. After this check, it was aligned with the then-existing Debug map:
manipulator 1–3, stage 4–6. This provisional map was later superseded by the
confirmed map at the top of this report. Addresses 4–6 were separately read as
described above. A repeat GUI Connect after the provisional edit was interrupted
by user interaction with the window. The application was restarted with devices
disconnected, leaving COM3 and the camera free. The Rider Debug build was also
updated. Before editing local profiles, backups were saved in
`.cache/config-backups/20260929-kern-fix`.

## Initial check before fixes

The initial check was limited to Windows discovery and an attempt to read
configuration through LN-Config. The application driver did not open the COM
port at that stage.

## Controller

- Windows: Prolific PL2303GC USB Serial COM Port (COM3), Status OK.
- USB VID 067B / PID 23A3. Signed Prolific driver 5.2.12.0, oem71.inf.
- LN-Config_V200.exe was open from Setup_docs. The Ethernet not connected title refers to Ethernet and does not itself establish COM status.
- The test panel already contained the Fast CW response `06001200BBBB`. This was existing UI content, not a result of this check; no test Send buttons were clicked.
- Edit Configuration was opened. One Read controller attempt returned COM Error. Communication stopped after the error; no repeat read was attempted. The error was left on screen.
- The complete current configuration was NOT read. The initial address table 1–72 could not be treated as a verified installation map.

## Camera

- Windows: USB2.0 Camera, USBDevice, Status OK, VID 0547 / PID F510.
- Installed Microsoft WinUSB 10.0.26100.9444, winusb.inf, signed.
- FFmpeg DirectShow listed only USB2.0 HD UVC WebCam (VID 322E / PID 2233) and OBS Virtual Camera. The connected USB2.0 Camera was absent from DirectShow.
- Capture was not started during this initial check. Integration with the current WinUSB driver required a compatible manufacturer SDK/adapter or a manufacturer-supported DirectShow driver. No drivers were changed.

## Communication constraints

SM10 Interface description V1.0 specifies no responses for collecting/group
commands. Response waiting must depend on command type. Missing ACK does not
generally mean a command was not executed; automatic retries are not acceptable.

Stopping command transmission after an error does not guarantee physical
stopping of an already moving axis. Hardware Stop and behavior on communication
loss were not tested in this check.

Communication stopped after the first error. Subsequent reads and their results
are documented in the update above.
