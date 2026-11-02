"""Identity lifecycle checks do not require model downloads or hardware."""
import unittest
from segmentation import Identities


def detection(box, kind='cell'):
    return {'bbox': box, 'class': kind, 'confidence': .9}


class IdentityTests(unittest.TestCase):
    def test_motion_and_loss(self):
        tracker = Identities()
        first = [detection([10, 10, 50, 50])]
        tracker.update(first, 1)
        second = [detection([12, 12, 52, 52])]
        tracker.update(second, 1.2)
        self.assertEqual(first[0]['id'], second[0]['id'])
        self.assertEqual(tracker.update([], 1.4)[0]['id'], first[0]['id'])
        third = [detection([12, 12, 52, 52])]
        tracker.update(third, 1.6)
        self.assertNotEqual(first[0]['id'], third[0]['id'])

    def test_seek_and_class_change(self):
        tracker = Identities()
        first = [detection([10, 10, 50, 50])]; tracker.update(first, 5)
        second = [detection([10, 10, 50, 50], 'cell_group')]; tracker.update(second, 5.2)
        self.assertNotEqual(first[0]['id'], second[0]['id'])
        third = [detection([10, 10, 50, 50], 'cell_group')]; tracker.update(third, 2)
        self.assertNotEqual(second[0]['id'], third[0]['id'])
