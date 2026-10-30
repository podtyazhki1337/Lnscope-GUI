"""Persistent observation bookmarks and calibrated XY geometry; never sends motion."""
import copy
import json
import math
from pathlib import Path
import uuid


def number(value):
    value = float(value)
    if not math.isfinite(value):
        raise ValueError("Coordinates and timestamps must be finite.")
    return value


def project_xy(pixel, stage_xy, calibration):
    """Return sample XY using measured pixel-to-sample and stage-to-sample matrices.

    Both matrices require calibration; signs are not inferred from axis names.
    Z is a focus observation only and is never inferred from a 2D mask.
    """
    p = [number(pixel[i])-number(calibration["origin_px"][i]) for i in range(2)]
    s = [number(stage_xy[i]) for i in range(2)]
    a, b = calibration["pixel_to_sample"], calibration["stage_to_sample"]
    for matrix in (a, b):
        if len(matrix) != 2 or any(len(row) != 2 for row in matrix):
            raise ValueError("XY calibration requires 2 by 2 matrices.")
        determinant = number(matrix[0][0])*number(matrix[1][1])-number(matrix[0][1])*number(matrix[1][0])
        if abs(determinant) < 1e-12:
            raise ValueError("XY calibration is singular.")
    return [sum(number(a[i][j])*p[j]+number(b[i][j])*s[j] for j in range(2)) for i in range(2)]


class ObjectMap:
    def __init__(self):
        self.data = {"schema": 1, "session_id": str(uuid.uuid4()), "objects": []}

    @staticmethod
    def validate_snapshot(snapshot):
        if not snapshot:
            raise ValueError("Connect a positioner and read its coordinates first.")
        for device in snapshot:
            if not device.get("device_id") or not device.get("axes"):
                raise ValueError("A snapshot requires named devices and axes.")
            for axis in device["axes"]:
                if axis.get("moving"):
                    raise ValueError("Stop movement before saving an observation.")
                for key in ("reference", "display", "read_time_ms"):
                    number(axis[key])
                if not axis.get("id") or not axis.get("unit"):
                    raise ValueError("Axis identity and units are required.")

    def call(self, method, args):
        if method == "map_add":
            name = str(args["name"]).strip()
            kind = args["kind"]
            if not name or len(name) > 120 or kind not in ("cell", "cell_group", "spheroid", "pipette", "region"):
                raise ValueError("Enter an object name and a supported type.")
            snapshot = copy.deepcopy(args["snapshot"])
            self.validate_snapshot(snapshot)
            self.data["objects"].append({"id": str(uuid.uuid4()), "name": name, "kind": kind,
                "snapshot": snapshot, "status": "last_observed_view", "note": str(args.get("note", ""))[:2000],
                "coordinate_frame": "controller_C1", "localization": "view_bookmark_not_object_xyz"})
        elif method == "map_invalidate":
            for obj in self.data["objects"]:
                obj["status"] = "needs_reacquisition"
        elif method == "map_remove":
            self.data["objects"] = [o for o in self.data["objects"] if o["id"] != args["id"]]
        elif method == "map_save":
            path = Path(args["path"])
            temporary = path.with_suffix(path.suffix + ".tmp")
            temporary.write_text(json.dumps(self.data, indent=2, allow_nan=False), encoding="utf-8")
            temporary.replace(path)
        elif method == "map_load":
            candidate = json.loads(Path(args["path"]).read_text(encoding="utf-8"))
            if candidate.get("schema") != 1 or not isinstance(candidate.get("objects"), list):
                raise ValueError("Unsupported object-map file.")
            ids = set()
            for obj in candidate["objects"]:
                self.validate_snapshot(obj["snapshot"])
                if not obj.get("id") or obj["id"] in ids or not obj.get("name") or not obj.get("kind"):
                    raise ValueError("Invalid object identity.")
                ids.add(obj["id"])
                # Imported positions cannot establish the current hardware reference or object identity.
                obj["status"] = "needs_reacquisition"
            self.data = candidate
        elif method != "map_list":
            raise ValueError("Unknown object-map method.")
        return copy.deepcopy(self.data)
