"""SM-10 positioner operations. Edit command payloads and coordinate decoding here."""
import datetime
import math
import struct
from coordinates import finite
from limits import target
from links import members

def mask(addresses):
    result = bytearray(10)
    result[0] = 0xA0
    for address in addresses:
        if not 1 <= address <= 72:
            raise ValueError("SM-10 axis address must be 1–72.")
        result[9 - (address - 1) // 8] |= 1 << ((address - 1) % 8)
    return bytes(result)


class Positioner:
    def __init__(self, options, exchange):
        self.options = options
        self.exchange = exchange
        self.axes = {a["Id"].casefold(): a for a in options["Axes"]}
        if len(self.axes) != len(options["Axes"]):
            raise ValueError("Duplicate axis IDs.")
        for axis in self.axes.values():
            mask([axis["HardwareAxis"]])
        self.simulated = options["Transport"].casefold() == "simulated"
        self.positions = dict.fromkeys(self.axes, 0.0)
        self.saved_positions = {}

    def axis(self, name):
        try:
            return self.axes[name.casefold()]
        except KeyError:
            raise ValueError(f"Unknown axis '{name}'.") from None

    def validate(self, name, value):
        axis = self.axis(name)
        value = finite(value)
        low, high = axis["MinPosition"], axis["MaxPosition"]
        if not self.simulated:
            if not self.options["MotionEnabled"]:
                raise ValueError("Verify axis mapping and travel limits, then enable motion.")
            if low is None or high is None or finite(low) >= finite(high):
                raise ValueError("Real axes require verified finite travel limits.")
        if low is not None and value < low or high is not None and value > high:
            raise ValueError("Target is outside configured travel limits.")
        if abs(value) > 3.4028234663852886e38:
            raise ValueError("Target exceeds the SM-10 float range.")
        return value

    def read(self, name):
        axis = self.axis(name)
        if self.simulated:
            reference = user = self.positions[name.casefold()]
            moving, limit = False, 0
        else:
            address = bytes([axis["HardwareAxis"]])
            status = self.exchange(0x0120, address)
            if len(status) != 8:
                raise IOError("Invalid SM-10 status payload.")
            # German original: two reserved bytes; motor status is at index 6.
            moving, limit = status[6] != 0, status[0]
            reference = self.coordinate(self.exchange(0x0101, address))
            user = self.coordinate(self.exchange(0x0131, address))
        return {"AxisId": axis["Id"], "Reference": reference, "User": user,
                "IsMoving": moving, "StopSwitchActive": limit != 0,
                "Timestamp": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                "Limit": limit if limit <= 2 else 255}

    @staticmethod
    def coordinate(payload):
        if len(payload) != 4:
            raise IOError("Invalid SM-10 position payload.")
        value = struct.unpack("<f", payload)[0]
        if not math.isfinite(value):
            raise IOError("Non-finite SM-10 position.")
        return value

    def move(self, name, value, speed, relative=False):
        if relative:
            value = self.read(name)["Reference"] + finite(value)
        value = self.validate(name, value)
        if speed not in (0, 1):
            raise ValueError("Unknown movement speed.")
        if self.simulated:
            self.positions[name.casefold()] = value
        else:
            reply = self.exchange(0x0048 if speed == 1 else 0x0049,
                                  struct.pack("<Bf", self.axis(name)["HardwareAxis"], value))
            if reply:
                raise IOError("Unexpected movement acknowledgment.")

    def save_position(self, slot):
        if type(slot) is not int or not 1 <= slot <= 5:
            raise ValueError("Position slot must be an integer from 1 to 5.")
        if self.simulated:
            self.saved_positions[slot] = self.positions.copy()
        elif self.exchange(0xA10A, mask([a["HardwareAxis"] for a in self.axes.values()]) + bytes([slot])):
            raise IOError("Unexpected group-save acknowledgment.")

    def goto_position(self, slot, velocity):
        if type(slot) is not int or not 1 <= slot <= 5:
            raise ValueError("Position slot must be an integer from 1 to 5.")
        if type(velocity) is not int or not 1 <= velocity <= 15:
            raise ValueError("Preset velocity must be an integer from 1 to 15.")
        if self.simulated:
            if slot not in self.saved_positions:
                raise ValueError("Save this simulated slot before recalling it.")
            for name, value in self.saved_positions[slot].items():
                self.validate(name, value)
            self.positions.update(self.saved_positions[slot])
        else:
            # The protocol cannot read stored targets; validate current bounds and enablement.
            for axis in self.axes.values():
                self.validate(axis["Id"], self.read(axis["Id"])["Reference"])
            if self.exchange(0xA110, mask([a["HardwareAxis"] for a in self.axes.values()]) + bytes([slot, velocity])):
                raise IOError("Unexpected group-recall acknowledgment.")

    def call(self, method, args):
        if method == "save_position":
            return self.save_position(args["slot"])
        if method == "goto_position":
            return self.goto_position(args["slot"], args["velocity"])
        if method == "connect":
            # Read-only handshake; no enable, home, protocol switch or movement.
            return [self.read(axis["Id"]) for axis in self.axes.values()]
        if method == "read":
            return self.read(args["axis"])
        if method == "move":
            return self.move(args["axis"], args["value"], args["speed"], args["relative"])
        if method == "stop":
            axis = self.axis(args["axis"])
            if not self.simulated and self.exchange(0xA0FF, mask([axis["HardwareAxis"]])):
                raise IOError("Unexpected stop acknowledgment.")
            return None
        if method == "validate":
            return self.validate(args["axis"], args["value"])
        if method == "members":
            return members(args["device"], args["axis"], args["links"])
        if method == "target":
            return target(args["current"], args["info"], args["delta"], args["factor"])
        if method == "reference_target":
            current = self.read(args["axis"])
            return finite(finite(args["value"]) + current["Reference"] - current["User"])
        raise ValueError(f"Unknown backend method '{method}'.")


