"""Stage sparse video keyframes and exact masks without treating partial labels as background."""
import argparse
import json
import shutil
import subprocess
from pathlib import Path

import cv2
import numpy as np


def main():
    root=Path(__file__).resolve().parents[1]
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output",type=Path,default=root / "datasets/video_annotation_staging_v1")
    parser.add_argument("--sources",type=Path,nargs="+")
    args=parser.parse_args()
    output=args.output.resolve()
    output.mkdir(exist_ok=False)
    sources=[root / "artifacts/multicell_video_v1/single_extraction",
             root / "artifacts/multicell_video_v3/corrected_cell_group",
             root / "artifacts/video_annotation_batch4/free_cell_exit",
             root / "artifacts/video_annotation_batch4/two_cells_near_mass"]
    if args.sources:
        sources=[p.resolve() for p in args.sources]
    corrected=json.loads((root / "tools/corrected_hierarchical_video_case.json").read_text())["cases"][0]
    all_records=[]
    summaries=[]
    for source in sources:
        track=json.loads((source / "tracks.json").read_text())
        semantic_correction=source.name=="corrected_cell_group"
        if semantic_correction:
            track["seeds"]=corrected
            track["semantic_review"]=corrected["semantic_review"]
        objects={o["id"]:o for o in track["seeds"]["objects"]}
        for frame in track["frames"]:
            for obj in frame["objects"]:
                seed=objects[obj["id"]]
                obj["kind"]=seed["kind"]
                obj["seed_cell_count"]=seed.get("seed_cell_count")
                obj["training_eligible"]=False
        target=output / "review" / track["split"] / source.name
        (target / "images").mkdir(parents=True)
        (target / "masks").mkdir()
        corrections={c["source_frame"] for c in track["seeds"].get("corrections",[])}
        indices=sorted(set(range(0,len(track["frames"]),8)) | {len(track["frames"])-1} | {i for i,f in enumerate(track["frames"]) if f["source_frame"] in corrections})
        for index in indices:
            frame=track["frames"][index]
            review_notes=[]
            if source.name=="free_cell_exit" and frame["source_frame"]>=671:
                review_notes.append("Focus change and tracking gaps: verify identity and redraw before export.")
            if semantic_correction:
                review_notes.append("Seed counts corrected by user; later geometry and identity remain unverified.")
                if 784<=frame["source_frame"]<=805:
                    review_notes.append("Known incomplete group mask: redraw the missing region.")
            name=f"{frame['source_frame']:06d}"
            shutil.copy2(source / "frames" / f"{index:05d}.jpg",target / "images" / (name+".jpg"))
            mask_records=[]
            for obj in frame["objects"]:
                mask_name=f"{name}_id{obj['id']}.png"
                shutil.copy2(source / "review_masks" / str(obj["id"]) / f"{index:05d}.png",target / "masks" / mask_name)
                mask_records.append({**obj,"mask":str((target / "masks" / mask_name).relative_to(output))})
            all_records.append({"sequence":source.name,"source_video":track["source"],"source_sha256":track["source_sha256"],
                                "split":track["split"],"frame":frame["source_frame"],"time_ms":frame["decoder_time_ms"],
                                "image":str((target / "images" / (name+".jpg")).relative_to(output)),"objects":mask_records,
                                "training_eligible":False,"exhaustive_annotation":False,"review_status":"needs_correction" if review_notes else "pending_frame_review",
                                "review_notes":review_notes})
        (target / "track_metadata.json").write_text(json.dumps(track,indent=2),encoding="utf-8")
        summaries.append({"sequence":source.name,"video_frames":len(track["frames"]),"staged_keyframes":len(indices),"split":track["split"]})
        if semantic_correction:
            writer=None
            colors=[(70,180,230),(70,230,70),(230,140,50),(190,70,230)]
            for i,frame_record in enumerate(track["frames"]):
                image=cv2.imread(str(source / "frames" / f"{i:05d}.jpg"))
                overlay=image.copy()
                h,w=image.shape[:2]
                if writer is None:
                    cap=cv2.VideoCapture(str(source / "review.mp4"));fps=cap.get(cv2.CAP_PROP_FPS);cap.release()
                    writer=cv2.VideoWriter(str(target / "review_raw.mp4"),cv2.VideoWriter_fourcc(*"mp4v"),fps,(w*2,h))
                    assert writer.isOpened()
                fallback=frame_record.get("fallback_to_group",False)
                for j,(object_id,o) in enumerate(objects.items()):
                    if (object_id==7 and not fallback) or (object_id in (2,3,4) and fallback): continue
                    mask=cv2.imread(str(source / "review_masks" / str(object_id) / f"{i:05d}.png"),0)>0
                    if mask.sum()<25: continue
                    overlay[mask]=(.62*overlay[mask]+.38*np.array(colors[j%len(colors)])).astype(np.uint8)
                    yy,xx=np.nonzero(mask)
                    label=o.get("display_label","group: count unknown")
                    cv2.putText(overlay,f'{object_id}: {label}',(max(0,min(w-220,int(xx.mean()))),max(45,int(yy.mean()))),0,.42,(255,255,255),1)
                cv2.putText(overlay,f"Frame {frame_record['source_frame']} | DRAFT / counts only at seed",(8,22),0,.48,(255,255,255),1)
                writer.write(np.hstack((image,overlay)))
            writer.release()
            ffmpeg=root / "GUI/ThirdParty/ffmpeg/ffmpeg.exe"
            subprocess.run([str(ffmpeg),"-y","-i",str(target / "review_raw.mp4"),"-c:v","libx264","-crf","20","-pix_fmt","yuv420p","-movflags","+faststart",str(target / "review.mp4")],check=True,capture_output=True)
    metadata={"schema":1,"classes":{"cell":0,"cell_group":1,"unresolved_group":2,"uncertain_object":None},
              "status":"review_staging_not_training_ready","sequences":summaries,"frames":all_records}
    (output / "manifest.json").write_text(json.dumps(metadata,indent=2),encoding="utf-8")
    (output / "README.md").write_text("""# Video training-data staging

Review-only sparse keyframes, per-object PNG masks and original video/frame provenance.
No training was launched. No incomplete full-frame YOLO labels were generated.
All propagated labels remain training_eligible=false until per-frame geometry,
class and annotation completeness are checked. Adjacent frames are correlated.

User correction for the earlier multicell sequence: ID 4 is a two-cell group;
ID 2 is a group of two to three cells; ID 3 is uncertain. Those counts apply only
to the reviewed initial interval, not to later drift or loss of identity.
The corrected review.mp4 changes labels only; it does not repair mask geometry.
Unknown objects have no trainable class ID. Groups must not be exported as cells.

Every eighth frame plus the final and manual correction frames is staged. Source
splits are retained. Separate IDs are stored in separate binary PNGs, including
empty masks; an empty tracked mask does not imply an object-free full frame.
The previous isolated-cell and mass training datasets are unchanged.
""",encoding="utf-8")
    print(json.dumps({"keyframes":len(all_records),"mask_files":sum(len(f["objects"]) for f in all_records),"sequences":summaries}))


if __name__=="__main__":
    main()
