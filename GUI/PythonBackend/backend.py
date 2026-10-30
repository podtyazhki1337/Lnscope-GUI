"""Positioner IPC entry point; device functions live in sm10.py."""
import base64
import json
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))
from coordinates import finite
from limits import blocks, target
from links import members
from sm10 import mask, Positioner
import commands

def emit(message):
    sys.stdout.write(json.dumps(message, allow_nan=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def serve():
    positioner = None
    vision = None
    from object_map import ObjectMap
    object_map = ObjectMap()
    for line in sys.stdin:
        request = json.loads(line)
        request_id = request["id"]

        def exchange(command, payload):
            emit({"id": request_id, "type": "io", "command": command,
                  "data": base64.b64encode(payload).decode("ascii")})
            reply_line = sys.stdin.readline()
            if not reply_line:
                raise EOFError("Host disconnected.")
            reply = json.loads(reply_line)
            if reply.get("id") != request_id or reply.get("type") != "io_result":
                raise IOError("Unexpected host response.")
            if "error" in reply:
                raise IOError(reply["error"])
            return base64.b64decode(reply["data"], validate=True)

        try:
            if request["method"].startswith("map_"):
                result = object_map.call(request["method"], request["args"])
            elif request["method"].startswith("vision_"):
                if vision is None:
                    from vision import VideoSession
                    vision = VideoSession()
                result = vision.call(request["method"], request["args"])
            elif request["method"].startswith("console_"):
                result = commands.call(request["method"], request["args"], exchange)
            elif request["method"] == "init":
                positioner = Positioner(request["args"], exchange)
                result = {"protocol": 1, "python": sys.version.split()[0]}
            else:
                if positioner is None:
                    raise ValueError("Backend is not initialized.")
                positioner.exchange = exchange
                result = positioner.call(request["method"], request["args"])
            emit({"id": request_id, "type": "result", "result": result})
        except Exception as error:
            emit({"id": request_id, "type": "error", "validation": isinstance(error, ValueError),
                  "error": str(error)})


if __name__ == "__main__":
    serve()
