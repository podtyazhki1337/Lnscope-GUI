# Offline YOLO pilot

For the subsequent crop training run, see
[LARGE_VISION_DATASET.md](LARGE_VISION_DATASET.md). The small datasets and run
results below are retained as historical pilot records.

This is an offline training experiment, not a live motion controller. The GUI and
embedded application Python do not depend on this training environment.

## Reproduce

Use the isolated `.cache/yolo-clean/Scripts/python.exe` environment. Exact installed
packages are recorded in `tools/vision-pilot-requirements.lock.txt`. Install the CUDA
builds of torch and torchvision using the official PyTorch cu128 index; do not run
multiple pip installers concurrently in the same environment.

```powershell
& .cache/yolo-clean/Scripts/python.exe -X utf8 tools/train_vision_pilot.py --prepare-only
& .cache/yolo-clean/Scripts/python.exe -X utf8 tools/train_vision_pilot.py
& .cache/yolo-clean/Scripts/python.exe -X utf8 tools/preview_vision_pilot.py
```

The dataset builder uses an explicit batch allowlist, including the corrected cell
batch instead of its superseded version. Partial annotations and unresolved review
examples are excluded. Native video splits are retained, with duplicate crop and
video split checks. The source manifest grouped acquisition dates into splits; this
does not prove biological specimen independence across dates.

## Scope

Two independent YOLO26n-seg models use pretrained weights, frozen initial layers,
512-pixel input, batch 2, up to 40 epochs and validation-based early stopping. Each
task has class index 0, but different meanings: `spheroid_mass` and `visible_cell`.
Never combine their label files without explicit class mapping.

Training data are only 9 mass images and 8 isolated cell crops. Validation has 5
mass images and 4 cell crops (6 cells). Test has 2 images per task (2 masses, 3 cells).
These metrics are a pipeline check, not a stable generalization estimate. All
spheroid examples are clipped by image borders. Cell evaluation uses selected crops;
it does not measure full-frame cell search or reliable separation inside tissue.

The new boundary between the two elongated cells remains outside training until
reviewed. Accepted contours in partially annotated images remain excluded because
unlabelled neighbours would be interpreted as background.

Outputs are under `artifacts/vision_pilot_v1`: per-run `weights/best.pt`, training
curves, test predictions, `report.json` and `test_comparison.jpg`. Training logs are
in `.cache/yolo-training.log`. Repeated training uses new run directories.

Next validation should add fully annotated peripheral-cell regions, background
examples and videos from the actual live camera. These models do not estimate depth,
pipette contact, suction success or a safe trajectory.

## First run outcome (2026-10-29)

Both jobs completed on the RTX 4060 Laptop GPU. Validation early stopping ended
spheroid training after 13 epochs and cell training after 21 epochs. At confidence
0.25, neither model detected any object on either of its two test images.
Test mask AP50 was 0.1654 for spheroid and 0.0145 for cell; these scores use a
different confidence sweep from the displayed predictions and do not indicate
usable performance. Do not deploy these weights for motion decisions.

The run verifies data ingestion, GPU training, checkpoint saving and offline
prediction. It does not establish whether data scarcity, training configuration or
both caused the failure. Before another comparison, verify the model can overfit a
small training-only subset, then expand the fully labelled dataset. Do not tune to
these four test images; reserve additional independent videos for final evaluation.

## Training-only memorization diagnostic

Run `tools/diagnose_vision_overfit.py` in the same isolated environment. It selects
two existing training images for each task, copies them into an explicitly named
diagnostic dataset, and deliberately uses those same images for validation. No
held-out images are read. Outputs go to `artifacts/vision_overfit_v1`; an existing
output directory is rejected to preserve results.

This diagnostic uses 120 epochs, no augmentation, no frozen backbone, full precision,
AdamW at 0.001, no warmup and `batch=2, nbs=2`. The initial pilot used `nbs=64`, which
with batch 2 gave nominal gradient accumulation of 32 mini-batches. This is especially
poorly matched to a dataset with only four or five mini-batches per epoch and early
stopping after 13 or 21 epochs. Several settings changed in the diagnostic, so this
comparison cannot isolate the contribution of accumulation alone.

