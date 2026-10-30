"""IPC transport. Application functions live in motion.py and jogging.py."""
import asyncio
import json
from pathlib import Path
import sys
import traceback

# Isolated bundled Python deliberately excludes the working directory and PYTHONPATH.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from motion import MotionController, MotionCancelled
from jogging import PROFILES


class Server:
    def __init__(self):
        self.engine = None
        self.pending = {}
        self.sequence = 0
        self.tasks = set()

    def emit(self, message):
        print(json.dumps(message, allow_nan=False, separators=(",", ":")), flush=True)

    def event(self, name, data):
        self.emit({"type": "event", "name": name, "data": data})

    async def host(self, op, **args):
        self.sequence += 1
        request_id = self.sequence
        future = asyncio.get_running_loop().create_future()
        self.pending[request_id] = future
        self.emit({"type": "io", "id": request_id, "op": op, "args": args})
        try:
            return await asyncio.wait_for(future, 30)
        finally:
            self.pending.pop(request_id, None)

    async def call(self, request):
        try:
            if request["method"] == "init":
                if self.engine is not None:
                    raise ValueError("Already initialized.")
                self.engine = MotionController(request["args"]["devices"], self.host, self.event)
                result = {"protocol": 2, "profiles": PROFILES}
            elif self.engine is None:
                raise ValueError("Initialize the motion controller first.")
            else:
                result = await self.engine.call(request["method"], request["args"])
            self.emit({"type": "result", "id": request["id"], "result": result})
        except Exception as error:
            traceback.print_exc(file=sys.stderr)
            self.emit({"type": "error", "id": request["id"], "error": str(error),
                       "cancelled": isinstance(error, MotionCancelled)})

    async def run(self):
        while line := await asyncio.to_thread(sys.stdin.readline):
            message = json.loads(line)
            if message.get("type") == "io_result":
                future = self.pending.get(message["id"])
                if future is not None and not future.done():
                    if "error" in message:
                        future.set_exception(IOError(message["error"]))
                    else:
                        future.set_result(message["result"])
            else:
                task = asyncio.create_task(self.call(message))
                self.tasks.add(task)
                task.add_done_callback(self.tasks.discard)
        for task in self.tasks:
            task.cancel()
        if self.engine is not None:
            self.engine.watch.cancel()
            for poll in self.engine.polls.values():
                poll.cancel()


if __name__ == "__main__":
    asyncio.run(Server().run())
