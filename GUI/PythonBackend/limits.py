"""Software travel bounds and directional hardware end switches."""
from coordinates import finite

def blocks(position, direction):
    limit = position["Limit"]
    return position["StopSwitchActive"] and (
        limit in (0, 255) or limit == 1 and direction < 0 or limit == 2 and direction > 0)


def target(current, info, delta, factor):
    if current["IsMoving"]:
        raise ValueError("Axis is already moving; wait for standstill.")
    value = finite(current["Reference"] + finite(delta) * finite(factor))
    if (info["MinPosition"] is not None and value < info["MinPosition"] or
            info["MaxPosition"] is not None and value > info["MaxPosition"]):
        raise ValueError("Target is outside software C1 limits. No linked movement started.")
    direction = (value > current["Reference"]) - (value < current["Reference"])
    if blocks(current, direction):
        raise ValueError("Hardware limit: move away from the limit.")
    return {"Target": value, "Direction": direction}


