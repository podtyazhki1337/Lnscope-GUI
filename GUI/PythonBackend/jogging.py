"""Edit L/M/H sensitivity and held-key aggregation here."""
from coordinates import finite

# multiplier scales held-key steps; speed selects SM-10 Slow (0) or Fast (1).
PROFILES = {
    "L": {"multiplier": 1.0, "speed": 0},
    "M": {"multiplier": 5.0, "speed": 1},
    "H": {"multiplier": 20.0, "speed": 1},
}


def requests(bindings, step, rate):
    """W+A drives two axes; W+S cancel each other. New steps are not queued."""
    step = finite(step)
    if step <= 0:
        raise ValueError("Jog step must be positive.")
    profile = PROFILES[rate]
    directions = {}
    for binding in bindings:
        key = binding["Device"], binding["Axis"]
        directions[key] = directions.get(key, 0) + finite(binding["Direction"])
    return [{"device": device, "axis": axis,
             "value": (1 if direction > 0 else -1) * step * profile["multiplier"],
             "absolute": False, "speed": profile["speed"]}
            for (device, axis), direction in directions.items() if direction != 0]
