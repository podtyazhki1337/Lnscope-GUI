"""Linked-axis mapping, reciprocal factors and conflict checks."""
from coordinates import finite

def members(device, axis, links):
    result = [{"Device": device, "Axis": axis, "Factor": 1.0}]
    for link in links:
        if not link["Enabled"]:
            continue
        options = link["Options"]
        for mapping in options["AxisMap"]:
            factor = finite(mapping["Factor"])
            if options["Source"] == device and mapping["From"] == axis:
                result.append({"Device": options["Target"], "Axis": mapping["To"], "Factor": factor})
            elif options["Bidirectional"] and options["Target"] == device and mapping["To"] == axis:
                if factor == 0:
                    raise ValueError("A bidirectional link factor cannot be zero.")
                result.append({"Device": options["Source"], "Axis": mapping["From"], "Factor": 1 / factor})
    if len({(m["Device"].casefold(), m["Axis"].casefold()) for m in result}) != len(result):
        raise ValueError("Conflicting linked axes.")
    return result


