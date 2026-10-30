# Cells, unresolved tissue and extraction events

This offline prototype starts from manually drafted seed masks. It does not yet
discover every cell automatically or add identities for previously unseen cells.
Seed JSON files under `tools/` are editable. Comments and code are English.

## Representation

- A visible individual cell keeps its object ID as its shape changes.
- Tissue whose internal boundaries are unclear is an `unresolved_group` with
  unknown cell count. A disconnected clump is not automatically labelled one cell.
- A separately tracked group envelope can replace individual displayed masks when
  cell tracks overlap, disappear or leave the group envelope.
- Separation events are candidates based on 2D projected masks, not evidence of
  physical detachment or successful extraction.

## Experiments

`tools/track_multicell_video.py` and `tools/multicell_video_cases.json` produced:

1. E-minus single extraction, source frames 599–820: 222 frames, one cell plus
   unresolved source mass. The sampled overlays follow the cell through extraction.
   The contact-chain audit marks source frame 644 as a separation candidate.
2. S multiple peripheral cells, frames 679–934: 256 frames, five tentative cell IDs
   plus unresolved mass. This first attempt fails after occlusion/defocus: tracks
   migrate onto neighbours and background. Its output is diagnostic evidence only.

`tools/analyze_cell_relations.py` checks projected connectivity through neighbouring
cell masks. A direct cell-to-mass distance alone can misclassify an attached chain.
The audit uses a six-pixel adjacency threshold and three consecutive separated
frames, requires prior contact and rejects incomplete contact graphs. These heuristic
thresholds have not been calibrated in micrometres. `contact_graph.json` supersedes
the direct-gap candidate events in v1 `tracks.json`.

## Conservative fallback experiment

`tools/hierarchical_video_case.json` retains three clearer cells and puts two blurred
attached cells into unresolved tissue. Object 7 independently tracks the envelope of
the three extracted cells. `tools/track_multicell_video.py --config
tools/hierarchical_video_case.json --output artifacts/multicell_video_v2` reruns this
case. Cell/group envelope masks are hierarchical and may overlap by design; the
envelope is excluded from competition among individual cell masks.

If a cell disappears, overlaps another cell by more than 25% of its mask or lies
more than 25% outside the envelope, the display latches to the unresolved envelope.
It does not automatically restore individual identities after loss; that needs a
corrective keyframe. This avoids pretending that identity survived an ambiguous
merge. The envelope is also a draft prediction and may fail under severe defocus.

Outputs contain side-by-side `review.mp4`, sampled `inspection.jpg`, per-ID binary
PNG masks and `tracks.json` with source video hash, original frame/time, seed masks,
states and candidate events. All masks have `training_eligible=false`; neighbouring
cells are not exhaustively annotated. These clips retain their original train split.

No live camera or motion-control integration has been made. Next work is corrective
keyframe entry, explicit appearance/disappearance and split/merge handling, and
fully labelled regions for an automatic cell proposal model. Review tracks against
the original video before adding them to YOLO training.

## Keyframe-corrected group experiment

The independent envelope in v2 also dropped cells during defocus; it is not a
successful autonomous fallback. V3 (`tools/corrected_hierarchical_video_case.json`)
adds manual group contours at source frames 731, 826, 849, 900 and 934, plus an empty
mask at 820 where the group is outside the field. The initial envelope covers the
whole selected cluster. Corrections are supplied before offline propagation, so
future keyframes may influence earlier predictions. This is assisted annotation,
not a real-time tracking benchmark.

The group association after leaving the field needs review; the reused group ID is
not proof of automatic reidentification. Counts remain unknown when individual
boundaries are ambiguous. V3 output is under `artifacts/multicell_video_v3` and must
remain excluded from training until reviewed. V1 and v2 are retained as failed-case
evidence, not additional independent data.

V3 completed with 256 decoded review-video frames and 1,280 per-object mask files
(including empty/hidden tracks). The envelope mask is empty at the manually marked
exit frame 820. The display switches to the group at source frame 745. Sampled
overlays improve after the corrective keyframes but still omit part of the cluster
between some corrections (e.g. around 784–805). This is not exhaustive cell
segmentation and not an autonomous tracking success. Review/correct those intervals
before exporting training labels. The single-extraction v1 clip is the stronger
demonstration; all initial masks and corrections are still manual prompts.

## Training-data staging and batch 4

`tools/video_annotation_batch4.json` adds 179 propagated frames: 130 frames of a
free cell (S, source frames 621–750), and 49 frames of two cells beside a mass
(E+, frames 1157–1205). Review videos and masks are in
`artifacts/video_annotation_batch4`. The second clip looks consistent on sampled
frames; the first has tracking gaps during focus changes from about frame 671.
Neither clip is approved ground truth.

`tools/prepare_video_training_data.py` stages four sequences into
`datasets/video_annotation_staging_v1`: 92 sparse keyframes and 287 exact binary
object masks. Original train splits and video provenance are retained. The
manifest flags known correction intervals and keeps every sample in review.
No incomplete full-frame YOLO labels are exported and no training is launched.

The user corrected initial IDs in the earlier multicell clip: ID 2 is a group of
2–3 cells, ID 4 is a group of two, and ID 3 is uncertain. Configurations and staged
metadata now reflect this. The staged corrected video updates display labels
only; later identity drift and incomplete geometry are not repaired by renaming.
Unknown objects have no trainable class ID. Counts describe the reviewed seed,
not every propagated frame. Historical output folders are preserved.
