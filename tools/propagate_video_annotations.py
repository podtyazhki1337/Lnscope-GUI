"""Create review-only SAM 2 mask tracks from existing seed polygons."""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path

import cv2
import numpy as np
import torch
from sam2.build_sam import build_sam2_video_predictor


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--frames", type=int, default=64)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = root / "artifacts/sam2_video_pilot_v1"
    output.mkdir(exist_ok=False)
    seed_root = root / "datasets/extraction_seed_v1"
    manifest = json.loads((seed_root / "manifest.json").read_text(encoding="utf-8-sig"))
    cases = [("cell", "cell_annotation_batch2.json", "26_S_260801150655-H-M-01_f000268.png", "peripheral_b"),
             ("spheroid", "spheroid_annotation_pilot.json", "19_Eminus_20260917_T_S_01_02_f000236.png", None)]
    checkpoint = root / ".cache/sam2.1_hiera_tiny.pt"
    predictor = build_sam2_video_predictor("configs/sam2.1/sam2.1_hiera_t.yaml", str(checkpoint),
                                          device="cuda", apply_postprocessing=False)
    overall = {}
    for task, annotation_file, image_name, object_name in cases:
        annotations = json.loads((root / "tools" / annotation_file).read_text())
        annotation = next(a for a in annotations["annotations"] if a["image"] == image_name and (object_name is None or a.get("id") == object_name))
        video, sample = next((v, s) for v in manifest["videos"] for s in v["samples"] if Path(s["image"]).name == image_name)
        folder = output / task
        frames = folder / "frames"
        frames.mkdir(parents=True)
        (folder / "review_masks").mkdir()
        cap = cv2.VideoCapture(video["source"])
        assert cap.isOpened()
        cap.set(cv2.CAP_PROP_POS_FRAMES, sample["frame"])
        times = []
        for index in range(args.frames):
            ok, frame = cap.read()
            if not ok:
                break
            if index == 0:
                reference = cv2.imread(str(seed_root / sample["image"]))
                assert np.max(np.abs(frame.astype(float)-reference.astype(float))) <= 2, "Seed frame mismatch"
                h, w = frame.shape[:2]
            assert cv2.imwrite(str(frames / f"{index:05d}.jpg"), frame, [cv2.IMWRITE_JPEG_QUALITY, 98])
            times.append({"source_frame": int(cap.get(cv2.CAP_PROP_POS_FRAMES))-1, "decoder_time_ms": cap.get(cv2.CAP_PROP_POS_MSEC)})
        cap.release()
        seed_mask = np.zeros((h, w), np.uint8)
        cv2.fillPoly(seed_mask, [np.array(annotation["polygon"], np.int32)], 1)
        cv2.imwrite(str(folder / "seed_mask.png"), seed_mask*255)
        writer = cv2.VideoWriter(str(folder / "review_raw.mp4"), cv2.VideoWriter_fourcc(*"mp4v"), video["nominal_fps"], (w*2,h))
        assert writer.isOpened()
        records, previews = [], []
        previous_area = None
        with torch.inference_mode(), torch.autocast("cuda", dtype=torch.bfloat16):
            state = predictor.init_state(str(frames), offload_video_to_cpu=True, offload_state_to_cpu=True)
            predictor.add_new_mask(state, frame_idx=0, obj_id=1, mask=seed_mask.astype(bool))
            for index, ids, logits in predictor.propagate_in_video(state):
                mask = (logits[0,0] > 0).cpu().numpy().astype(np.uint8)
                assert mask.shape == (h,w)
                cv2.imwrite(str(folder / "review_masks" / f"{index:05d}.png"), mask*255)
                area = int(mask.sum())
                count, _ = cv2.connectedComponents(mask)
                flags = []
                if area == 0:
                    flags.append("empty_mask")
                if previous_area and (area/previous_area < .5 or area/previous_area > 2):
                    flags.append("abrupt_area_change")
                if count > 2:
                    flags.append("multiple_components")
                previous_area = area
                original = cv2.imread(str(frames / f"{index:05d}.jpg"))
                overlay = original.copy()
                overlay[mask.astype(bool)] = (.55*overlay[mask.astype(bool)] + .45*np.array([40,220,60])).astype(np.uint8)
                cv2.putText(overlay, f"{task} ID 1 | frame {times[index]['source_frame']} | DRAFT", (10,25), cv2.FONT_HERSHEY_SIMPLEX,.55,(255,255,255),1)
                writer.write(np.hstack((original,overlay)))
                if index in {0, len(times)//3, 2*len(times)//3, len(times)-1}:
                    previews.append(cv2.resize(overlay,(480,360)))
                records.append({**times[index], "clip_frame": index, "object_id": int(ids[0]), "area_pixels": area,
                                "flags": flags, "review_status": "unreviewed_prediction", "training_eligible": False})
            predictor.reset_state(state)
            del state
        writer.release()
        cv2.imwrite(str(folder / "preview.jpg"), np.vstack([np.hstack(previews[:2]),np.hstack(previews[2:])]))
        ffmpeg = next((root / "GUI/ThirdParty").rglob("ffmpeg.exe"))
        subprocess.run([str(ffmpeg), "-y", "-i", str(folder / "review_raw.mp4"), "-c:v", "libx264", "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(folder / "review.mp4")], check=True, capture_output=True)
        result = {"source": video["source"], "source_sha256": video["source_sha256"], "source_split": video["split"],
                  "seed": annotation, "frames": records, "exhaustive_annotation": False, "training_eligible": False,
                  "checkpoint_sha256": hashlib.sha256(checkpoint.read_bytes()).hexdigest(),
                  "sam2_revision": subprocess.check_output(["git", "-C", str(root / ".cache/sam2"), "rev-parse", "HEAD"],text=True).strip()}
        (folder / "track.json").write_text(json.dumps(result,indent=2),encoding="utf-8")
        overall[task] = {"frames":len(records), "flagged_frames":sum(bool(r["flags"]) for r in records), "video":str(folder / "review.mp4")}
        (output / "summary.json").write_text(json.dumps(overall,indent=2),encoding="utf-8")
        torch.cuda.empty_cache()


if __name__ == "__main__":
    main()
