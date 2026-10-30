import asyncio
import unittest

from jogging import requests, PROFILES
from motion import MotionController, MotionCancelled
from sm10 import mask, Positioner


def position(value=0, limit=0, moving=False):
    return {"AxisId": "X", "Reference": value, "User": value, "IsMoving": moving,
            "StopSwitchActive": limit != 0, "Limit": limit}


class FakeHardware:
    """Test adapter that neither opens COM ports nor moves physical hardware."""
    def __init__(self):
        self.positions = {"stage": position(), "manipulator": position()}
        self.moves, self.stops, self.emergency = [], [], []
        self.memory = []
        self.entered = asyncio.Event()
        self.block_read = self.block_move = None

    async def __call__(self, op, **args):
        device = args["device"]
        if op in ("save_position", "goto_position"):
            self.memory.append((op, args))
        if op == "read":
            self.entered.set()
            if self.block_read is not None:
                await self.block_read.wait()
            return self.positions[device].copy()
        if op == "move":
            self.moves.append(args)
            self.entered.set()
            if self.block_move is not None:
                await self.block_move.wait()
        if op == "stop":
            self.stops.append(device)
        if op == "emergency":
            self.emergency.append(device)


class MotionChecks(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.hardware = FakeHardware()
        self.events = []
        devices = [{"Id": name, "State": 2, "Axes": [{"Id": "X", "MinPosition": -100, "MaxPosition": 100}]}
                   for name in ("stage", "manipulator")]
        self.engine = MotionController(devices, self.hardware, lambda name, data: self.events.append((name, data)))
        self.links = [{"Enabled": True, "Options": {"Source": "stage", "Target": "manipulator", "Bidirectional": True,
                       "AxisMap": [{"From": "X", "To": "X", "Factor": -2}]}}]

    async def asyncTearDown(self):
        await self.engine.close()

    async def move(self):
        await self.engine.move("stage", "X", 10, False, 1, self.links)

    async def test_global_stop_includes_untracked_axes(self):
        await self.engine.call("stop_all", {})
        self.assertEqual(self.hardware.stops, ["stage", "manipulator"])
        self.assertEqual(self.hardware.moves, [])

    async def test_global_stop_continues_after_failure(self):
        original = self.engine.host
        async def failing(op, **args):
            if op == "stop" and args["device"] == "stage":
                raise IOError("Stop failed")
            return await original(op, **args)
        self.engine.host = failing
        await self.engine.stop_all()
        self.assertIn("manipulator", self.hardware.stops)
        self.assertIn("stage", self.hardware.emergency)

    async def test_memory_save_is_scoped_and_idle(self):
        await self.engine.save_position("stage", 3)
        self.assertEqual(self.hardware.memory[0][1]["device"], "stage")
        self.assertEqual(self.hardware.memory[0][1]["slot"], 3)
        self.assertEqual(self.hardware.moves, [])
        self.hardware.positions["stage"] = position(moving=True)
        with self.assertRaises(ValueError):
            await self.engine.save_position("stage", 4)
        self.assertEqual(len(self.hardware.memory), 1)

    async def test_memory_recall_tracks_native_motion_and_stop(self):
        await self.engine.goto_position("stage", 2, 1)
        self.assertTrue(self.engine.is_busy("stage", "X"))
        self.assertEqual(self.hardware.memory[0][0], "goto_position")
        self.engine.feedback("stage", position(5, moving=True))
        self.engine.feedback("stage", position(10))
        self.assertFalse(self.engine.is_busy("stage", "X"))
        await self.engine.goto_position("stage", 2, 1)
        await self.engine.stop_all()
        self.assertIn("stage", self.hardware.stops)
        self.assertEqual(self.engine.active, [])

    async def test_memory_limit_stops_other_active_device(self):
        await self.engine.goto_position("stage", 2, 1)
        await self.engine.move("manipulator", "X", 10, False, 0, [])
        self.engine.feedback("stage", position(5, limit=2, moving=True))
        await self.engine.cancel()
        self.assertCountEqual(self.hardware.emergency, ["stage", "manipulator"])

    async def test_memory_recall_cancelled_during_preflight(self):
        self.hardware.block_read = asyncio.Event()
        moving = asyncio.create_task(self.engine.goto_position("stage", 2, 1))
        await self.hardware.entered.wait()
        stopping = asyncio.create_task(self.engine.cancel())
        await asyncio.sleep(0)
        self.hardware.block_read.set()
        with self.assertRaises(MotionCancelled):
            await moving
        await stopping
        self.assertEqual(self.hardware.memory, [])

    async def test_memory_rejects_invalid_slots_and_speed(self):
        for slot in (0, 6, True, 1.5):
            with self.assertRaises(ValueError):
                await self.engine.save_position("stage", slot)
        for velocity in (0, 16, True, 1.5):
            with self.assertRaises(ValueError):
                await self.engine.goto_position("stage", 1, velocity)
        self.assertEqual(self.hardware.memory, [])

    async def test_atomic_preflight(self):
        self.hardware.positions["manipulator"] = position(limit=1)
        with self.assertRaises(ValueError):
            await self.move()
        self.assertEqual(self.hardware.moves, [])

    async def test_linked_targets(self):
        await self.move()
        self.assertEqual([m["value"] for m in self.hardware.moves], [10, -20])

    async def test_release_during_read(self):
        self.hardware.block_read = asyncio.Event()
        moving = asyncio.create_task(self.move())
        await self.hardware.entered.wait()
        stopping = asyncio.create_task(self.engine.cancel())
        await asyncio.sleep(0)
        self.hardware.block_read.set()
        with self.assertRaises(MotionCancelled):
            await moving
        await stopping
        self.assertEqual(self.hardware.moves, [])

    async def test_release_during_ack(self):
        self.hardware.block_move = asyncio.Event()
        moving = asyncio.create_task(self.move())
        while not self.hardware.moves:
            await asyncio.sleep(0)
        stopping = asyncio.create_task(self.engine.cancel())
        await asyncio.sleep(0)
        self.hardware.block_move.set()
        with self.assertRaises(MotionCancelled):
            await moving
        await stopping
        self.assertEqual(len(self.hardware.moves), 1)
        self.assertEqual(self.hardware.stops, ["stage"])

    async def test_late_limit_on_arrived_leader(self):
        await self.move()
        self.engine.feedback("stage", position(10))
        self.engine.feedback("stage", position(10, limit=2))
        await self.engine.cancel()
        self.assertCountEqual(self.hardware.emergency, ["stage", "manipulator"])
        with self.assertRaises(ValueError):
            await self.move()

    async def test_idle_group_fault(self):
        await self.engine.move("stage", "X", 10, False, 1, [])
        self.engine.state("manipulator", 3)
        await self.engine.cancel()
        self.assertEqual(self.hardware.emergency, ["stage"])

    async def test_jog_uses_editable_profile(self):
        previous = PROFILES["H"].copy()
        try:
            PROFILES["H"] = {"multiplier": 3.0, "speed": 0}
            await self.engine.jog([{"Device": "stage", "Axis": "X", "Direction": 1}], 5, "H", [])
            self.assertEqual(self.hardware.moves[0]["value"], 15)
            self.assertEqual(self.hardware.moves[0]["speed"], 0)
        finally:
            PROFILES["H"] = previous


class FunctionChecks(unittest.TestCase):
    def test_opposite_keys_cancel(self):
        self.assertEqual(requests([{"Device": "stage", "Axis": "X", "Direction": d} for d in (1, -1)], 5, "L"), [])

    def test_stop_mask(self):
        self.assertEqual(mask([1, 7]).hex(), "a0000000000000000041")

    def test_memory_wire_payloads_and_simulation(self):
        calls = []
        options = {"Transport": "sm10", "MotionEnabled": True,
                   "Axes": [{"Id": name, "HardwareAxis": address, "MinPosition": -100, "MaxPosition": 100}
                            for name, address in (("X", 7), ("Y", 8), ("Z", 9))]}
        def exchange(command, payload):
            calls.append((command, payload))
            if command == 0x0120:
                return bytes(8)
            if command in (0x0101, 0x0131):
                return bytes(4)
            return b""
        driver = Positioner(options, exchange)
        driver.save_position(5)
        self.assertEqual(calls[-1], (0xA10A, mask([7, 8, 9]) + bytes([5])))
        driver.goto_position(5, 3)
        self.assertEqual(calls[-1], (0xA110, mask([7, 8, 9]) + bytes([5, 3])))
        options["Transport"] = "simulated"
        driver = Positioner(options, exchange)
        with self.assertRaises(ValueError):
            driver.goto_position(1, 1)
        driver.positions["x"] = 25
        driver.save_position(1)
        driver.positions["x"] = 0
        driver.goto_position(1, 1)
        self.assertEqual(driver.positions["x"], 25)

    def test_invalid_coordinate(self):
        with self.assertRaises(OSError):
            Positioner.coordinate(b"\0")
