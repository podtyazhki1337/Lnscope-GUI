"""Generate resumable SAM proposals for visual review, never automatic ground truth."""
import argparse
import json
from pathlib import Path
import time

import cv2
import numpy as np
import torch
from sam2.build_sam import build_sam2
from sam2.automatic_mask_generator import SAM2AutomaticMaskGenerator


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--limit", type=int, default=232)
    parser.add_argument("--output", default="datasets/large_annotation_proposals")
    parser.add_argument("--iou", type=float, default=.45)
    parser.add_argument("--stability", type=float, default=.65)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    source = root / "datasets/extraction_seed_v1"
    output = root / args.output
    output.mkdir(exist_ok=True)
    manifest = json.loads((source / "manifest.json").read_text(encoding="utf-8-sig"))
    model = build_sam2("configs/sam2.1/sam2.1_hiera_t.yaml", str(root / ".cache/sam2.1_hiera_tiny.pt"), device="cuda", apply_postprocessing=False)
    generator = SAM2AutomaticMaskGenerator(model, points_per_side=24, points_per_batch=48,
        pred_iou_thresh=args.iou, stability_score_thresh=args.stability, box_nms_thresh=.65)
    samples = [(v, s) for v in manifest["videos"] for s in v["samples"]][:args.limit]
    for index, (video, sample) in enumerate(samples):
        folder = output / Path(sample["image"]).stem
        if (folder / "proposals.json").exists():
            continue
        folder.mkdir(exist_ok=True)
        image = cv2.imread(str(source / sample["image"]))
        start = time.monotonic()
        with torch.inference_mode(), torch.autocast("cuda", dtype=torch.bfloat16):
            proposals = generator.generate(cv2.cvtColor(image, cv2.COLOR_BGR2RGB))
        records = []
        for proposal in sorted(proposals, key=lambda p: (p["bbox"][1], p["bbox"][0])):
            x, y, w, h = [int(v) for v in proposal["bbox"]]
            area = int(proposal["area"])
            # Keep both possible cells and masses; classification requires review.
            if area < 180 or min(w, h) < 12 or area > image.shape[0]*image.shape[1]*.95:
                continue
            mask = proposal["segmentation"].astype(np.uint8)*255
            contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
            if not contours:
                continue
            largest = max(contours, key=cv2.contourArea)
            if cv2.contourArea(largest) < area*.92:
                continue
            object_id = len(records)
            cv2.imwrite(str(folder / f"{object_id:03d}.png"), mask)
            records.append(dict(id=object_id, bbox=[x,y,w,h], area=area,
                predicted_iou=float(proposal["predicted_iou"]), stability=float(proposal["stability_score"])))
        (folder / "proposals.json").write_text(json.dumps(dict(image=sample["image"],
            source_video=video["source"], source_sha256=video["source_sha256"], split=video["split"],
            source_frame=sample["frame"], status="unreviewed", objects=records), indent=2))
        print(f"{index+1}/{len(samples)} {folder.name}: {len(records)} proposals, {time.monotonic()-start:.1f}s", flush=True)


if __name__ == "__main__":
    main()
