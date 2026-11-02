# Offline tracking prototype

Open **Camera → Offline tracking · video**. This mode receives only a local
video, has no COM transport, and sends no motion commands. Main-window motion
hotkeys are disabled while the analysis window is open.

1. **Open video**: select the original `showcase/20260803-H-M.mp4`, not the edited showcase montage.
2. Enter a time, for example `210`, and click **Seek / clear points**. Seeking clears all markers.
3. Select **Pipette tip** and click the tip; then select **Target** and click a visible cell or spheroid landmark. Coordinates are recorded in the image, not on the controller.
4. **Next frame** or **Play** starts tracking. Weak or ambiguous matches turn red and pause Play. Reselect the lost point to continue. There is no automatic recovery.
5. **Export CSV** saves the last 10,000 records: frame, decoder time, coordinates, match scores, loss flag, and displacement. Displacement is not computed when either point is lost. Red coordinates are the last accepted position, not a new estimate.

`tracking.py` contains the algorithm and thresholds; `vision.py` handles decoding,
state, logging, and displacement calculation. OpenCV 4.12.0.88 and NumPy 2.2.6
are bundled with embedded Python. No GPU or separate Python installation is
required. Bundled licenses are in `ThirdParty/python/Lib/site-packages/*dist-info`
and `cv2`.

The method uses a fixed 31×31 pixel template and local normalized OpenCV template
matching. Search is limited to the neighborhood of the previous position;
match strength and ambiguity are checked. The match score is not the probability
of selecting the correct cell. The algorithm does not segment cells, establish
contact, or reconstruct Z. Similar cells, deformation, and occlusion can still
cause incorrect tracking even with a high score. Trajectories need manual review.

Frames larger than 1280×960 are resized proportionally; scale is logged in CSV
and displayed in the window. Coordinates refer to the processed image (original
coordinates = coordinates / scale). Playback is sequential without skipped
frames. Its pace follows the file's nominal frame rate and may be slower than
the source. For measurements, use CSV frame times rather than window playback time.

## Showcase verification

Source: 640×480, seek to 210 s (actual frame time about 209.957 s), manually
selected landmarks tip=(335,194), target=(409,153). On the 12th subsequent frame
(214.16 s), the algorithm declared the tip lost: score 0.723 was below the 0.78
threshold, and displacement calculation was disabled. This is a diagnostic
example, not an accuracy measurement: reference manual trajectories have not
yet been annotated. The observed median processing time for two templates was
about 0.49 ms; it excludes decoding, IPC, and rendering and is not an estimate
of application FPS.

## Next steps for the installation

Manually focus the tip and record manipulator/platform positions, then select
the target and record its position and platform focus Z. These records must
include both coordinate systems, camera scale, axis directions, and offsets
between them. Only after calibration can a machine trajectory with safe
clearance be computed and checked in preview mode first. Stored hardware slots
alone do not constitute calibration.

Reference: https://docs.opencv.org/4.x/d4/dc6/tutorial_py_template_matching.html
