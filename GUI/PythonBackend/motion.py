"""Main LNscope motion logic, independent of WPF, COM and installed vendor SDKs.

Edit linked moves, preflight, key release, end-switch handling, target completion
and feedback loss handling here. host(op, **args) is the asynchronous hardware
adapter; emit publishes UI updates. All state changes share one asyncio event
loop, so pending IO does not block Stop processing.
"""
import asyncio
import time
from dataclasses import dataclass, field

from coordinates import finite
from limits import blocks, target
from links import members
from jogging import PROFILES, requests

FEEDBACK_TIMEOUT = 3.0
STOPPED_BEFORE_TARGET_GRACE = 1.0
POSITION_TOLERANCE = 0.05
WATCH_INTERVAL = 0.25


class MotionCancelled(Exception):
    pass


@dataclass
class Member:
    device: str
    axis: str
    factor: float
    generation: int = 0
    target: float = 0
    direction: float = 0
    sent: bool = False
    done: bool = False
    feedback: float = field(default_factory=time.monotonic)
    sent_at: float = 0
    native_preset: bool = False
    seen_moving: bool = False


@dataclass(eq=False)
class Batch:
    members: list
    cancelled: bool = False


class MotionController:
    def __init__(self, devices, host, emit):
        self.devices = {d["Id"]: d for d in devices}
        self.host, self.emit = host, emit
        self.active = []
        self.dispatch = asyncio.Lock()
        self.tripped = False
        self.stopping = None
        self.polls = {}
        self.watch = asyncio.create_task(self.watchdog())
        for device in self.devices.values():
            self.update_poll(device)

    def update_poll(self, device):
        if not device.get("PythonFeedback", False):
            return
        previous = self.polls.get(device["Id"])
        if device["State"] == 2:
            if previous is None or previous.done():
                self.polls[device["Id"]] = asyncio.create_task(self.poll(device))
        elif previous is not None:
            previous.cancel()

    async def poll(self, device):
        """Edit polling order and interval here; the adapter serializes the shared COM port."""
        generation = device.get("Generation", 0)
        try:
            while device["State"] == 2:
                await asyncio.sleep(max(0.1, min(2.0, device["FeedbackIntervalMs"] / 1000)))
                for axis in device["Axes"]:
                    position = await self.host("read", device=device["Id"], axis=axis["Id"], generation=generation)
                    self.feedback(device["Id"], position)
                    self.emit("position", {"device": device["Id"], "position": position, "generation": generation})
        except asyncio.CancelledError:
            pass
        except Exception as error:
            if device["State"] != 2 or device.get("Generation", 0) != generation:
                return
            if self.active:
                self.trip(f"{device['Id']}: position polling failed: {error}")
            try:
                await self.host("emergency", device=device["Id"], generation=generation)
            except Exception as stop_error:
                self.emit("log", {"message": f"Polling/Stop error: {stop_error}"})

    def snapshot(self):
        self.emit("motion", {"busy": [{"device": m.device, "axis": m.axis}
                                     for b in self.active for m in b.members],
                             "stopping": self.stopping is not None and not self.stopping.done(),
                             "tripped": self.tripped})

    def is_busy(self, device, axis):
        return any(m.device == device and m.axis == axis for b in self.active for m in b.members)

    def info(self, member):
        return next(a for a in self.devices[member.device]["Axes"] if a["Id"] == member.axis)

    def check_cancelled(self, batch):
        if batch.cancelled:
            raise MotionCancelled("Movement cancelled.")

    async def move(self, device, axis, value, absolute, speed, links):
        finite(value)
        if self.tripped or self.stopping is not None and not self.stopping.done():
            raise ValueError("Motion stopped. Disconnect and reconnect controller groups before resuming.")
        batch = Batch([Member(m["Device"], m["Axis"], m["Factor"]) for m in members(device, axis, links)])
        for member in batch.members:
            self.info(member)  # Reject unknown devices/axes before reserving anything.
            member.generation = self.devices[member.device].get("Generation", 0)
            if self.is_busy(member.device, member.axis):
                raise ValueError("Wait for the current movement to finish.")
        self.active.append(batch)
        self.snapshot()
        try:
            async with self.dispatch:
                delta = value
                # Validate ALL members before the first motor command.
                for index, member in enumerate(batch.members):
                    self.check_cancelled(batch)
                    if self.devices[member.device]["State"] != 2:  # Ready
                        raise ValueError(f"{member.device} is not ready.")
                    current = await self.host("read", device=member.device, axis=member.axis, generation=member.generation)
                    self.check_cancelled(batch)
                    if index == 0 and absolute:
                        delta = value - current["Reference"]
                    planned = target(current, self.info(member), delta, member.factor)
                    member.target, member.direction = planned["Target"], planned["Direction"]
                    await self.host("validate", device=member.device, axis=member.axis, value=member.target, generation=member.generation)
                for member in batch.members:
                    self.check_cancelled(batch)
                    member.sent = True
                    member.sent_at = member.feedback = time.monotonic()
                    await self.host("move", device=member.device, axis=member.axis, value=member.target, speed=speed, generation=member.generation)
                    self.check_cancelled(batch)
        except Exception as error:
            if any(m.sent for m in batch.members):
                if not batch.cancelled:
                    self.trip(f"Motion error: {error}")
            elif batch in self.active:
                self.active.remove(batch)
                self.snapshot()
            raise

    async def jog(self, bindings, step, rate, links):
        if self.tripped or self.stopping is not None and not self.stopping.done():
            return
        for request in requests(bindings, step, rate):
            if not self.is_busy(request["device"], request["axis"]):
                await self.move(**request, links=links)

    def feedback(self, device, position):
        reason = None
        now = time.monotonic()
        for batch in self.active[:]:
            for member in batch.members:
                if member.device != device or member.axis != position["AxisId"] or not member.sent:
                    continue
                # Include members already at target until every follower has arrived.
                member.feedback = now
                info = self.info(member)
                if member.native_preset:
                    # Native memory is opaque: observe both switches and both software bounds.
                    if (position["StopSwitchActive"] or
                            info["MinPosition"] is not None and position["Reference"] <= info["MinPosition"] or
                            info["MaxPosition"] is not None and position["Reference"] >= info["MaxPosition"]):
                        reason = f"{device}.{member.axis}: preset reached a limit; stopping all active motion"
                    member.seen_moving |= position["IsMoving"]
                    member.done = not position["IsMoving"] and (member.seen_moving or now - member.sent_at > STOPPED_BEFORE_TARGET_GRACE)
                    continue
                if (blocks(position, member.direction) or
                        member.direction < 0 and info["MinPosition"] is not None and position["Reference"] <= info["MinPosition"] or
                        member.direction > 0 and info["MaxPosition"] is not None and position["Reference"] >= info["MaxPosition"]):
                    reason = f"{device}.{member.axis}: limit reached; stopping all active motion"
                if not position["IsMoving"] and abs(position["Reference"] - member.target) <= POSITION_TOLERANCE:
                    member.done = True
                elif not member.done and not position["IsMoving"] and now - member.sent_at > STOPPED_BEFORE_TARGET_GRACE:
                    reason = f"{device}.{member.axis}: stopped before target; stopping linked motion"
            if all(m.done for m in batch.members) and reason is None:
                self.active.remove(batch)
                self.snapshot()
        if reason is not None:
            self.trip(reason)

    def state(self, device, state, generation=0):
        self.devices[device]["State"] = state
        self.devices[device]["Generation"] = generation
        self.update_poll(self.devices[device])
        if state in (0, 3) and self.active:
            self.trip(f"{device}: feedback lost or disconnected")
        if (self.stopping is None or self.stopping.done()) and all(d["State"] == 2 for d in self.devices.values()):
            self.tripped = False
            self.snapshot()

    async def watchdog(self):
        while True:
            await asyncio.sleep(WATCH_INTERVAL)
            if any(m.sent and not m.done and time.monotonic() - m.feedback > FEEDBACK_TIMEOUT
                   for b in self.active if not b.cancelled for m in b.members):
                self.trip("No fresh position feedback; stopping active motion")

    def trip(self, reason):
        if self.tripped:
            return
        self.tripped = True
        batches = self.active[:]
        for batch in batches:
            batch.cancelled = True
        self.stopping = asyncio.create_task(self.stop_batches(batches, emergency=True))
        self.snapshot()
        self.emit("halted", {"reason": reason + ". Reconnect controller groups to reset."})

    async def cancel(self):
        if self.tripped or self.stopping is not None and not self.stopping.done():
            if self.stopping is not None:
                await self.stopping
            return
        batches = self.active[:]
        if not batches:
            return
        for batch in batches:
            batch.cancelled = True
        self.stopping = asyncio.create_task(self.stop_batches(batches, emergency=False))
        self.snapshot()
        await self.stopping

    async def goto_position(self, device, slot, velocity):
        if type(slot) is not int or not 1 <= slot <= 5:
            raise ValueError("Position slot must be an integer from 1 to 5.")
        if type(velocity) is not int or not 1 <= velocity <= 15:
            raise ValueError("Preset velocity must be an integer from 1 to 15.")
        if self.tripped or self.stopping is not None and not self.stopping.done():
            raise ValueError("Motion is stopped; reconnect controller groups.")
        info = self.devices[device]
        if info["State"] != 2:
            raise ValueError("Connect the device before recalling a position.")
        batch = Batch([Member(device, a["Id"], 1, generation=info.get("Generation", 0), native_preset=True)
                       for a in info["Axes"]])
        if any(self.is_busy(device, m.axis) for m in batch.members):
            raise ValueError("Wait for the current movement to finish.")
        self.active.append(batch)
        self.snapshot()
        try:
            async with self.dispatch:
                for member in batch.members:
                    self.check_cancelled(batch)
                    current = await self.host("read", device=device, axis=member.axis, generation=member.generation)
                    self.check_cancelled(batch)
                    if current["IsMoving"] or current["StopSwitchActive"]:
                        raise ValueError("Stop the device and move away from end switches before recalling a slot.")
                    await self.host("validate", device=device, axis=member.axis, value=current["Reference"], generation=member.generation)
                self.check_cancelled(batch)
                for member in batch.members:
                    member.sent = True
                    member.sent_at = member.feedback = time.monotonic()
                await self.host("goto_position", device=device, slot=slot, velocity=velocity, generation=batch.members[0].generation)
                self.check_cancelled(batch)
            self.emit("log", {"message": f"{device}: Go to controller slot {slot} sent (A110, no ACK)."})
        except Exception as error:
            if any(m.sent for m in batch.members):
                if not batch.cancelled:
                    self.trip(f"Preset movement error: {error}")
            elif batch in self.active:
                self.active.remove(batch)
                self.snapshot()
            raise

    async def save_position(self, device, slot):
        if type(slot) is not int or not 1 <= slot <= 5:
            raise ValueError("Position slot must be an integer from 1 to 5.")
        info = self.devices[device]
        generation = info.get("Generation", 0)

        def check_idle():
            if (info["State"] != 2 or info.get("Generation", 0) != generation or
                    self.tripped or self.stopping is not None and not self.stopping.done() or self.active):
                raise ValueError("Stop motion and connect the device before saving a position.")

        check_idle()
        async with self.dispatch:
            check_idle()
            for axis in info["Axes"]:
                current = await self.host("read", device=device, axis=axis["Id"], generation=generation)
                check_idle()
                if current["IsMoving"]:
                    raise ValueError("The device is moving; position was not saved.")
            await self.host("save_position", device=device, slot=slot, generation=generation)
        self.emit("log", {"message": f"{device}: save slot {slot} sent (A10A, no ACK)."})

    async def stop_all(self):
        if self.stopping is not None and not self.stopping.done():
            await self.stopping
        batches = self.active[:]
        for batch in batches:
            batch.cancelled = True
        # Include untracked axes, such as motion initiated from the hardware panel.
        batches.append(Batch([Member(d["Id"], a["Id"], 1,
                                     generation=d.get("Generation", 0), sent=True)
                              for d in self.devices.values() if d["State"] == 2
                              for a in d["Axes"]], cancelled=True))
        self.stopping = asyncio.create_task(self.stop_batches(batches, emergency=False))
        self.snapshot()
        await self.stopping

    async def stop(self, device, axis):
        await self.cancel()
        if self.devices[device]["State"] == 2:
            await self.host("stop", device=device, axis=axis, generation=self.devices[device].get("Generation", 0))

    async def stop_batches(self, batches, emergency):
        async def send():
            grouped = {}
            for batch in batches:
                for member in batch.members:
                    grouped.setdefault(member.device, {})[member.axis] = member
            # Try every group even if the first Stop fails.
            for device, axes in grouped.items():
                generation = next(iter(axes.values())).generation
                try:
                    if emergency:
                        await self.host("emergency", device=device, generation=generation)
                    else:
                        for axis, member in axes.items():
                            if member.sent:
                                await self.host("stop", device=device, axis=axis, generation=generation)
                except Exception as error:
                    self.emit("log", {"message": f"STOP {device}: {error}"})
                    if not emergency:
                        self.trip(f"Stop failed: {error}")
                    try:
                        await self.host("emergency", device=device, generation=generation)
                    except Exception as stop_error:
                        self.emit("log", {"message": f"Emergency stop failed: {stop_error}"})
        try:
            if emergency:
                await send()
            else:
                # Finish an in-flight ACK/read before ordinary Stop; suppress unsent followers.
                async with self.dispatch:
                    await send()
        finally:
            for batch in batches:
                if batch in self.active:
                    self.active.remove(batch)
            # Task.done() changes after returning to the loop.
            asyncio.get_running_loop().call_soon(self.snapshot)

    async def close(self):
        await self.cancel()
        self.watch.cancel()
        for poll in self.polls.values():
            poll.cancel()

    async def call(self, method, args):
        if method == "save_position":
            return await self.save_position(**args)
        if method == "goto_position":
            return await self.goto_position(**args)
        if method == "move":
            return await self.move(**args)
        if method == "jog":
            return await self.jog(**args)
        if method == "cancel":
            return await self.cancel()
        if method == "stop_all":
            return await self.stop_all()
        if method == "stop":
            return await self.stop(**args)
        if method == "feedback":
            return self.feedback(**args)
        if method == "state":
            return self.state(**args)
        if method == "close":
            return await self.close()
        if method == "ping":
            return True
        raise ValueError(f"Unknown motion method: {method}")
