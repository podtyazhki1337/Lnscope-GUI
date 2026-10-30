"""Editable SM-10 command catalogue, parameter encoding and reply interpretation.

Source: SM10 Interface description V1.0 pp. 33-34; German original resolves
status layout and slow-speed reply size. C# retains independent wire guards.
"""
import base64
import binascii
import math
import struct
from dataclasses import dataclass
from sm10 import mask


@dataclass(frozen=True)
class Command:
    id: int
    name: str
    payload: str = "Axis"
    reply: str = "Ack"
    read: bool = False
    move: bool = False
    minimum: float = 0
    maximum: float = 15


COMMANDS = [
    Command(0x0012, 'Fast positive', 'Axis', 'Ack', False, True, 0, 15),
    Command(0x0013, 'Fast negative', 'Axis', 'Ack', False, True, 0, 15),
    Command(0x0014, 'Slow positive', 'Axis', 'Ack', False, True, 0, 15),
    Command(0x0015, 'Slow negative', 'Axis', 'Ack', False, True, 0, 15),
    Command(0x0022, 'Home return', 'Axis', 'Ack', False, True, 0, 15),
    Command(0x0024, 'Go to zero', 'Axis', 'Ack', False, True, 0, 15),
    Command(0x0034, 'Power off', 'Axis', 'Ack', False, False, 0, 15),
    Command(0x0035, 'Power on', 'Axis', 'Ack', False, False, 0, 15),
    Command(0x003A, 'Set ramp length', 'Byte', 'Ack', False, False, 0, 15),
    Command(0x003C, 'Set slow positioning speed (Вµsteps/s)', 'UInt16', 'Ack', False, False, 1, 17999),
    Command(0x003D, 'Set fast positioning speed (steps/s)', 'UInt16', 'Ack', False, False, 1, 2999),
    Command(0x0048, 'Absolute fast', 'Float', 'Ack', False, True, -3.4028234663852886e+38, 3.4028234663852886e+38),
    Command(0x0049, 'Absolute slow', 'Float', 'Ack', False, True, -3.4028234663852886e+38, 3.4028234663852886e+38),
    Command(0x004A, 'Relative fast', 'Float', 'Ack', False, True, -3.4028234663852886e+38, 3.4028234663852886e+38),
    Command(0x004B, 'Relative slow', 'Float', 'Ack', False, True, -3.4028234663852886e+38, 3.4028234663852886e+38),
    Command(0x00F0, 'Zero position counter', 'Axis', 'Ack', False, False, 0, 15),
    Command(0x00FF, 'Stop', 'Axis', 'Ack', False, False, 0, 15),
    Command(0x0101, 'Read position', 'Axis', 'Float', True, False, 0, 15),
    Command(0x0104, 'Home', 'Axis', 'Ack', False, True, 0, 15),
    Command(0x010A, 'Save position', 'Byte', 'Ack', False, False, 1, 5),
    Command(0x0110, 'Go to saved position', 'Byte', 'Ack', False, True, 1, 5),
    Command(0x011E, 'Read power status', 'Axis', 'Byte', True, False, 0, 15),
    Command(0x011F, 'Read output-stage presence', 'Axis', 'Byte', True, False, 0, 15),
    Command(0x0120, 'Read main status', 'Axis', 'Status', True, False, 0, 15),
    Command(0x012F, 'Read fast movement speed', 'Axis', 'Byte', True, False, 0, 15),
    Command(0x0130, 'Read slow movement speed', 'Axis', 'Byte', True, False, 0, 15),
    Command(0x0131, 'Read counter 2', 'Axis', 'Float', True, False, 0, 15),
    Command(0x0132, 'Zero counter 2', 'Counter2', 'Ack', False, False, 0, 15),
    Command(0x0134, 'Set fast movement speed', 'Byte', 'Ack', False, False, 0, 15),
    Command(0x0135, 'Set slow movement speed', 'Byte', 'Ack', False, False, 0, 15),
    Command(0x0138, 'Read home speed', 'Axis', 'Byte', True, False, 0, 15),
    Command(0x0139, 'Set home speed', 'Byte', 'Ack', False, False, 0, 15),
    Command(0x013C, 'Set home direction', 'Byte', 'Ack', False, False, 0, 1),
    Command(0x013D, 'Read home direction', 'Axis', 'Byte', True, False, 0, 15),
    Command(0x013F, 'Home abort', 'Axis', 'Ack', False, False, 0, 15),
    Command(0x0140, 'Step increment', 'Axis', 'Ack', False, True, 0, 15),
    Command(0x0141, 'Step decrement', 'Axis', 'Ack', False, True, 0, 15),
    Command(0x0143, 'Read fast positioning speed', 'Axis', 'Byte', True, False, 0, 15),
    Command(0x0144, 'Set fast positioning speed', 'Byte', 'Ack', False, False, 0, 15),
    Command(0x0146, 'Set handwheel resolution', 'Byte', 'Ack', False, False, 1, 255),
    Command(0x0147, 'Single steps', 'SignedByte', 'Ack', False, True, -127, 127),
    Command(0x014B, 'Read motor type', 'Axis', 'Byte', True, False, 0, 15),
    Command(0x014D, 'Read spindle pitch', 'Axis', 'Byte', True, False, 0, 15),
    Command(0x0158, 'Set step speed', 'Byte', 'Ack', False, False, 0, 15),
    Command(0x0159, 'Read step speed', 'Axis', 'Byte', True, False, 0, 15),
    Command(0x0160, 'Read fast positioning speed (steps/s)', 'Axis', 'UInt16', True, False, 0, 15),
    Command(0x0161, 'Read slow positioning speed (Вµsteps/s)', 'Axis', 'UInt16', True, False, 0, 15),
    Command(0x018F, 'Set slow positioning speed', 'Byte', 'Ack', False, False, 0, 15),
    Command(0x0190, 'Read slow positioning speed', 'Axis', 'Byte', True, False, 0, 15),
    Command(0x0191, 'Set positioning speed mode', 'Byte', 'Ack', False, False, 0, 1),
    Command(0x0192, 'Read speed mode', 'Axis', 'Byte', True, False, 0, 15),
    Command(0x019F, 'Set proportional factor', 'SignedByte', 'Ack', False, False, -127, 127),
    Command(0x01A2, 'Read proportional factor', 'Axis', 'Byte', True, False, 0, 15),
    Command(0x01E8, 'Trackball (ambiguous documentation)', 'Ambiguous', 'Ack', False, True, 0, 15),
    Command(0x042F, 'Slow ramp off', 'Axis', 'Ack', False, False, 0, 15),
    Command(0x0430, 'Slow ramp on', 'Axis', 'Ack', False, False, 0, 15),
    Command(0x0431, 'Read slow ramp state', 'Axis', 'Byte', True, False, 0, 15),
    Command(0x044F, 'Set step distance', 'Float', 'Ack', False, False, 0, 3.4028234663852886e+38),
    Command(0xA012, 'Group fast positive', 'MaskByte', 'None', False, True, 0, 15),
    Command(0xA013, 'Group fast negative', 'MaskByte', 'None', False, True, 0, 15),
    Command(0xA014, 'Group slow positive', 'MaskByte', 'None', False, True, 0, 15),
    Command(0xA015, 'Group slow negative', 'MaskByte', 'None', False, True, 0, 15),
    Command(0xA022, 'Group home return', 'MaskByte', 'None', False, True, 0, 15),
    Command(0xA024, 'Group go to zero', 'MaskByte', 'None', False, True, 0, 15),
    Command(0xA034, 'Group power off', 'Mask', 'None', False, False, 0, 15),
    Command(0xA035, 'Group power on', 'Mask', 'None', False, False, 0, 15),
    Command(0xA048, 'Four-axis absolute fast', 'FourPositions', 'None', False, True, 0, 15),
    Command(0xA049, 'Four-axis absolute slow', 'FourPositions', 'None', False, True, 0, 15),
    Command(0xA04A, 'Four-axis relative fast', 'FourPositions', 'None', False, True, 0, 15),
    Command(0xA04B, 'Four-axis relative slow', 'FourPositions', 'None', False, True, 0, 15),
    Command(0xA0F0, 'Group zero position', 'Mask', 'None', False, False, 0, 15),
    Command(0xA0FF, 'Group stop', 'Mask', 'None', False, False, 0, 15),
    Command(0xA101, 'Four-axis positions', 'FourAxes', 'GroupPositions', True, False, 0, 15),
    Command(0xA104, 'Group home', 'MaskByte', 'None', False, True, 0, 15),
    Command(0xA10A, 'Group save position', 'MaskByte', 'None', False, False, 1, 5),
    Command(0xA110, 'Group go to saved position', 'MaskTwoBytes', 'None', False, True, 0, 15),
    Command(0xA120, 'Four-axis status', 'FourAxes', 'GroupStatus', True, False, 0, 15),
    Command(0xA131, 'Four-axis counters 2', 'FourAxes', 'GroupPositions', True, False, 0, 15),
    Command(0xA132, 'Group zero counter 2', 'Mask', 'None', False, False, 0, 15),
    Command(0xA13F, 'Group home abort', 'Mask', 'None', False, False, 0, 15),
    Command(0xA140, 'Group step increment', 'MaskStep', 'None', False, True, 0, 15),
    Command(0xA141, 'Group step decrement', 'MaskStep', 'None', False, True, 0, 15),
]
CATALOGUE = {c.id: c for c in COMMANDS}


