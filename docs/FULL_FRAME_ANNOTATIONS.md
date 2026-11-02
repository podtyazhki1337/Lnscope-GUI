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

## SAM2 assistance and explicit classes

The native-frame SAM2 run is complete: 798 frames, 6,837 geometric proposals.
956 proposals overlap an existing reviewed mask sufficiently to suggest its
class. Suggestions are not accepted labels. SAM2 can merge neighboring cells,
include the pipette, or return an inner cell region rather than its outer edge.

Class IDs are fixed across the native-frame workflow:

| ID | Class | Meaning |
| --- | --- | --- |
| 0 | `cell` | An individually resolved cell, using its visible outer boundary |
| 1 | `cell_group` | A small cluster whose individual boundaries cannot be reliably separated |
| 2 | `spheroid` | Unresolved bulk tissue |

Background, pipette, and duplicate/nested proposals are rejected. Individually
resolved cells at a tissue edge are separated from the mass mask. A cropped
object at the image border is labelled only over its visible extent.

`tools/full_frame_class_review.json` records selected source masks, classes,
rejections, repair prompts, and completeness decisions. The original restored
dataset and automatic proposals remain unchanged. The classified output is
`datasets/full_frame_classified_v1`; it currently contains 31 complete native
frames and 39 objects (34 cells, 2 cell groups, 3 spheroids). This small reviewed
subset is **not** the completed 798-frame training set. Most frames still need
review, including additional validation/test videos and difficult extractions.

Reproduce proposals, render review sheets, generate targeted repairs, and apply
explicit decisions with the training Python environment:

```powershell
& .cache/yolo-clean/Scripts/python.exe tools/propose_large_annotations.py --full-frames --limit 798 --output datasets/full_frame_sam2_proposals_v1
& .cache/yolo-clean/Scripts/python.exe tools/review_full_frame_annotations.py --render 01_
& .cache/yolo-clean/Scripts/python.exe tools/review_full_frame_annotations.py --prompt --apply
```

Review sheets contain enlarged crops only to inspect proposal boundaries; saved
images and masks remain 640 x 480. Full-frame overlays are in the classified
output's `review` directory. Repair prompts use native pixel coordinates. Optional
largest-component cleanup removes detached mask speckles; explicit subtraction
separates edge cells from a tissue mask. Each output records its source-mask hash
and the exact decision. Mark `annotation_complete` only after inspecting the
complete image and final overlay. Applying decisions rejects incomplete candidate
accounting or unresolved issues on a frame marked complete. No new full-frame
YOLO training has been launched from these partial review results.
