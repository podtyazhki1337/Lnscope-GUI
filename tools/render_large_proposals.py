"""Render numbered proposal crops for explicit visual classification."""
import argparse
import json
from pathlib import Path
import cv2
import numpy as np


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--video", required=True)
    parser.add_argument("--source", default="datasets/large_annotation_proposals")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    source = root / "datasets/extraction_seed_v1"
    output = root / args.source
    review = output / "review"
    review.mkdir(exist_ok=True)
    records, tiles = [], []
    for path in sorted(output.glob(args.video+"*/proposals.json")):
        data = json.loads(path.read_text())
        image = cv2.imread(str(source / data["image"]))
        for obj in data["objects"]:
            x,y,w,h = obj["bbox"]
            if obj["area"]>18000 or max(w,h)>230:
                continue
            mask = cv2.imread(str(path.parent / f"{obj['id']:03d}.png"), 0)
            x0,y0,x1,y1=max(0,x-8),max(0,y-8),min(image.shape[1],x+w+9),min(image.shape[0],y+h+9)
            crop = image[y0:y1,x0:x1].copy()
            contours,_=cv2.findContours(mask[y0:y1,x0:x1],cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_SIMPLE)
            cv2.drawContours(crop,contours,-1,(0,255,70),1)
            index=len(records)+1
            records.append(dict(index=index,folder=path.parent.name,id=obj["id"],bbox=obj["bbox"]))
            tile=np.full((170,180,3),28,np.uint8)
            scale=min(172/crop.shape[1],140/crop.shape[0])
            resized=cv2.resize(crop,(round(crop.shape[1]*scale),round(crop.shape[0]*scale)))
            tile[26:26+resized.shape[0],4:4+resized.shape[1]]=resized
            cv2.putText(tile,f"{index} f{data['source_frame']} #{obj['id']}",(4,17),0,.42,(255,255,255),1)
            tiles.append(tile)
    (review / (args.video+"_index.json")).write_text(json.dumps(records,indent=2))
    for start in range(0,len(tiles),48):
        page=tiles[start:start+48]
        while len(page)%6: page.append(np.zeros_like(tiles[0]))
        cv2.imwrite(str(review/f"{args.video}_{start//48+1:02d}.jpg"),np.vstack([np.hstack(page[i:i+6]) for i in range(0,len(page),6)]))
    print(f"{args.video}: {len(records)} candidates")


if __name__ == "__main__":
    main()
