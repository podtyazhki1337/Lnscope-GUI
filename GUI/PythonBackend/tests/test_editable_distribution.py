"""Proves that editing deployed Python changes the live IPC controller without C# compilation."""
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


class EditableDistribution(unittest.TestCase):
    def test_editing_python_changes_live_controller(self):
        root = Path(__file__).resolve().parents[1]
        with tempfile.TemporaryDirectory(prefix="lnscope-python-") as temporary:
            folder = Path(temporary)
            for source in root.glob("*.py"):
                shutil.copyfile(source, folder / source.name)
            policy = folder / "jogging.py"
            policy.write_text(policy.read_text(encoding="utf-8").replace('"multiplier": 20.0', '"multiplier": 3.0'), encoding="utf-8")
            script = """
import asyncio
import json
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).parent))
from motion_server import Server

async def check():
    server = Server()
    async def host(op, **args):
        if op == 'read':
            return {'AxisId':'X', 'Reference':0, 'User':0, 'IsMoving':False, 'StopSwitchActive':False, 'Limit':0}
        if op == 'move':
            print(json.dumps({'checked_target':args['value']}), flush=True)
    server.host = host
    await server.call({'id':1, 'method':'init', 'args':{'devices':[
        {'Id':'stage', 'State':2, 'Axes':[{'Id':'X', 'MinPosition':-100, 'MaxPosition':100}]}]}})
    await server.call({'id':2, 'method':'jog', 'args':{'bindings':[
        {'Device':'stage', 'Axis':'X', 'Direction':1}], 'step':5, 'rate':'H', 'links':[]}})
    await server.engine.close()
asyncio.run(check())
"""
            (folder / "probe.py").write_text(script, encoding="utf-8")
            result = subprocess.run([sys.executable, "-I", "-X", "utf8", str(folder / "probe.py")],
                                    capture_output=True, text=True, encoding="utf-8", timeout=10, check=True)
            messages = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertIn({"checked_target": 15.0}, messages)
            self.assertFalse(any(m.get("type") == "error" for m in messages))
