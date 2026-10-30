"""Run with the bundled isolated Python; no pip or physical controller required."""
import importlib.util
import pathlib
import struct
import unittest

spec = importlib.util.spec_from_file_location(
    "backend", pathlib.Path(__file__).resolve().parents[1] / "GUI" / "PythonBackend" / "backend.py")
backend = importlib.util.module_from_spec(spec)
spec.loader.exec_module(backend)


class BackendChecks(unittest.TestCase):
    def test_mask_wire_order(self):
        self.assertEqual(backend.mask([1, 7]), bytes.fromhex("A0000000000000000041"))
        self.assertEqual(backend.mask([72]), bytes.fromhex("A0800000000000000000"))
        for address in (0, 73):
            with self.assertRaises(ValueError):
                backend.mask([address])

    def test_reject_malformed_coordinates(self):
        for payload in (b"", b"\0" * 5, struct.pack("<f", float("nan")), struct.pack("<f", float("inf"))):
            with self.assertRaises(OSError):
                backend.Positioner.coordinate(payload)

    def test_directional_limits(self):
        p = {"Reference": 10, "IsMoving": False, "StopSwitchActive": True, "Limit": 2}
        info = {"MinPosition": 0, "MaxPosition": 20}
        self.assertEqual(backend.target(p, info, -2, 1), {"Target": 8, "Direction": -1})
        with self.assertRaises(ValueError):
            backend.target(p, info, 2, 1)
        p["Limit"] = 255
        with self.assertRaises(ValueError):
            backend.target(p, info, -2, 1)
        p["StopSwitchActive"] = False
        with self.assertRaises(ValueError):
            backend.target(p, info, -20, 1)
        with self.assertRaises(ValueError):
            backend.target(p, info, 2, float("inf"))

    def test_links_conflicts_and_reverse(self):
        links = [{"Enabled": True, "Options": {"Source": "a", "Target": "b", "Bidirectional": True,
                 "AxisMap": [{"From": "X", "To": "Y", "Factor": -2}]}}]
        self.assertEqual(backend.members("b", "Y", links)[1], {"Device": "a", "Axis": "X", "Factor": -0.5})
        with self.assertRaises(ValueError):
            backend.members("a", "X", links * 2)
        links[0]["Options"]["AxisMap"][0]["Factor"] = 0
        with self.assertRaises(ValueError):
            backend.members("b", "Y", links)

    def test_read_order_and_move_encoding(self):
        calls = []

        def exchange(command, payload):
            calls.append((command, payload))
            if command == 0x0120:
                return bytes([2, 1, 0, 99, 111, 2, 1, 0])
            if command in (0x0101, 0x0131):
                return struct.pack("<f", 12580 if command == 0x0101 else -15329)
            return b""

        options = {"Transport": "serial", "MotionEnabled": True, "Axes": [
            {"Id": "X", "HardwareAxis": 7, "MinPosition": -20000, "MaxPosition": 20000}]}
        device = backend.Positioner(options, exchange)
        position = device.read("X")
        self.assertEqual(position["Reference"], 12580)
        self.assertEqual(position["User"], -15329)
        self.assertTrue(position["IsMoving"])
        self.assertEqual(position["Limit"], 2)
        self.assertEqual([c[0] for c in calls], [0x0120, 0x0101, 0x0131])
        device.move("X", 12.5, 0)
        self.assertEqual(calls[-1], (0x0049, bytes.fromhex("0700004841")))
        previous = len(calls)
        for value in (float("nan"), 20001):
            with self.assertRaises(ValueError):
                device.move("X", value, 1)
        self.assertEqual(len(calls), previous)
        self.assertEqual(device.call("reference_target", {"axis": "X", "value": -15319}), 12590)


if __name__ == "__main__":
    unittest.main()
