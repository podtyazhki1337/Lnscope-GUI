"""Render every proposed training sample, with stable per-case frame identifiers."""
import argparse
import json
from pathlib import Path
import cv2
import numpy as np


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--case",default="*")
    args=parser.parse_args()
    root=Path(__file__).resolve().parents[1]
    output=root/"datasets/large_track_review"
    output.mkdir(exist_ok=True)
    for path in sorted((root/"artifacts/large_video_annotations").glob(args.case+"/tracks.json")):
        folder=output/path.parent.name
        if (folder/"index.json").exists(): continue
        folder.mkdir(exist_ok=True)
        track=json.loads(path.read_text())
        records,tiles=[],[]
        for i in sorted(set(range(0,len(track['frames']),8))|{len(track['frames'])-1}):
            frame=track['frames'][i]
            image=cv2.imread(str(path.parent/"frames"/f"{i:05d}.jpg"))
            for obj in frame['objects']:
                mask=cv2.imread(str(path.parent/"review_masks"/str(obj['id'])/f"{i:05d}.png"),0)
                contours,_=cv2.findContours(mask,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_SIMPLE)
                significant=[c for c in contours if cv2.contourArea(c)>=25]
                if len(significant)!=1: continue
                contour=max(significant,key=cv2.contourArea)
                x,y,w,h=cv2.boundingRect(contour)
                if obj['kind']!='unresolved_group' and (x<=1 or y<=1 or x+w>=image.shape[1]-1 or y+h>=image.shape[0]-1): continue
                if obj['kind']=='unresolved_group': x,y,w,h=0,0,image.shape[1],image.shape[0]
                left,top,right,bottom=max(0,x-5),max(0,y-5),min(image.shape[1],x+w+5),min(image.shape[0],y+h+5)
                crop=image[top:bottom,left:right].copy()
                cv2.drawContours(crop,[contour-[left,top]],-1,(0,255,70),1)
                tile=np.full((180,200,3),28,np.uint8)
                scale=min(192/crop.shape[1],145/crop.shape[0])
                resized=cv2.resize(crop,(round(crop.shape[1]*scale),round(crop.shape[0]*scale)))
                tile[28:28+resized.shape[0],4:4+resized.shape[1]]=resized
                number=len(records)+1
                cv2.putText(tile,f"{number}: f{frame['source_frame']} id{obj['id']}",(4,18),0,.42,(255,255,255),1)
                records.append(dict(index=number,local_frame=i,source_frame=frame['source_frame'],object_id=obj['id'],kind=obj['kind']))
                tiles.append(tile)
        (folder/"index.json").write_text(json.dumps(dict(case=path.parent.name,source=track['source'],split=track['split'],samples=records),indent=2))
        for start in range(0,len(tiles),36):
            page=tiles[start:start+36]
            while len(page)%6: page.append(np.zeros_like(tiles[0]))
            cv2.imwrite(str(folder/f"page_{start//36+1}.jpg"),np.vstack([np.hstack(page[i:i+6]) for i in range(0,len(page),6)]))
        print(f"{path.parent.name}: {len(records)} review samples",flush=True)


if __name__=='__main__': main()
