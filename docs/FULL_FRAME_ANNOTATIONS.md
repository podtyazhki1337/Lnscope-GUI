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
`datasets/full_frame_classified_v1`; it currently contains 56 reviewed native
frames and 101 objects (74 cells, 9 cell groups, 18 spheroids). 43 frames are
complete; 13 remain ineligible for training because of out-of-focus structures,
pipette occlusion, or an ambiguous tissue attachment boundary. The latest review
added 21 frames, including 12 complete validation frames. Resolved pairs are
labelled as separate cells with non-overlapping masks; blurred pairs retain a
group label. Tissue masks exclude separately resolved cells. This small reviewed
subset is **not** the completed 798-frame training set. Most frames still need
review, including additional training/test videos and difficult extractions.

Reproduce proposals, render review sheets, generate targeted repairs, and apply
explicit decisions with the training Python environment:

```powershell
& .cache/yolo-clean/Scripts/python.exe tools/propose_large_annotations.py --full-frames --limit 798 --output datasets/full_frame_sam2_proposals_v1
& .cache/yolo-clean/Scripts/python.exe tools/review_full_frame_annotations.py --render 01_
& .cache/yolo-clean/Scripts/python.exe tools/review_full_frame_annotations.py --prompt --apply
& .cache/yolo-clean/Scripts/python.exe tools/review_full_frame_annotations.py --pages 29_
```

Review sheets contain enlarged crops only to inspect proposal boundaries; saved
images and masks remain 640 x 480. Full-frame overlays are in the classified
output's `review` directory. Repair prompts use native pixel coordinates. Optional
largest-component cleanup removes detached mask speckles; explicit subtraction
separates edge cells from a tissue mask. Each output records its source-mask hash
and the exact decision. Mark `annotation_complete` only after inspecting the
complete image and final overlay. Applying decisions rejects incomplete candidate
accounting or unresolved issues on a frame marked complete. This review produced candidate annotations. The later human-reviewed export
and bootstrap training run are described below.

Native review pages tile four final overlays without resizing their pixels. Each
frame retains its source identifier above the image. The November 3 review fixed
edge-cell masks across the validation sequence 29, excluded the pipette, and
separated clear pairs in sequence 28. Frame 29/288 and the strongly occluded
29/4148 and 29/4156 remain pending. The ten reviewed sequence-28 frames also remain
pending because unclassified defocused structures must not become background
labels accidentally. Completeness decisions apply to the entire frame, not just
the central target.

## Human review in X-AnyLabeling (from November 3, 2026)

Review moved from AI-written SAM2 prompts to human review in X-AnyLabeling.
`tools/xanylabeling_roundtrip.py export` writes `datasets/xanylabeling_review/<video>/`
with native PNG frames and polygons prefilled from the classified review or the
restored masks; every frame starts unchecked. Open a video with
`datasets/xanylabeling_review/review.cmd <video folder>`. SAM2.1 runs inside
X-AnyLabeling for missing objects (Q positive point, E negative point, F finish).

`tools/xanylabeling_roundtrip.py import` writes `datasets/full_frame_human_v1`.
Only frames flagged `checked` and not `exclude` are training eligible. Circles and
rectangles are rasterized; cell and cell_group masks are cut out of spheroid masks.

Agreed rules:

- `cell`: boundary of an individual cell is visible, even when it touches others.
- `cell_group`: only when boundaries inside the cluster cannot be distinguished.
- `spheroid`: unresolved bulk tissue without the separately labelled cells.
- Out-of-focus or unidentifiable blobs are not labelled; they are background.
- Pipette is not labelled. Objects cut by the frame edge are labelled over the visible part.
- A frame with any unlabelled, clearly visible cell must not be marked `checked`.


## Full-frame training and review prefill

After importing checked frames, use `tools/full_frame_yolo.py export` with a new
output directory. It exports native images and YOLO polygons for three classes
in order: cell, cell_group and spheroid. The exporter uses external contours,
so interior holes in raster masks are not represented in the polygon labels.
The `train` subcommand accepts the exported `data.yaml` and a required run name.

The local `v0_bootstrap` run used 25 training frames and six validation frames.
Its export used `--bootstrap-val` to hold out frames because no checked validation
video was available. These scores cannot establish generalization to unseen videos.
The `prefill` subcommand can add proposals to untouched review files. It leaves
checked and excluded frames alone and does not mark proposals as reviewed.
The portable GUI still loads the two ONNX files listed in its model manifest.
