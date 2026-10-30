"""Object-map persistence and coordinate-frame checks without hardware."""
import json
from pathlib import Path
import tempfile
import unittest
from object_map import ObjectMap, project_xy


class ObjectMapChecks(unittest.TestCase):
    def snapshot(self):
        return [{"device_id": "stage", "axes": [{"id": "X", "unit": "um", "reference": 120,
            "display": 0, "read_time_ms": 1000, "moving": False}]}]

    def test_save_load_preserves_reference_and_invalidates_identity(self):
        store = ObjectMap()
        store.call("map_add", dict(name="Cell 1", kind="cell", snapshot=self.snapshot()))
        with tempfile.TemporaryDirectory() as folder:
            path = str(Path(folder) / "map.json")
            store.call("map_save", {"path": path})
            loaded = ObjectMap().call("map_load", {"path": path})
        obj = loaded["objects"][0]
        self.assertEqual(obj["status"], "needs_reacquisition")
        self.assertEqual(obj["snapshot"][0]["axes"][0]["reference"], 120)
        self.assertEqual(obj["snapshot"][0]["axes"][0]["display"], 0)

    def test_movement_and_nan_are_rejected(self):
        for key, value in (("moving", True), ("reference", float("nan"))):
            snapshot = self.snapshot()
            snapshot[0]["axes"][0][key] = value
            with self.assertRaises(ValueError):
                ObjectMap().call("map_add", dict(name="Cell", kind="cell", snapshot=snapshot))

    def test_stage_motion_compensates_and_cell_motion_remains(self):
        calibration = dict(origin_px=[320, 240], pixel_to_sample=[[2, 0], [0, -2]],
                           stage_to_sample=[[1, 0], [0, 1]])
        before = project_xy([330, 250], [0, 0], calibration)
        after = project_xy([325, 250], [10, 0], calibration)
        self.assertEqual(before, after)
        shifted_cell = project_xy([328, 250], [10, 0], calibration)
        self.assertEqual(shifted_cell[0]-before[0], 6)

    def test_failed_import_does_not_replace_current_map(self):
        store = ObjectMap()
        before = store.call("map_add", dict(name="Cell", kind="cell", snapshot=self.snapshot()))
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "bad.json"
            path.write_text(json.dumps({"schema": 1, "objects": [{"snapshot": []}]}))
            with self.assertRaises(ValueError):
                store.call("map_load", {"path": str(path)})
        self.assertEqual(store.call("map_list", {}), before)


if __name__ == "__main__":
    unittest.main()
