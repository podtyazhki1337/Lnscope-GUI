# Larger segmentation training set: November 2, 2026

The dataset is `datasets/yolo_training_large_v2`. The training run is
`artifacts/vision_training_large_v1`. Both directories are local and ignored by
Git. Scripts, review decisions, and this guide are versioned. The earlier
`yolo_training_large_v1` export is an intermediate audit without negatives;
it was not used for this training run.

## Contents

| Task / split | Images | Single cells | Cell groups | Spheroid masks | Negative images |
|---|---:|---:|---:|---:|---:|
| Cell / train | 365 | 294 | 59 | 0 | 12 |
| Cell / val | 189 | 150 | 34 | 0 | 7 |
| Cell / test | 81 | 29 | 48 | 0 | 5 |
| Spheroid / train | 118 | 0 | 0 | 106 | 12 |
| Spheroid / val | 81 | 0 | 0 | 74 | 7 |
| Spheroid / test | 37 | 0 | 0 | 32 | 5 |

There are 871 training/evaluation images and 826 annotated object instances:
473 single cells, 141 groups, and 212 spheroid masks. The images derive from
715 source frames in 29 videos. Object instances across adjacent frames are
correlated observations, not 826 independent biological objects. The same 24
reviewed negative crops are used in both independent tasks; they are not 48
independent source images.

## Review and export

All 85 propagated sequences have explicit per-sample visual decisions in
`tools/large_track_acceptance.json`. Of 953 accepted review masks, the exporter
excludes 30 already covered by baseline annotations, 83 cell crops intersecting
another tracked object, and 47 overlapping objects within eight source frames.
It adds the earlier reviewed baseline labels and negative crops from
`tools/large_negative_acceptance.json`. These are AI-reviewed assisted
annotations, not expert-certified ground truth.

`tools/prepare_large_training.py` preserves acquisition-date splits, checks
video/date separation and duplicate images, validates normalized polygons, and
requires at least 0.97 IoU when rasterizing a new polygon back against its exact
binary mask. Per-image provenance retains source video hashes, source frame,
crop, review index, and mask hashes. Exported masks are preserved alongside
labels. `summary.json`, `exclusions.json`, and per-task `provenance.json` record
the result. Cell-group overrides prevent known multi-cell objects from being
labelled as single cells.

Cell training uses reviewed local crops, not partially labelled full frames.
This avoids treating every unlabelled cell elsewhere in a frame as background.
Known neighboring masks intersecting a crop cause rejection. Spheroid training
uses full frames with the visible tissue mass annotated; detached cells belong
to the other task. Most masses extend beyond the image boundary.

Rebuild into a new, nonexistent output directory to preserve earlier exports:

```powershell
& .cache/yolo-clean/Scripts/python.exe tools/prepare_large_training.py --output datasets/yolo_training_large_v2
```

This requires the local seed batches, seed manifest, propagated video frames and
masks under `artifacts/large_video_annotations`, and stable review indices under
`datasets/large_track_review`. A Git clone alone does not contain those datasets.
The source videos must be provided separately. Do not infer acceptance for new
frames from an accepted seed; review them explicitly.

## Training

```powershell
& .cache/yolo-clean/Scripts/python.exe tools/train_vision_pilot.py --dataset datasets/yolo_training_large_v2 --runs artifacts/vision_training_large_v1 --epochs 150 --imgsz 512
```

This launches two sequential YOLO26n-seg jobs on CUDA: spheroid first, then cell.
The cell model has classes `0: visible_cell`, `1: cell_group`; the separate
spheroid model has `0: spheroid_mass`. Training uses pretrained weights,
512-pixel inputs, batch 4, AdamW, all layers unfrozen, seed 42, up to 150 epochs,
and early stopping after 30 epochs without improvement. Mosaic, mixup, and copy
paste are disabled. Validation drives model selection; the test split is not
used during training. Acquisition dates are isolated, but specimen independence
across dates is not established.

Logs: `.cache/vision-training-large-v1.log`. Checkpoints:
`artifacts/vision_training_large_v1/{spheroid,cell}/weights/best.pt` and `last.pt`.
Each job writes `results.csv`; `report.json` is updated after that job's training,
validation, and prediction complete. A checkpoint or an early epoch score does
not establish deployment readiness. Preserve the run directory when restarting;
use a new directory for a new experiment.

## GUI integration boundary

Training runs in the isolated ML environment; the published GUI's embedded
Python remains independent of PyTorch and Ultralytics. These new weights are not
yet wired into Offline tracking or camera capture.

Before integration, evaluate both models on held-out videos and actual KERN
frames, including empty scenes and pipette motion. Cell metrics on selected
crops do not measure full-frame cell discovery. Tiling/ROI selection and pixel
scale must be validated before using the cell model on live full frames. Group
segmentation does not recover individual cell boundaries inside a group.

The future Python inference adapter should return masks, class IDs, scores,
frame timestamps, and image coordinates to the C# UI. Tracking must handle lost
objects, deformation, splits, and merges; a segmentation model alone does not
provide reliable persistent identities. Benchmark latency including capture,
preprocessing, inference, IPC, and rendering. Neither model supplies Z, contact,
suction success, or collision-safe motion decisions.
