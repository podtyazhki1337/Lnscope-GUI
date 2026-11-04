# Video analysis and coordinate recording

Open the camera's **Open live view** window. **Segment** enables segmentation;
**Track** also assigns temporary image-space IDs. Click an outline to select an
ID. Missing objects are marked lost rather than automatically reacquired. Seek,
source changes, and tracking resets invalidate previous associations. Ambiguous
splits/merges and changes between cell/group classes create new IDs.

**Test video > Open video**, Play / pause, Next frame, and Seek provide the temporary
recording-review mode requested for model verification. **Live camera** returns
to capture. Motion hotkeys are ignored in test-video mode and while editing
any control in the sidebar. The standalone Offline tracking and Object map buttons have
been removed from the main window; their source files remain for historical
reference. Analysis does not issue motor commands or estimate machine coordinates.

Models and ONNX Runtime are bundled. No system Python, PyTorch, Ultralytics,
CUDA installation, or model download is needed for inference. The runtime uses
CPU; latency depends on scene complexity and the number of proposed cell regions.
The image displayed during analysis is the frame that produced the contours,
not a newer live image with old contours. Only one analysis request runs at a
time; camera capture and raw recording continue separately. Overlays are not
burned into recorded video.

## Current model limitations

The supplied models are the completed larger crop-based training run, exported
to ONNX at 512 pixels. On the held-out test split, segmentation mAP50–95 was
0.9568 for spheroid masks and 0.4182 for cell/group crops. The latter is weaker
than validation performance and does not measure full-frame detection.

The cell model was trained on local crops. The current experimental adapter
proposes compact image-edge regions before running the cell model; this is not
a substitute for full-frame training. It can miss low-contrast objects or merge,
split, or misclassify objects near tissue and the pipette. Tissue uses the
separate full-frame model. Confidence values are model scores, not calibrated
probabilities. Do not use these outputs as autonomous motion targets.

The native-frame workflow now includes human review, a three-class bootstrap
training run and model-assisted prefill. The published GUI uses the two ONNX
models described above. [Full-frame annotations](FULL_FRAME_ANNOTATIONS.md)

## Recording folder and event log

Every Record operation creates a new folder named by local start date and time,
for example `2026-11-03_08-58-25-243_0ccddbc5`, inside the configured output
folder. The 8-character suffix keeps simultaneous camera recordings apart. The
folder contains exactly two files: `name.mp4` and `name.csv`. Nothing is
overwritten.

In release `v2026.11.04.1`, the first data row (`initial`) contains the latest observed coordinates of all
configured axes when recording starts. Each device has its own port column.
Devices on the same controller show the same COM port. Simulators show
`simulated`. Disconnected devices retain their configured port.

Each `frame` row contains the zero-based MP4 frame number, the source camera
frame number, its capture time and the complete latest observed axis state.
Columns such as `stage_X_um` contain user coordinates (C2). Reference
coordinates (C1), device state, movement, limit, sample UTC and validity
follow the coordinate columns. Compare capture UTC with each axis sample UTC
to align asynchronous observations. Use only valid samples for training.

Between frames, `position` rows preserve observed changes, including a move
and return. The detail column identifies the changed axis. The final
`recording_stop` row includes written and dropped frame counts. `NA` means
a measurement or frame reference is unavailable. Zero is a measured value.
Values use invariant decimals and CSV quoting only where needed. Before the
first accepted frame, frame references are `NA`. The leading coordinate columns
always use C2 even when a device profile displays C1 in the motion workspace.

Earlier CSVs use a different schema. In `v2026.11.04`, initial positions and
changes are separate per-axis rows and frame rows omit coordinates. In
`v2026.11.03`, command rows describe host traffic. Neither format records
physical keypad input. Identify the format by the header before importing
recordings from multiple releases.

## Video display controls

Video controls are placed in a scrollable panel on the right. The default 100%
view maps one image pixel to one physical display pixel, including high-DPI
displays; scrollbars expose portions beyond the available viewport. **Fit to
window** scales the image to the available area. Mask coordinates remain in the
native image coordinate system in both modes.

The right sidebar groups View, Analysis, Acquisition, Recording and Test video.
Sensor mode, target frame rate (including 0 for automatic), horizontal/vertical
flips, output folder, connection and recording controls
have moved here from the main window. The main window keeps camera status and
an entry point to the workspace. Acquisition controls are disabled while reviewing
a file; switch back with Test video > Live camera.

Display zoom ranges from 10% to 400% using the slider, plus/minus buttons or
Ctrl + mouse wheel over the image. Native resets to 100%; Fit follows window
resizes. Settings scroll independently of the video. Source pixels and overlay
coordinates are preserved when zooming. The video workspace requests original
file dimensions from the Python decoder, bypassing the legacy 1280 x 960 preview
limit. Rendering a smaller view does not resize the source used for analysis.

Validation covers native pixel scaling at the active display DPI, zoom in, Fit,
unchanged source frames, normal/minimum-size window renders, model inference and
independent window closure. A 1920 x 1080 decoder check confirms native dimensions
while retaining the legacy scaled path for other callers.


The Exposure and Color tabs in the right sidebar below Recording show settings reported by the
connected camera. With KERN, the app reads exposure time and gain ranges from
the SDK. Turn off Auto exposure to edit exposure time in milliseconds and
analog gain in percent. Type an exact value and press Tab or use the slider.
Available color controls include white balance temperature and tint, hue,
saturation, brightness, contrast and gamma. Changes apply to the camera image,
including subsequently recorded frames. The tabs are disabled in test-video mode.
