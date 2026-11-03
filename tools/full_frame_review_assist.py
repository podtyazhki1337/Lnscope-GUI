"""Speed up native full-frame review: context sheets, junk flags and decision carry-over.

Nothing here marks a frame complete. Drafts are written with annotation_complete=false
and must be confirmed on the final overlay before training eligibility is set.
"""
import argparse
import json
import re
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / 'datasets/full_frame_annotations_v1'
PROPOSALS = ROOT / 'datasets/full_frame_sam2_proposals_v1'
PROMPTED = ROOT / 'datasets/full_frame_prompted_v1'
DECISIONS = ROOT / 'tools/full_frame_class_review.json'
SHEETS = ROOT / 'datasets/full_frame_review_sheets'
COLORS = {'cell': (60, 240, 60), 'cell_group': (40, 210, 255), 'spheroid': (255, 110, 70), None: (235, 235, 235)}
FLAG_COLOR = (110, 110, 110)
NAME = re.compile(r'^(\d+_.+)_f(\d+)$')

# Geometry thresholds for junk flags; they only pre-sort candidates for review.
MIN_AREA = 60
BACKGROUND_FRACTION = 0.6
DUPLICATE_IOU = 0.9
CARRY_IOU = 0.5
ACCEPT_IOU = 0.75
GRID = 40


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def write_json(path, data):
    path.write_text(json.dumps(data, indent=1, ensure_ascii=False) + '\n', encoding='utf-8')


def mask(path, shape):
    image = cv2.imread(str(path), cv2.IMREAD_GRAYSCALE)
    if image is None or image.shape != shape:
        raise ValueError(f'Missing or non-native mask: {path}')
    return image > 0


def manifest():
    return {f['name']: f for f in read_json(BASE / 'manifest.json')['frames']}


def proposals(name, shape):
    data = read_json(PROPOSALS / name / 'proposals.json')
    return data, {o['id']: mask(PROPOSALS / name / f"{o['id']:03d}.png", shape) for o in data['objects']}


def iou(a, b):
    union = np.logical_or(a, b).sum()
    return float(np.logical_and(a, b).sum() / union) if union else 0.0


def junk_flags(objects, masks, shape):
    """Return {id: reason} for candidates that are almost certainly not trainable objects."""
    flags = {}
    frame_area = shape[0] * shape[1]
    ids = sorted(masks, key=lambda i: -masks[i].sum())
    for i in ids:
        area = int(masks[i].sum())
        if area < MIN_AREA:
            flags[i] = f'Fragment below {MIN_AREA} px'
        elif area > BACKGROUND_FRACTION * frame_area:
            flags[i] = 'Background-scale mask'
    for n, i in enumerate(ids):
        if i in flags:
            continue
        for j in ids[:n]:
            if j in flags:
                continue
            if iou(masks[i], masks[j]) >= DUPLICATE_IOU:
                flags[i] = f'Duplicate of candidate {j}'
                break
    # Nested masks are not flagged: real edge cells are often nested inside a cluster or tissue mask.
    return flags


def largest(m):
    count, labels, stats, _ = cv2.connectedComponentsWithStats(m.astype(np.uint8), 8)
    return labels == (1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))) if count > 2 else m


def fill_holes(m):
    """Fill enclosed holes; regions connected to the image border stay background."""
    inverse = (~m).astype(np.uint8)
    count, labels, stats, _ = cv2.connectedComponentsWithStats(inverse, 4)
    filled = m.copy()
    h, w = m.shape
    for label in range(1, count):
        x, y, bw, bh, _ = stats[label]
        if x > 0 and y > 0 and x + bw < w and y + bh < h:
            filled[labels == label] = True
    return filled


def label_point(m):
    dist = cv2.distanceTransform(m.astype(np.uint8), cv2.DIST_L2, 3)
    y, x = np.unravel_index(int(np.argmax(dist)), dist.shape)
    return int(x), int(y)


def put(image, text, xy, color, scale=.42):
    cv2.putText(image, text, xy, 0, scale, (0, 0, 0), 3, cv2.LINE_AA)
    cv2.putText(image, text, xy, 0, scale, color, 1, cv2.LINE_AA)


