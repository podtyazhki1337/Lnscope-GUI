"""Numeric validation shared by editable motion modules."""
import math

def finite(value):
    value = float(value)
    if not math.isfinite(value):
        raise ValueError("A finite coordinate is required.")
    return value


