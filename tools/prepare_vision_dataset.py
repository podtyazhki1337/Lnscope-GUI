"""Prepare unlabelled extraction frames without modifying source experiments.

Run with the application's bundled Python (OpenCV and NumPy included).
Whole acquisition dates stay in one split; this is a provisional grouping,
not proof that differently named recordings contain distinct specimens.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
from collections import Counter
import cv2
import numpy as np

GROUPS = {'C1', 'C2', 'C3', 'C4', 'S', 'E+', 'E-'}
VALIDATION_DATES = {'2026-07-31', '2026-09-04'}
TEST_DATES = {'2026-08-03'}


def acquisition_date(name):
    match = re.match(r'(2026\d{4}|26\d{4})', name)
    if not match:
        raise ValueError(f'Unknown acquisition date: {name}')
    digits = match[1]
    if len(digits) == 6:
        digits = '20' + digits
    return f'{digits[:4]}-{digits[4:6]}-{digits[6:8]}'


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('source', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--frames', type=int, default=8)
    args = parser.parse_args()
    if not 2 <= args.frames <= 32:
        parser.error('--frames must be between 2 and 32')
    source, output = args.source.resolve(), args.output.resolve()
    files = sorted(p for p in source.rglob('*') if p.is_file() and p.parent.name in GROUPS and p.suffix.lower() in {'.mp4', '.avi', '.wmv', '.mov'})
    if not files:
        parser.error('No matching source videos')
    output.mkdir(parents=True, exist_ok=False)
    (output/'contact_sheets').mkdir()
    for split in ('train', 'val', 'test'):
        (output/'images'/split).mkdir(parents=True)
    records, hashes, date_splits = [], {}, {}
    for number, path in enumerate(files, 1):
        date = acquisition_date(path.name)
        split = 'val' if date in VALIDATION_DATES else 'test' if date in TEST_DATES else 'train'
        digest = sha256(path)
        if digest in hashes:
            raise ValueError(f'Duplicate video: {path} and {hashes[digest]}')
        hashes[digest] = str(path)
        date_splits[date] = split
        group = path.parent.name
        name = f'{number:02d}_{group.replace("+", "plus").replace("-", "minus")}_{path.stem}'
        cap = cv2.VideoCapture(str(path))
        if not cap.isOpened():
            raise ValueError(f'Cannot decode {path}')
        count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
        fps = float(cap.get(cv2.CAP_PROP_FPS))
        if count < args.frames or not np.isfinite(fps) or fps <= 0:
            raise ValueError(f'Invalid video metadata: {path}')
        indices = np.linspace(round((count-1)*0.04), round((count-1)*0.96), args.frames).round().astype(int)
        items, thumbnails = [], []
        try:
            for index in indices:
                if not cap.set(cv2.CAP_PROP_POS_FRAMES, int(index)):
                    raise ValueError(f'Seek failed: {path} frame {index}')
                ok, frame = cap.read()
                if not ok:
                    raise ValueError(f'Decode failed: {path} frame {index}')
                actual = int(cap.get(cv2.CAP_PROP_POS_FRAMES))-1
                time_ms = float(cap.get(cv2.CAP_PROP_POS_MSEC))
                filename = f'{name}_f{actual:06d}.png'
                destination = output/'images'/split/filename
                if destination.exists() or not cv2.imwrite(str(destination), frame):
                    raise ValueError(f'Image output failed: {destination}')
                h, w = frame.shape[:2]
                gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
                items.append(dict(image=str(destination.relative_to(output)).replace('\\', '/'), requested_frame=int(index),
                                  frame=actual, decoder_time_ms=time_ms, width=w, height=h,
                                  sha256=sha256(destination), annotation_status='unlabelled',
                                  laplacian_variance=float(cv2.Laplacian(gray, cv2.CV_64F).var())))
                thumb = np.full((264, 320, 3), 24, np.uint8)
                scale = min(320/w, 240/h)
                resized = cv2.resize(frame, (round(w*scale), round(h*scale)), interpolation=cv2.INTER_AREA)
                rh, rw = resized.shape[:2]
                thumb[(240-rh)//2:(240-rh)//2+rh, (320-rw)//2:(320-rw)//2+rw] = resized
                cv2.putText(thumb, f'f{actual} | {time_ms/1000:.2f}s', (8, 257), cv2.FONT_HERSHEY_SIMPLEX, .45, (240, 240, 240), 1)
                thumbnails.append(thumb)
        finally:
            cap.release()
        while len(thumbnails) % 4:
            thumbnails.append(np.full((264, 320, 3), 24, np.uint8))
        sheet = np.vstack([np.hstack(thumbnails[i:i+4]) for i in range(0, len(thumbnails), 4)])
        sheet = cv2.copyMakeBorder(sheet, 36, 0, 0, 0, cv2.BORDER_CONSTANT, value=(24, 24, 24))
        cv2.putText(sheet, f'{group} | {path.name} | {split}', (10, 25), cv2.FONT_HERSHEY_SIMPLEX, .55, (255, 255, 255), 1)
        sheet_name = f'contact_sheets/{name}.jpg'
        if not cv2.imwrite(str(output/sheet_name), sheet):
            raise ValueError('Contact sheet output failed')
        records.append(dict(source=str(path), source_relative=str(path.relative_to(source)), source_sha256=digest,
                            group=group, date=date, split=split, encoded_frame_count=count, nominal_fps=fps,
                            nominal_duration_s=count/fps, contact_sheet=sheet_name, samples=items))
        print(f'{number}/{len(files)} {group} {split}: {len(items)} frames', flush=True)
    manifest = dict(schema=1, source_root=str(source), status='unlabelled_not_training_ready',
                    selection='C1-C4, S, E+, E- selected by user; outcome is not an object class',
                    grouping='Acquisition date parsed from filename; confirm specimen identities before final evaluation',
                    dates=date_splits, videos=records)
    (output/'manifest.json').write_text(json.dumps(manifest, indent=2, ensure_ascii=False), encoding='utf-8')
    summary = dict(videos=len(records), frames=sum(len(r['samples']) for r in records),
                   videos_by_group=dict(Counter(r['group'] for r in records)),
                   frames_by_split=dict(Counter(s for r in records for s in [r['split']]*len(r['samples']))))
    (output/'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    print(json.dumps(summary), flush=True)


if __name__ == '__main__':
    main()