The diagnostic passes only if each training image produces exactly one detection at
confidence 0.25 and its mask IoU with the hand-drawn polygon is at least 0.8. IoU is
computed at the original crop resolution. This measures memorization, not biological
accuracy or generalization. Do not treat these models or metrics as deployment-ready.

Both tasks passed the 120-epoch diagnostic. Reloaded cell checkpoints produced one
object per image with mask IoU 0.9786 and 0.9754. Spheroid checkpoints produced one
object per image with IoU 0.9887 and 0.9925. The rendered comparison was visually
inspected. This confirms that these four labels, the training pipeline and mask
inference can work together; it does not validate all annotations or unseen frames.
Report and preview: `artifacts/vision_overfit_v1/report.json` and `comparison.jpg`.

## Full small-data retraining

The current default `train_vision_pilot.py` preset is `small-data`: 120 epochs maximum,
384-pixel input, batch 4 / nbs 4, no frozen backbone, no warmup, AdamW 0.001, full
precision, modest augmentation and validation early stopping. Outputs are isolated
under `artifacts/vision_pilot_v2`. Evaluation defaults to `val`; test images were not
used in this retraining evaluation. To reproduce the original settings explicitly,
use `--preset initial --epochs 40 --eval-split test` in a separate output location.
Completed task reports are protected against accidental overwrite.

Validation mask AP50/AP50-95: spheroid 0.995/0.945 on five images; cell 0.6284/0.3679
on four crops containing six cells. These validation images also selected the best
checkpoint, so these are development metrics, not an independent final test. At
confidence 0.25, spheroid image counts were 1,2,1,1,1 (one duplicate prediction).
Cell image counts were 2,3,1,1 against 1,2,2,1 labelled objects: duplicates and merged
cells remain. Visual comparisons are saved per task under the v2 output directory.

## SAM 2 video annotation pilot

`tools/propagate_video_annotations.py` uses the official SAM 2.1 tiny checkpoint and
the pinned repository revision recorded in each output `track.json`. Installation
uses `.cache/sam2` in the isolated training environment with `SAM2_BUILD_CUDA=0`;
custom CUDA postprocessing is disabled, while neural inference still runs on CUDA.
Reference: https://github.com/facebookresearch/sam2

The script starts from an existing polygon in a verified decoded seed frame. It
propagates one selected object over 64 consecutive frames in each of two training
videos: a peripheral cell (S, frames 268–331) and spheroid mass (E-minus, 236–299).
These are approximately 22-second clips at the source encoded frame rate. They are
not live-camera performance measurements and not newly independent training samples.

Outputs: `artifacts/sam2_video_pilot_v1/{cell,spheroid}/review.mp4` shows original and
overlay side by side; `review_masks` holds full-resolution binary PNG masks;
`track.json` preserves object ID, source frame/time, split, seed, source/checkpoint
hashes and review status. All propagated masks have `training_eligible=false`.
Only one selected cell is tracked, so neighbouring cells are not exhaustively labelled.
No automatic YOLO-label export or motion commands are performed.

`tools/audit_video_masks.py` verifies that each playable MP4 has all 64 frames,
summarizes mask components and creates an inspection sheet (every fourth frame plus
the final frame). Both tracks had 64 nonempty masks and no multiple significant
components at the heuristic cutoff max(25 pixels, 0.5% of mask area). Raw warnings
occurred on 4 cell and 39 spheroid frames, mainly due to tiny components. The cutoff
is a review aid, not an accuracy measure. Sampled overlays were visually inspected;
there are no independent per-frame ground-truth masks or identity-switch metrics.

To expand this workflow, add corrective keyframes at defocus, deformation, overlap,
detachment and object disappearance. Preserve object identity through shape changes;
record actual splitting/merging separately. Validate propagated masks before using
them as labels, and retain the original video split for every derived frame.
