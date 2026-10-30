"""Synthetic ground-truth checks and local video-session checks; no hardware."""
import csv
import io
from pathlib import Path
import tempfile
import unittest
import cv2
import numpy as np
from tracking import PointTrack
from vision import VideoSession


class TrackingChecks(unittest.TestCase):
    def setUp(self):
        self.image = np.random.default_rng(24).integers(0, 256, (160, 220), dtype=np.uint8)

    def test_translation_and_blank_rejection(self):
        track = PointTrack(self.image, 80, 80)
        shifted = cv2.warpAffine(self.image, np.float32([[1, 0, 7], [0, 1, -4]]), (220, 160))
        track.update(shifted)
        self.assertFalse(track.lost)
        self.assertEqual((track.x, track.y), (87, 76))
        with self.assertRaises(ValueError):
            PointTrack(np.zeros((100, 100), np.uint8), 50, 50)

    def test_occlusion_latches_loss(self):
        track = PointTrack(self.image, 80, 80)
        track.update(np.full_like(self.image, 128))
        self.assertTrue(track.lost)
        track.update(self.image)
        self.assertTrue(track.lost)
        self.assertEqual((track.x, track.y), (80, 80))

    def test_ambiguous_pattern_is_lost(self):
        track = PointTrack(self.image, 80, 80)
        repeated = self.image.copy()
        repeated[65:96, 96:127] = track.template
        track.update(repeated)
        self.assertTrue(track.lost)
        self.assertIn('Ambiguous', track.reason)

    def test_video_seek_reset_export_and_eof(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'sequence.avi'
            writer = cv2.VideoWriter(str(path), cv2.VideoWriter_fourcc(*'MJPG'), 10, (220, 160))
            self.assertTrue(writer.isOpened())
            for dx in range(4):
                frame = cv2.warpAffine(self.image, np.float32([[1, 0, dx], [0, 1, 0]]), (220, 160))
                writer.write(cv2.cvtColor(frame, cv2.COLOR_GRAY2BGR))
            writer.release()
            session = VideoSession()
            try:
                first = session.call('vision_open', dict(path=str(path)))
                self.assertEqual(first['width'], 220)
                session.call('vision_mark', dict(name='tip', x=60, y=70))
                session.call('vision_mark', dict(name='target', x=140, y=90))
                result = session.call('vision_next', {})
                self.assertEqual(result['offset']['dx'], 80)
                self.assertEqual(result['offset']['dy'], 20)
                self.assertFalse(result['tracks']['tip']['lost'])
                for _ in range(4): result = session.call('vision_next', {})
                self.assertTrue(result['eof'])
                result = session.call('vision_seek', dict(seconds=0))
                self.assertEqual(result['tracks'], {})
                self.assertIsNone(result['offset'])
                rows = list(csv.DictReader(io.StringIO(session.call('vision_export', {})['csv'])))
                self.assertTrue(any(r['event'] == 'mark_tip' for r in rows))
                self.assertEqual(rows[-1]['event'], 'seek')
            finally:
                session.capture.release()