def get(command):
    if type(command) is not int or command not in CATALOGUE:
        raise ValueError("Unknown documented command.")
    return CATALOGUE[command]


def integer(value, minimum, maximum):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value != int(value) or not minimum <= value <= maximum:
        raise ValueError(f"Enter an integer from {minimum:g} to {maximum:g}.")
    return int(value)


def build(command, axes, values):
    spec = get(command)
    if spec.payload == "Ambiguous":
        raise ValueError("Trackball 01E8 is blocked: documented payload lengths disagree.")
    if not axes or len(set(axes)) != len(axes):
        raise ValueError("Select distinct axis addresses.")
    axes = [integer(a, 1, 72) for a in axes]
    kind = spec.payload
    group = kind.startswith("Mask")
    four = kind in ("FourAxes", "FourPositions")
    if four and (len(axes) != 4 or len({(a - 1) // 18 for a in axes}) != 1):
        raise ValueError("Select exactly four axes within one controller (1–18, 19–36, 37–54 or 55–72).")
    if not group and not four and len(axes) != 1:
        raise ValueError("This command takes one axis.")
    count = {"Byte": 1, "SignedByte": 1, "UInt16": 1, "Float": 1, "MaskByte": 1,
             "MaskTwoBytes": 2, "MaskStep": 2, "FourPositions": 4}.get(kind, 0)
    if len(values) != count or any(isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) for v in values):
        raise ValueError(f"Expected {count} finite numeric value(s).")
    data = mask(axes) if group else bytes([0xA0, *axes]) if four else bytes(axes)
    def floating(value):
        if abs(value) > 3.4028234663852886e38:
            raise ValueError("Value exceeds float32 range.")
        return struct.pack("<f", value)
    if kind == "Counter2":
        data += bytes([2])
    elif kind in ("Byte", "SignedByte", "MaskByte"):
        data += bytes([integer(values[0], spec.minimum, spec.maximum) & 255])
    elif kind == "UInt16":
        data += struct.pack("<H", integer(values[0], spec.minimum, spec.maximum))
    elif kind == "Float":
        if not spec.minimum <= values[0] <= spec.maximum:
            raise ValueError("Value outside the documented range.")
        data += floating(values[0])
    elif kind == "MaskTwoBytes":
        data += bytes([integer(values[0], 1, 5), integer(values[1], 0, 15)])
    elif kind == "MaskStep":
        if values[1] < 0:
            raise ValueError("Step distance must be non-negative.")
        data += bytes([integer(values[0], 0, 15)]) + floating(values[1])
    elif kind == "FourPositions":
        data += b"".join(floating(v) for v in values)
    return data


def frame(command, data):
    return bytes([0x16]) + struct.pack(">HB", command, len(data)) + data + struct.pack(">H", binascii.crc_hqx(data, 0))


def category(spec):
    code = spec.id & 0x0FFF
    if spec.payload == "Ambiguous":
        return "Unavailable / documentation"
    if spec.read:
        return "Read positions and configuration"
    if code in (0x22, 0x104, 0x13F, 0x139, 0x13C):
        return "Home and reference"
    if code in (0xF0, 0x132, 0x10A, 0x110, 0x24):
        return "Counters and saved positions"
    if code in (0x34, 0x35, 0xFF):
        return "Power and Stop"
    if spec.move:
        return "Movement and steps"
    return "Speed and axis settings"


def help_text(spec):
    kind = spec.payload
    if kind == "Ambiguous":
        return "Blocked: trackball payload is described as both one and two bytes. Firmware clarification required."
    axes = "Selected axes (1–72)." if kind.startswith("Mask") else "Exactly four axes in one controller." if kind.startswith("Four") else "One axis (1–72)."
    values = {"Axis": "No values.", "Counter2": "No values; counter selector 2 is included.", "Mask": "No values.",
              "FourAxes": "No values.", "FourPositions": "Four distances / coordinates in µm, in axis order.",
              "MaskTwoBytes": "Values: slot (1–5); velocity (0–15).", "MaskStep": "Values: velocity (0–15); distance (µm, non-negative)."}.get(kind)
    if values is None:
        unit = "µm" if kind == "Float" else "integer"
        if spec.id == 0x003C: unit = "µsteps/s"
        if spec.id == 0x003D: unit = "steps/s"
        values = f"One value: {spec.minimum:g} … {spec.maximum:g} ({unit})."
    return axes + " " + values


def catalogue():
    return [{"Id": c.id, "Name": c.name, "Category": category(c), "Help": help_text(c),
             "Reply": c.reply, "ReadOnly": c.read, "Moves": c.move, "IsStop": c.id in (0xFF, 0xA0FF),
             "Continuous": c.id in (0x12, 0x13, 0x14, 0x15, 0xA012, 0xA013, 0xA014, 0xA015),
             "Supported": c.payload != "Ambiguous", "MultipleAxes": c.payload.startswith(("Mask", "Four"))}
            for c in COMMANDS]


def describe(command, data, axes):
    kind = get(command).reply
    expected = {"None": 0, "Ack": 0, "Byte": 1, "UInt16": 2, "Float": 4, "Status": 8, "GroupPositions": 20, "GroupStatus": 20}[kind]
    if len(data) != expected:
        raise IOError(f"Invalid reply length: expected {expected}, received {len(data)}.")
    def coordinate(offset):
        value = struct.unpack_from("<f", data, offset)[0]
        if not math.isfinite(value):
            raise IOError("Non-finite controller position.")
        return f"{value:.9g}"
    if kind == "None": return "Sent; no ACK is defined. Execution is unconfirmed."
    if kind == "Ack": return "ACK received; accepted, not necessarily completed."
    if kind == "Byte": return f"Value: {data[0]} (0x{data[0]:02X})"
    if kind == "UInt16": return f"Value: {struct.unpack('<H', data)[0]}"
    if kind == "Float": return f"Position: {coordinate(0)} µm"
    if kind == "Status": return f"Limits={data[0]}, power={data[1]}, home={data[2]}, resolution={data[5]}, moving={data[6]}"
    if list(data[:4]) != list(axes):
        raise IOError("Group reply axis addresses do not match the request.")
    if kind == "GroupPositions":
        return "; ".join(f"axis {data[i]}: {coordinate(4 + i * 4)} µm" for i in range(4))
    return "; ".join(f"axis {data[i]}: limits={data[4+i*4]}, power={data[5+i*4]}, moving={data[6+i*4]}, resolution={data[7+i*4]}" for i in range(4))


def call(method, args, exchange):
    if method == "console_catalogue":
        return catalogue()
    command, axes, values = args["command"], args["axes"], args["values"]
    data = build(command, axes, values)
    if method == "console_build":
        return {"data": base64.b64encode(data).decode("ascii"), "frame": frame(command, data).hex().upper()}
    if method != "console_execute":
        raise ValueError("Unknown console method.")
    spec = get(command)
    if not spec.read and command not in (0xFF, 0xA0FF) and args.get("allow_writes") is not True:
        raise ValueError("Enable advanced commands to move axes or change settings.")
    reply = exchange(command, data)
    return {"description": describe(command, reply, axes), "data": base64.b64encode(reply).decode("ascii")}
