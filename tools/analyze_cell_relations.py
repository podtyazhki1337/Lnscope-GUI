"""Audit projected contact chains; never infer physical detachment or cell count."""
import json
from pathlib import Path

import cv2
import numpy as np


def reachable(graph, start, target):
    pending, seen = [start], set()
    while pending:
        node = pending.pop()
        if node == target:
            return True
        if node not in seen:
            seen.add(node)
            pending.extend(graph[node] - seen)
    return False


def main():
    root = Path(__file__).resolve().parents[1] / "artifacts/multicell_video_v1"
    for folder in root.iterdir():
        if not folder.is_dir() or not (folder / "tracks.json").exists():
            continue
        report = json.loads((folder / "tracks.json").read_text())
        objects = {o["id"]: o for o in report["seeds"]["objects"]}
        seen_contact = {i:False for i in objects}
        separated = {i:0 for i in objects}
        latched = set()
        records, events = [], []
        for index, frame in enumerate(report["frames"]):
            masks = {i:cv2.imread(str(folder / "review_masks" / str(i) / f"{index:05d}.png"),0)>0 for i in objects}
            valid = {o["id"] for o in frame["objects"] if o["status"] == "visible_draft"}
            graph = {i:set() for i in objects}
            gaps = []
            for i in valid:
                distances = cv2.distanceTransform((~masks[i]).astype(np.uint8),cv2.DIST_L2,5)
                for j in valid:
                    if j<=i:
                        continue
                    gap = float(distances[masks[j]].min())
                    if gap<=6:
                        graph[i].add(j)
                        graph[j].add(i)
                    gaps.append({"a":i,"b":j,"gap_pixels":gap})
            relations = []
            for i,o in objects.items():
                if "parent_id" not in o:
                    continue
                parent = o["parent_id"]
                if i not in valid or parent not in valid:
                    relation = "unknown_lost_or_overlap"
                    separated[i] = 0
                elif reachable(graph,i,parent):
                    relation = "contact_chain_in_projection"
                    seen_contact[i] = True
                    separated[i] = 0
                elif len(valid) != len(objects):
                    relation = "unknown_incomplete_contact_graph"
                    separated[i] = 0
                else:
                    relation = "separated_in_projection"
                    separated[i] += 1
                    if seen_contact[i] and separated[i]>=3 and i not in latched:
                        events.append({"source_frame":frame["source_frame"],"object_id":i,"parent_id":parent,
                                       "type":"separation_candidate","requires_review":True})
                        latched.add(i)
                relations.append({"id":i,"parent_id":parent,"relation":relation})
            records.append({"source_frame":frame["source_frame"],"relations":relations,"pairwise_gaps":gaps})
        result = {"note":"Supersedes direct-gap events in tracks.json; projected contact only, not physical detachment.",
                  "threshold_pixels":6,"persistence_frames":3,"events":events,"frames":records}
        (folder / "contact_graph.json").write_text(json.dumps(result,indent=2),encoding="utf-8")
        print(folder.name, "contact-chain candidate events:",len(events))


if __name__=="__main__":
    assert reachable({1:{2},2:{1,3},3:{2}},3,1)
    assert not reachable({1:set(),2:{3},3:{2}},3,1)
    main()