def sheet(name, frames, decisions):
    frame = frames[name]
    image = cv2.imread(str(BASE / 'images' / frame['split'] / (name + '.png')))
    shape = image.shape[:2]
    data, masks = proposals(name, shape)
    flags = junk_flags(data['objects'], masks, shape)
    decision = decisions.get(name, {})
    chosen = {o['id']: o['class'] for o in decision.get('objects', []) if o['source'] == 'sam2'}
    rejected = {int(k) for k in decision.get('rejected', {})}
    overlay = image.copy()
    order = sorted(data['objects'], key=lambda o: -o['area'])
    for obj in order:
        i = obj['id']
        flagged = i in flags or i in rejected
        color = FLAG_COLOR if flagged else COLORS[chosen.get(i, obj.get('suggested_class'))]
        contours, _ = cv2.findContours(masks[i].astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        cv2.drawContours(overlay, contours, -1, color, 1 if flagged else 2, cv2.LINE_AA)
    for obj in order:
        i = obj['id']
        x, y = label_point(masks[i])
        flagged = i in flags or i in rejected
        tag = str(i) + ('x' if flagged else {'cell': 'c', 'cell_group': 'g', 'spheroid': 's'}.get(
            chosen.get(i, obj.get('suggested_class')), ''))
        put(overlay, tag, (max(0, x - 6), min(shape[0] - 3, y + 4)),
            FLAG_COLOR if flagged else COLORS[chosen.get(i, obj.get('suggested_class'))])
    header = np.full((26, shape[1] * 2, 3), 24, np.uint8)
    state = 'complete' if decision.get('annotation_complete') else ('draft' if decision else 'unreviewed')
    put(header, f"{name}  [{frame['split']}]  {len(data['objects'])} candidates, {len(flags)} flagged  {state}",
        (6, 18), (255, 255, 255), .45)
    return np.vstack([header, np.hstack([image, overlay])]), flags


def prompt_from(m, shape, pad=6):
    ys, xs = np.where(m)
    x, y = label_point(m)
    box = [max(0, int(xs.min()) - pad), max(0, int(ys.min()) - pad),
           min(shape[1] - 1, int(xs.max()) + pad), min(shape[0] - 1, int(ys.max()) + pad)]
    return dict(points=[[x, y]], labels=[1], box=box)


def cmd_grid(prefix, limit):
    """Native frame with a labelled pixel grid for writing SAM2 prompts."""
    frames = manifest()
    out = SHEETS / 'grid'
    out.mkdir(parents=True, exist_ok=True)
    names = [n for n in sorted(frames) if n.startswith(prefix)][:limit]
    for name in names:
        image = cv2.imread(str(BASE / 'images' / frames[name]['split'] / (name + '.png')))
        grid = image.copy()
        h, w = image.shape[:2]
        for x in range(0, w, GRID):
            cv2.line(grid, (x, 0), (x, h), (0, 255, 255) if x % (GRID * 2) == 0 else (0, 160, 160), 1)
        for y in range(0, h, GRID):
            cv2.line(grid, (0, y), (w, y), (0, 255, 255) if y % (GRID * 2) == 0 else (0, 160, 160), 1)
        image = cv2.addWeighted(image, .72, grid, .28, 0)
        for x in range(0, w, GRID * 2):
            put(image, str(x), (x + 2, 11), (0, 255, 255), .35)
        for y in range(GRID * 2, h, GRID * 2):
            put(image, str(y), (2, y - 3), (0, 255, 255), .35)
        cv2.imwrite(str(out / f'{name}.png'), image)
    print(f'Rendered {len(names)} grid frames into {out.relative_to(ROOT)}')


def cmd_sheets(prefix, limit):
    frames, decisions = manifest(), read_json(DECISIONS)['frames']
    SHEETS.mkdir(parents=True, exist_ok=True)
    names = [n for n in sorted(frames) if n.startswith(prefix)][:limit]
    report = {}
    for name in names:
        page, flags = sheet(name, frames, decisions)
        cv2.imwrite(str(SHEETS / f'{name}.jpg'), page, [cv2.IMWRITE_JPEG_QUALITY, 92])
        report[name] = {str(k): v for k, v in sorted(flags.items())}
    write_json(SHEETS / f'flags_{prefix or "all"}.json', report)
    print(f'Rendered {len(names)} sheets into {SHEETS.relative_to(ROOT)}')


def final_masks(name, decision, frame, shape):
    """Reproduce the masks that --apply would write, keyed by decision key."""
    masks = {}
    for obj in decision['objects']:
        if obj['source'] == 'sam2':
            path = PROPOSALS / name / f"{obj['id']:03d}.png"
        elif obj['source'] == 'base':
            path = BASE / next(o for o in frame['objects'] if o['id'] == obj['id'])['mask']
        else:
            path = PROMPTED / name / f"{obj['key']}.png"
            if not path.exists():
                continue
        masks[obj['key']] = mask(path, shape)
    result = {}
    for obj in decision['objects']:
        if obj['key'] not in masks:
            continue
        m = masks[obj['key']].copy()
        for key in obj.get('subtract', []):
            if key in masks:
                m &= ~masks[key]
        if obj.get('largest_component') and m.any():
            m = largest(m)
        result[obj['key']] = m
    return result


def nearest_reviewed(name, decisions, frames):
    video, index = NAME.match(name).groups()
    best = None
    for other, decision in decisions.items():
        if decision.get('draft') or other == name:
            continue
        match = NAME.match(other)
        if match and match.group(1) == video and frames[other]['split'] == frames[name]['split']:
            gap = abs(int(match.group(2)) - int(index))
            if best is None or gap < best[0]:
                best = (gap, other)
    return best


def cmd_draft(prefix, max_gap, overwrite):
    """Draft decisions for unreviewed frames by carrying classes from the nearest confirmed frame."""
    frames = manifest()
    store = read_json(DECISIONS)
    decisions = store['frames']
    drafted = 0
    for name in sorted(frames):
        if not name.startswith(prefix) or (name in decisions and not (overwrite and decisions[name].get('draft'))):
            continue
        frame = frames[name]
        shape = (frame['height'], frame['width'])
        data, masks = proposals(name, shape)
        flags = junk_flags(data['objects'], masks, shape)
        draft = dict(annotation_complete=False, draft=True, objects=[], rejected={}, issues=[])
        draft['rejected'] = {str(i): reason for i, reason in flags.items()}
        source = nearest_reviewed(name, decisions, frames)
        if source and source[0] <= max_gap:
            ref_name = source[1]
            ref = decisions[ref_name]
            ref_shape = (frames[ref_name]['height'], frames[ref_name]['width'])
            ref_final = final_masks(ref_name, ref, frames[ref_name], ref_shape)
            free = {i for i in masks if i not in flags}
            for obj in ref['objects']:
                target = ref_final.get(obj['key'])
                if target is None or not target.any():
                    continue
                carried = {k: v for k, v in obj.items() if k not in ('source', 'id', 'prompt')}
                scored = sorted(((iou(target, masks[i]), i) for i in free), reverse=True)
                if scored and scored[0][0] >= ACCEPT_IOU:
                    carried.update(source='sam2', id=scored[0][1])
                    free.discard(scored[0][1])
                else:
                    # No close automatic mask: re-prompt SAM2 from the confirmed neighbour's mask.
                    carried.update(source='prompted', prompt=prompt_from(target, shape))
                draft['objects'].append(carried)
            ref_props = proposals(ref_name, ref_shape)[1]
            for key, reason in ref.get('rejected', {}).items():
                target = ref_props.get(int(key))
                if target is None:
                    continue
                scored = sorted(((iou(target, masks[i]), i) for i in free), reverse=True)
                if scored and scored[0][0] >= CARRY_IOU:
                    draft['rejected'][str(scored[0][1])] = reason
                    free.discard(scored[0][1])
            keys = {o['key'] for o in draft['objects']}
            for obj in draft['objects']:
                if 'subtract' in obj:
                    obj['subtract'] = [k for k in obj['subtract'] if k in keys]
            draft['issues'].append(f'Draft carried from {ref_name}; confirm overlay and unaccounted candidates.')
            unaccounted = sorted(free - {int(k) for k in draft['rejected']})
        else:
            unaccounted = sorted(i for i in masks if i not in flags)
            draft['issues'].append('Draft without a confirmed reference frame.')
        if unaccounted:
            draft['issues'].append('Unaccounted candidates: ' + ', '.join(map(str, unaccounted)))
        decisions[name] = draft
        drafted += 1
    write_json(DECISIONS, store)
    print(f'Drafted {drafted} frames; none marked complete.')


def cmd_merge(path):
    """Merge hand-written frame decisions {name: {objects, rejected?, issues?}} as drafts."""
    frames = manifest()
    store = read_json(DECISIONS)
    for name, given in read_json(path).items():
        if name not in frames:
            raise ValueError(f'Unknown frame: {name}')
        if store['frames'].get(name, {}).get('annotation_complete'):
            raise ValueError(f'Refusing to overwrite a confirmed frame: {name}')
        shape = (frames[name]['height'], frames[name]['width'])
        data, masks = proposals(name, shape)
        rejected = {str(i): r for i, r in junk_flags(data['objects'], masks, shape).items()}
        rejected.update({str(k): v for k, v in given.get('rejected', {}).items()})
        selected = {str(o['id']) for o in given['objects'] if o['source'] == 'sam2'}
        rejected = {k: v for k, v in rejected.items() if k not in selected}
        store['frames'][name] = dict(annotation_complete=False, draft=True, manual=True, objects=given['objects'],
                                     rejected=rejected, issues=given.get('issues', []))
    write_json(DECISIONS, store)
    print(f'Merged {len(read_json(path))} frame decisions as drafts.')


def cmd_confirm(names, reason, exclude):
    """After visual check of the final overlay: account for leftovers and set completeness."""
    frames = manifest()
    store = read_json(DECISIONS)
    for name in names:
        decision = store['frames'][name]
        if exclude:
            decision.update(annotation_complete=False, issues=[exclude])
            decision.pop('draft', None)
            decision.pop('manual', None)
            continue
        shape = (frames[name]['height'], frames[name]['width'])
        data, _ = proposals(name, shape)
        selected = {o['id'] for o in decision['objects'] if o['source'] == 'sam2'}
        for obj in data['objects']:
            if obj['id'] not in selected:
                decision['rejected'].setdefault(str(obj['id']), reason)
        decision.update(annotation_complete=True, issues=[])
        decision.pop('draft', None)
        decision.pop('manual', None)
    write_json(DECISIONS, store)
    print(f"{'Excluded' if exclude else 'Confirmed'} {len(names)} frames.")


RUN_GAP = 24


def runs(frames):
    """Group frame names into contiguous runs within one video and split."""
    groups = {}
    for name in sorted(frames):
        video, index = NAME.match(name).groups()
        groups.setdefault((video, frames[name]['split']), []).append((int(index), name))
    result = []
    for items in groups.values():
        current = [items[0][1]]
        for (a, _), (b, name) in zip(items, items[1:]):
            if b - a > RUN_GAP:
                result.append(current)
                current = []
            current.append(name)
        result.append(current)
    return result


def interior_points(m, count, spacing):
    dist = cv2.distanceTransform(m.astype(np.uint8), cv2.DIST_L2, 5)
    points = []
    work = dist.copy()
    for _ in range(count):
        y, x = np.unravel_index(int(np.argmax(work)), work.shape)
        if work[y, x] <= 2:
            break
        points.append([int(x), int(y)])
        cv2.circle(work, (int(x), int(y)), spacing, 0, -1)
    return points


def carry_prompt(key, masks, shape, large):
    m = masks[key]
    ys, xs = np.where(m)
    pad = 10 if large else 6
    box = [max(0, int(xs.min()) - pad), max(0, int(ys.min()) - pad),
           min(shape[1] - 1, int(xs.max()) + pad), min(shape[0] - 1, int(ys.max()) + pad)]
    positive = interior_points(m, 6 if large else 1, 60)
    negative = []
    for other, om in masks.items():
        if other == key or not om.any():
            continue
        x, y = label_point(om)
        if box[0] <= x <= box[2] and box[1] <= y <= box[3] and not m[y, x]:
            negative.append([x, y])
    return dict(points=positive + negative, labels=[1] * len(positive) + [0] * len(negative), box=box)


def cmd_propagate(prefix):
    """Walk outward from confirmed frames through each run, re-prompting SAM2 frame by frame."""
    import torch
    from sam2.build_sam import build_sam2
    from sam2.sam2_image_predictor import SAM2ImagePredictor

    frames = manifest()
    store = read_json(DECISIONS)
    decisions = store['frames']
    predictor = SAM2ImagePredictor(build_sam2('configs/sam2.1/sam2.1_hiera_t.yaml',
        str(ROOT / '.cache/sam2.1_hiera_tiny.pt'), device='cuda', apply_postprocessing=False))
    done = 0
    with torch.inference_mode(), torch.autocast('cuda', dtype=torch.bfloat16):
        for run in runs(frames):
            if not run[0].startswith(prefix):
                continue
            confirmed = [i for i, n in enumerate(run) if decisions.get(n, {}).get('annotation_complete')]
            if not confirmed:
                continue
            todo = [i for i, n in enumerate(run) if not decisions.get(n, {}).get('annotation_complete')
                    and (n not in decisions or (decisions[n].get('draft') and not decisions[n].get('manual')))]
            todo.sort(key=lambda i: min(abs(i - c) for c in confirmed))
            known = {}
            for i in todo:
                c = min(confirmed + list(known), key=lambda j: (abs(i - j), j not in confirmed))
                step = 1 if c > i else -1
                ref_index = i + step
                if ref_index not in known and ref_index not in confirmed:
                    continue
                ref_name, name = run[ref_index], run[i]
                frame = frames[name]
                shape = (frame['height'], frame['width'])
                ref = decisions[ref_name] if ref_index in confirmed else known[ref_index][0]
                ref_masks = known[ref_index][1] if ref_index in known else final_masks(
                    ref_name, ref, frames[ref_name], (frames[ref_name]['height'], frames[ref_name]['width']))
                image = cv2.imread(str(BASE / 'images' / frame['split'] / (name + '.png')))
                predictor.set_image(cv2.cvtColor(image, cv2.COLOR_BGR2RGB))
                folder = PROMPTED / name
                folder.mkdir(parents=True, exist_ok=True)
                objects, new_masks, issues = [], {}, []
                raw = {}
                for obj in ref['objects']:
                    key = obj['key']
                    if key not in ref_masks or not ref_masks[key].any():
                        continue
                    prompt = carry_prompt(key, ref_masks, shape, obj['class'] != 'cell')
                    pred, score, _ = predictor.predict(point_coords=np.array(prompt['points'], np.float32),
                        point_labels=np.array(prompt['labels'], np.int32), box=np.array(prompt['box'], np.float32),
                        multimask_output=False)
                    m = fill_holes(pred[0] > 0)
                    if not m.any():
                        issues.append(f'Lost {key}')
                        continue
                    ratio = m.sum() / max(1, ref_masks[key].sum())
                    if ratio > 2 or ratio < .5 or iou(m, ref_masks[key]) < .3:
                        issues.append(f'Unstable {key}: area x{ratio:.2f}, IoU {iou(m, ref_masks[key]):.2f}')
                    cv2.imwrite(str(folder / f'{key}.png'), m.astype(np.uint8) * 255)
                    write_json(folder / f'{key}.json', dict(prompt=prompt, predicted_iou=float(score[0]),
                        status='requires_visual_review', carried_from=ref_name))
                    raw[key] = m
                    carried = {k: v for k, v in obj.items() if k not in ('source', 'id', 'prompt', 'largest_component')}
                    carried.update(source='prompted', prompt=prompt)
                    if obj['class'] != 'spheroid':
                        carried['largest_component'] = True
                    objects.append(carried)
                for obj in objects:
                    m = raw[obj['key']].copy()
                    obj['subtract'] = [k for k in obj.get('subtract', []) if k in raw]
                    for k in obj['subtract']:
                        m &= ~raw[k]
                    if obj.get('largest_component') and m.any():
                        m = largest(m)
                    new_masks[obj['key']] = m
                data, pmasks = proposals(name, shape)
                draft = dict(annotation_complete=False, draft=True, objects=objects,
                             rejected={str(k): v for k, v in junk_flags(data['objects'], pmasks, shape).items()},
                             issues=[f'Propagated from {ref_name}; confirm overlay.'] + issues)
                decisions[name] = draft
                known[i] = (draft, new_masks)
                done += 1
                print(f'{name} <- {ref_name}' + (f'  [{"; ".join(issues)}]' if issues else ''), flush=True)
    write_json(DECISIONS, store)
    print(f'Propagated {done} frames; none marked complete.')


def cmd_drop(key, names):
    """Remove an object (e.g. one that left the field) from unconfirmed frames."""
    store = read_json(DECISIONS)
    for name in names:
        decision = store['frames'][name]
        if decision.get('annotation_complete'):
            raise ValueError(f'Refusing to edit a confirmed frame: {name}')
        decision['objects'] = [o for o in decision['objects'] if o['key'] != key]
        for obj in decision['objects']:
            if key in obj.get('subtract', []):
                obj['subtract'] = [k for k in obj['subtract'] if k != key]
        decision['manual'] = True
        decision['issues'] = [i for i in decision['issues'] if key not in i]
    write_json(DECISIONS, store)
    print(f'Dropped {key} from {len(names)} frames.')


def cmd_status(prefix):
    frames, decisions = manifest(), read_json(DECISIONS)['frames']
    rows = {}
    for name in sorted(frames):
        if not name.startswith(prefix):
            continue
        d = decisions.get(name)
        state = 'unreviewed' if d is None else 'complete' if d.get('annotation_complete') else 'draft' if d.get('draft') else 'pending'
        rows[state] = rows.get(state, 0) + 1
    print(json.dumps(rows))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    s = sub.add_parser('sheets', help='Raw frame beside numbered SAM2 candidates.')
    s.add_argument('prefix', nargs='?', default='')
    s.add_argument('--limit', type=int, default=10_000)
    d = sub.add_parser('draft', help='Junk flags and carry-over from the nearest confirmed frame.')
    d.add_argument('prefix', nargs='?', default='')
    d.add_argument('--max-gap', type=int, default=24, help='Maximum source-frame distance for carry-over.')
    d.add_argument('--overwrite', action='store_true', help='Replace existing drafts (never confirmed frames).')
    g = sub.add_parser('grid', help='Native frames with a labelled pixel grid.')
    g.add_argument('prefix', nargs='?', default='')
    g.add_argument('--limit', type=int, default=10_000)
    m = sub.add_parser('merge', help='Merge hand-written decisions as drafts.')
    m.add_argument('path', type=Path)
    c = sub.add_parser('confirm', help='Mark visually checked frames complete, or exclude them.')
    c.add_argument('names', nargs='+')
    c.add_argument('--reason', default='Not a separate trainable object; covered or background (visual review)')
    c.add_argument('--exclude', help='Keep frame out of training with this issue text.')
    pr = sub.add_parser('propagate', help='Carry confirmed masks through each run with SAM2 re-prompting.')
    pr.add_argument('prefix', nargs='?', default='')
    dr = sub.add_parser('drop', help='Remove an object key from unconfirmed frames.')
    dr.add_argument('key')
    dr.add_argument('names', nargs='+')
    t = sub.add_parser('status')
    t.add_argument('prefix', nargs='?', default='')
    args = parser.parse_args()
    if args.command == 'sheets':
        cmd_sheets(args.prefix, args.limit)
    elif args.command == 'grid':
        cmd_grid(args.prefix, args.limit)
    elif args.command == 'draft':
        cmd_draft(args.prefix, args.max_gap, args.overwrite)
    elif args.command == 'propagate':
        cmd_propagate(args.prefix)
    elif args.command == 'drop':
        cmd_drop(args.key, args.names)
    elif args.command == 'merge':
        cmd_merge(args.path)
    elif args.command == 'confirm':
        cmd_confirm(args.names, args.reason, args.exclude)
    else:
        cmd_status(args.prefix)


if __name__ == '__main__':
    main()
