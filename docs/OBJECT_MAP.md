# Object observations and return navigation

Historical standalone window: its main-window button has been removed. Use the unified camera/video window described in [VIDEO_ANALYSIS_AND_LOGGING.md](VIDEO_ANALYSIS_AND_LOGGING.md).

In the former interface, the Object map button opened a Python-backed observation bookmark editor. Connect
positioners, stop motion, name the object, choose its type, and save the current
view. Each axis is explicitly read and stored with C1 reference coordinates,
display coordinates, units and host receipt time. These sequential reads are not
a synchronized camera exposure or an exact object XYZ measurement. The window
has no movement or automatic return action.

Maps are saved in `%LOCALAPPDATA%/LNscope/object-map.json`; JSON export/import is
available. Import replaces the current map after a confirmation, validates the
file before replacement, and marks observations as needing reacquisition.
The sample-moved button marks all observations uncertain. Existing controller
position slots are independent. C1 is not assumed stable across controller
reinitialization, reference reset, changed wiring or sample replacement.

`GUI/PythonBackend/object_map.py` owns persistence and coordinate validation.
Its `project_xy` function uses two explicitly calibrated matrices: pixel offset
to sample displacement, and stage coordinates to sample displacement. Tests
verify that compensated stage motion leaves a stationary object's location
unchanged, while actual cell displacement changes its mapped position. No
calibration numbers are guessed. Z remains an observed platform/focus coordinate.

Remaining integration work:

1. Use the [recorded frame and axis timestamps](VIDEO_ANALYSIS_AND_LOGGING.md)
   and configured ports. Add calibration identity and measure pixel scale,
   rotation, signs and stage backlash.
2. Integrate reviewed YOLO segmentation with persistent object IDs and selected
   targets. Record mask, appearance, image location and last-seen time.
3. Update visible objects after pipette contact; grow uncertainty while unseen.
   Separate/merged masks require explicit identity handling.
4. Propose return to the last observed region, then reacquire visually. Similar
   cells may produce multiple candidates; proximity alone cannot confirm identity.
5. Execute return only through the existing motion interlocks with a calibrated
   pipette clearance path. Do not infer a safe path from the saved coordinates.

No training or real-device movement is performed by the object-map tests.
