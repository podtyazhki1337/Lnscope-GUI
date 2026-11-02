# Restoring annotations to native frames

`tools/restore_full_frame_annotations.py` restores the existing reviewed labels
to the original image coordinates. It does not enlarge a crop to fill a frame.
Propagated binary masks are copied at native resolution; earlier crop-normalized
polygons are multiplied by crop width/height and translated by the crop origin.

Output: `datasets/full_frame_annotations_v1`, ignored by Git.

- 798 original 640×480 frames.
- 955 object masks: 561 cells, 172 cell groups, 222 spheroid masses.
- 31 overlapping duplicate masks merged, with all provenance retained.
- No conflicting class assignments found among duplicate masks.
- Original video/acquisition-date train/val/test assignments preserved.

The pixel audit verified original masks against restored masks. Additional
provenance entries on merged duplicates are alternate accepted masks of the same
object, with IoU above the merge threshold, not necessarily identical pixels.

`images/{split}` contains native images without contours, `masks/{split}` stores
per-object binary masks, and `review` contains full-size overlays and numbered
contact sheets. `manifest.json` records source frame/video hashes, image sizes,
classes, provenance, and completion flags. `summary.json` records counts and
conflicts. Rebuild into a nonexistent directory:

```powershell
& .cache/yolo-clean/Scripts/python.exe tools/restore_full_frame_annotations.py --output datasets/full_frame_annotations_v1
```

## Completeness gate

Restoration does not annotate missing neighbors. The first full-frame review
already showed additional visible cells/groups and tissue outside previously
selected crops. All restored frames therefore remain `annotation_complete=false`
and `training_eligible=false`. No training YAML is generated at this stage.

Before training, review the entire frame, add missing single cells and groups,
and label unresolved tissue as spheroid mass according to the agreed annotation
policy. Check group/single-cell boundaries, occlusion, pipette overlap, and blur.
Do not count partially labelled neighbors as background. Mark a frame complete
only after that review; retain incomplete frames separately. Full-frame training
must preserve the original scale distribution and video/date split isolation.
