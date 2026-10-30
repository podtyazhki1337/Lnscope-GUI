"""Build short annotation sequences only from explicitly accepted seed masks."""
import json
from pathlib import Path
import cv2
from train_vision_pilot import BATCHES


def main():
    root = Path(__file__).resolve().parents[1]
    proposals = root / "datasets/large_annotation_proposals_v2"
    decisions = json.loads((root / "tools/large_annotation_review.json").read_text())
    grouped = {}
    for kind, category in (("cell", "cells"), ("cell_group", "groups")):
        for video, accepted in decisions.get(category, {}).items():
            index = {r["index"]: r for r in json.loads((proposals / "review" / (video+"_index.json")).read_text())}
            for value in accepted:
                item = index[value]
                mask = cv2.imread(str(proposals/item["folder"]/f"{item['id']:03d}.png"),0)
                contours,_=cv2.findContours(mask,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_SIMPLE)
                polygon=cv2.approxPolyDP(max(contours,key=cv2.contourArea),.7,True).reshape(-1,2).tolist()
                seed=item["folder"]+".png"
                objects=grouped.setdefault(seed,[])
                objects.append(dict(id=len(objects)+1,kind=kind,polygon=polygon,proposal_index=value))
    cases=[dict(name="proposal_"+Path(seed).stem,seed_image=seed,frames=128,objects=objects) for seed,objects in grouped.items()]
    for task,batches in BATCHES.items():
        seen=set()
        for batch in batches:
            data=json.loads((root/"datasets"/batch/"annotations.json").read_text())
            for annotation in data["annotations"]:
                if annotation.get("partial_annotation") or annotation.get("review_required") or annotation["output"].startswith("review/"):
                    continue
                seed=annotation["image"]
                # This source currently decodes differently from its saved seed image.
                if seed.startswith("18_Eminus_"): continue
                if seed in seen or (task=="cell" and seed in grouped): continue
                seen.add(seed)
                polygons=annotation.get("polygons",[annotation.get("polygon")])
                objects=[dict(id=i+1,kind="cell" if task=="cell" else "unresolved_group",polygon=p) for i,p in enumerate(polygons)]
                cases.append(dict(name=task+"_"+Path(seed).stem,seed_image=seed,frames=160 if task=="spheroid" else 128,objects=objects))
    (root/"tools/large_video_cases.json").write_text(json.dumps(dict(cases=cases,policy="Assisted annotation drafts. Each exported frame requires explicit visual review; no motor control."),indent=2))
    print(json.dumps(dict(cases=len(cases),objects=sum(len(c['objects']) for c in cases))))


if __name__=="__main__": main()
