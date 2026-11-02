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

The native-frame annotation restoration is documented in
[FULL_FRAME_ANNOTATIONS.md](FULL_FRAME_ANNOTATIONS.md). Its completeness review
must precede retraining; these restored frames are not silently substituted
into the existing model.

## Coordinate sidecar

Every Record operation creates `name.mp4` and `name.csv` in the same folder.
Existing video or CSV files cause a new unique filename; neither is overwritten.
CSV is UTF-8, comma-delimited, quoted, with invariant decimal points and UTC
timestamps. Rows are written continuously and flushed during recording.

Each frame successfully passed to the encoder gets one row per configured axis:

| Column | Meaning |
|---|---|
| `video_frame` | Zero-based index in the constant-rate encoded stream |
| `camera_frame` | Camera-provided frame number; gaps can reveal dropped frames |
| `capture_utc`, `capture_elapsed_ms` | Camera frame timestamp and elapsed capture time from the first written frame |
| `video_time_ms` | Encoded-frame index divided by the recording FPS |
| `device`, `axis`, `unit`, `state` | Configured device/axis and connection state |
| `c1`, `c2` | Reference and user coordinates from existing controller feedback |
| `moving`, `limit` | Last reported motion and limit state |
| `position_sample_utc` | Timestamp supplied with the position sample |
| `position_age_ms` | Monotonic age of that feedback at frame queueing |
| `valid` | Finite coordinates received within 3 seconds from a Ready device |

Disconnected or never-read axes have empty coordinate fields and `valid=False`;
they are not written as zero. Stale samples retain their values and age with
`valid=False`. With no positioners configured, a `no_positioners` row still
preserves frame timing. Logging reads a thread-safe feedback cache, adds no COM
commands, and does not wait for a fresh serial response on each camera frame.
Axis samples are asynchronous; the log does not claim simultaneous hardware
measurements or interpolate positions between samples.

Normal stop, Stop live, mode changes, disconnect, and application shutdown
finalize both files. Encoder/log errors are reported as failures, not successful
saves. A failed or interrupted recording may contain only a partial video/log.

## Video display controls

Video controls are placed in a scrollable panel on the right. The default 100%
view maps one image pixel to one physical display pixel, including high-DPI
displays; scrollbars expose portions beyond the available viewport. **Fit to
window** scales the image to the available area. Mask coordinates remain in the
native image coordinate system in both modes.

The right sidebar groups View, Analysis, Acquisition, Recording and Test video.
Sensor mode, target frame rate (including 0 for automatic), horizontal/vertical
flips, device image properties, output folder, connection and recording controls
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
