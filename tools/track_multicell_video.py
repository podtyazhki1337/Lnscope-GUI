"""Track visible cells and unresolved tissue groups in offline review clips."""
import argparse
import json
import subprocess
from pathlib import Path

import cv2
import numpy as np
import torch
from sam2.build_sam import build_sam2_video_predictor


def main():
    root = Path(__file__).resolve().parents[1]
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config",type=Path,default=root / "tools/multicell_video_cases.json")
    parser.add_argument("--output",type=Path,default=root / "artifacts/multicell_video_v1")
    parser.add_argument("--resume",action="store_true")
    args=parser.parse_args()
    output = args.output.resolve()
    output.mkdir(exist_ok=args.resume)
    config = json.loads(args.config.read_text())
    seed_root = root / "datasets/extraction_seed_v1"
    manifest = json.loads((seed_root / "manifest.json").read_text(encoding="utf-8-sig"))
    predictor = build_sam2_video_predictor("configs/sam2.1/sam2.1_hiera_t.yaml", str(root / ".cache/sam2.1_hiera_tiny.pt"),
                                          device="cuda", apply_postprocessing=False)
    summary = {}
    colors = [(70,180,230),(70,230,70),(230,140,50),(190,70,230),(30,220,240),(220,220,80)]
    for case in config["cases"]:
        folder = output / case["name"]
        if args.resume and (folder / "tracks.json").exists():
            continue
        print("Processing " + case["name"], flush=True)
        frames = folder / "frames"
        frames.mkdir(parents=True,exist_ok=args.resume)
        objects = {o["id"]:o for o in case["objects"]}
        for object_id in objects:
            (folder / "review_masks" / str(object_id)).mkdir(parents=True,exist_ok=args.resume)
        video, sample = next((v,s) for v in manifest["videos"] for s in v["samples"] if Path(s["image"]).name == case["seed_image"])
        cap = cv2.VideoCapture(video["source"])
        assert cap.isOpened()
        cap.set(cv2.CAP_PROP_POS_FRAMES,sample["frame"])
        stamps = []
        for i in range(case["frames"]):
            ok, frame = cap.read()
            if not ok:
                break
            if i==0:
                reference = cv2.imread(str(seed_root / sample["image"]))
                if np.max(np.abs(frame.astype(float)-reference.astype(float))) > 2:
                    # Some codecs seek to a different decoded frame. Verify a sequential fallback.
                    cap.release()
                    cap = cv2.VideoCapture(video["source"])
                    for _ in range(sample["frame"]+1):
                        ok, frame = cap.read()
                        if not ok: raise ValueError("Sequential seed decode failed.")
                assert np.max(np.abs(frame.astype(float)-reference.astype(float))) <= 2
                h,w = frame.shape[:2]
            cv2.imwrite(str(frames / f"{i:05d}.jpg"),frame,[cv2.IMWRITE_JPEG_QUALITY,98])
            stamps.append({"source_frame":int(cap.get(cv2.CAP_PROP_POS_FRAMES))-1,"decoder_time_ms":cap.get(cv2.CAP_PROP_POS_MSEC)})
        cap.release()
        writer=cv2.VideoWriter(str(folder / "review_raw.mp4"),cv2.VideoWriter_fourcc(*"mp4v"),video["nominal_fps"],(2*w,h))
        assert writer.isOpened()
        records, events, tiles = [],[],[]
        separated_runs={i:0 for i in objects}
        had_contact={i:False for i in objects}
        latched={i:False for i in objects}
        fallback_latched=False
        with torch.inference_mode(),torch.autocast("cuda",dtype=torch.bfloat16):
            state=predictor.init_state(str(frames),offload_video_to_cpu=True,offload_state_to_cpu=True)
            for object_id,o in objects.items():
                mask=np.zeros((h,w),np.uint8)
                for polygon in o.get("polygons",[o.get("polygon")]):
                    cv2.fillPoly(mask,[np.array(polygon,np.int32)],1)
                predictor.add_new_mask(state,frame_idx=0,obj_id=object_id,mask=mask.astype(bool))
            for correction in case.get("corrections",[]):
                mask=np.zeros((h,w),np.uint8)
                for polygon in correction.get("polygons",[]):
                    cv2.fillPoly(mask,[np.array(polygon,np.int32)],1)
                local_index=correction["source_frame"]-sample["frame"]
                assert 0<=local_index<len(stamps)
                predictor.add_new_mask(state,frame_idx=local_index,obj_id=correction["id"],mask=mask.astype(bool))
            for index,ids,logits in predictor.propagate_in_video(state):
                scores=logits[:,0].float().cpu().numpy()
                raw=scores>0
                envelope_id=case.get("envelope_id")
                cell_indices=[j for j,i in enumerate(ids) if objects[int(i)]["kind"] in ("cell","cell_group","uncertain_object")]
                child_ids={int(ids[j]) for j in cell_indices}
                render_scores=scores.copy()
                if envelope_id:
                    render_scores[list(ids).index(envelope_id)]=-1e6
                winner=np.argmax(render_scores,axis=0)
                masks={int(object_id):(raw[j] if int(object_id)==envelope_id else raw[j] & (winner==j)).astype(np.uint8) for j,object_id in enumerate(ids)}
                fallback_reasons=[]
                if envelope_id:
                    envelope=masks[envelope_id]>0
                    for j in cell_indices:
                        cell=raw[j]
                        area=int(cell.sum())
                        other=np.sum(raw[cell_indices],axis=0)>1
                        if area<25: fallback_reasons.append("missing_cell")
                        elif (cell & other).sum()/area>.25: fallback_reasons.append("cell_overlap")
                        elif (cell & ~envelope).sum()/area>.25: fallback_reasons.append("cell_outside_group")
                    if fallback_reasons and not fallback_latched:
                        events.append({"type":"fallback_to_unresolved_group",**stamps[index],"reasons":sorted(set(fallback_reasons)),"requires_review":True})
                        fallback_latched=True
                frame=cv2.imread(str(frames / f"{index:05d}.jpg"))
                overlay=frame.copy()
                object_records=[]
                for j,object_id in enumerate(ids):
                    object_id=int(object_id)
                    o=objects[object_id]
                    mask=masks[object_id]
                    area=int(mask.sum())
                    raw_area=int(raw[j].sum())
                    competing=[k for k,i in enumerate(ids) if int(i)!=envelope_id]
                    overlap=float((raw[j] & (raw[competing].sum(axis=0)>1)).sum()/max(raw_area,1))
                    status="lost" if area<25 else ("ambiguous_overlap" if overlap>.3 else "visible_draft")
                    relation="unknown"
                    gap=None
                    if o.get("parent_id") and status=="visible_draft":
                        parent=masks[o["parent_id"]]
                        if parent.sum()>=100:
                            distances=cv2.distanceTransform((1-parent).astype(np.uint8),cv2.DIST_L2,5)
                            gap=float(distances[mask>0].min())
                            relation="contact_or_near" if gap<=6 else "separated_in_projection"
                            if gap<=6:
                                had_contact[object_id]=True
                                separated_runs[object_id]=0
                            else:
                                separated_runs[object_id]+=1
                            if had_contact[object_id] and separated_runs[object_id]>=3 and not latched[object_id]:
                                events.append({"type":"separation_candidate","object_id":object_id,"parent_id":o["parent_id"],**stamps[index],"requires_review":True})
                                latched[object_id]=True
                        else:
                            separated_runs[object_id]=0
                    else:
                        separated_runs[object_id]=0
                    cv2.imwrite(str(folder / "review_masks" / str(object_id) / f"{index:05d}.png"),mask*255)
                    show=(not envelope_id or (object_id==envelope_id and fallback_latched) or (object_id!=envelope_id and (not fallback_latched or object_id not in child_ids)))
                    if area>=25 and show:
                        color=np.array(colors[(object_id-1)%len(colors)])
                        overlay[mask>0]=(.62*overlay[mask>0]+.38*color).astype(np.uint8)
                        yy,xx=np.nonzero(mask)
                        label=o.get("display_label",{"unresolved_group":"group ? cells","cell_group":"cell group","uncertain_object":"uncertain object","cell":"cell"}[o["kind"]])
                        text=f"{object_id}: {label}"
                        if status!="visible_draft": text+=" ?"
                        cv2.putText(overlay,text,(int(xx.mean()),max(40,int(yy.mean()))),0,.45,(255,255,255),1)
                    object_records.append({"id":object_id,"kind":o["kind"],"seed_cell_count":o.get("seed_cell_count"),"area":area,"status":status,"raw_overlap_fraction":overlap,
                                           "parent_relation":relation,"parent_gap_pixels":gap,"training_eligible":False})
                cv2.putText(overlay,f"Frame {stamps[index]['source_frame']} | SEEDED DRAFT",(10,22),0,.55,(255,255,255),1)
                writer.write(np.hstack((frame,overlay)))
                if index%max(1,len(stamps)//12)==0 or index==len(stamps)-1:
                    tiles.append(cv2.resize(overlay,(320,240)))
                records.append({**stamps[index],"objects":object_records,"fallback_to_group":fallback_latched,"fallback_reasons":sorted(set(fallback_reasons))})
            predictor.reset_state(state)
            del state
        writer.release()
        while len(tiles)%3: tiles.append(np.zeros_like(tiles[0]))
        cv2.imwrite(str(folder / "inspection.jpg"),np.vstack([np.hstack(tiles[i:i+3]) for i in range(0,len(tiles),3)]))
        ffmpeg=root / "GUI/ThirdParty/ffmpeg/ffmpeg.exe"
        subprocess.run([str(ffmpeg),"-y","-i",str(folder / "review_raw.mp4"),"-c:v","libx264","-crf","20","-pix_fmt","yuv420p","-movflags","+faststart",str(folder / "review.mp4")],check=True,capture_output=True)
        report={"source":video["source"],"source_sha256":video["source_sha256"],"split":video["split"],"seeds":case,
                "policy":config["policy"],"events":events,"frames":records,"exhaustive_annotation":False,"training_eligible":False}
        (folder / "tracks.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
        summary[case["name"]]={"frames":len(records),"seeded_objects":len(objects),
                               "separation_candidates":sum(e["type"]=="separation_candidate" for e in events),
                               "fallback_events":sum(e["type"]=="fallback_to_unresolved_group" for e in events),
                               "video":str(folder / "review.mp4")}
        (output / "summary.json").write_text(json.dumps(summary,indent=2),encoding="utf-8")
        torch.cuda.empty_cache()


if __name__=="__main__":
    main()
