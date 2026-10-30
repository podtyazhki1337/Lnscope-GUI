"""Command tests use no COM ports and do not move hardware."""
import struct
import unittest
import commands


class CommandChecks(unittest.TestCase):
    def test_known_wire_vectors(self):
        self.assertEqual(commands.build(0x0132, [7], []), bytes([7, 2]))
        self.assertEqual(commands.build(0x0147, [1], [-2]), bytes([1, 254]))
        self.assertEqual(commands.build(0x003D, [1], [258]), bytes([1, 2, 1]))
        self.assertEqual(commands.build(0xA0FF, [1, 8, 9, 72], []), bytes.fromhex('A0800000000000000181'))
        self.assertEqual(len(commands.build(0xA048, [1, 2, 3, 4], [1, -2, 3, 4])), 21)

    def test_bad_parameters_do_not_exchange(self):
        cases = [(0x1E8, [1], []), (0x48, [0], [1]), (0x48, [1], [float('nan')]),
                 (0x48, [1], [1e50]), (0xA0FF, [1, 1], []), (0xA048, [1, 2, 3, 19], [1]*4),
                 (0x134, [1], [16]), (0x147, [1], [-128]), (0xA140, [1], [1, -2]),
                 (0xA110, [1], [6, 1]), (0xA110, [1], [1, 16]), (0x101, [True], [])]
        calls = []
        for command, axes, values in cases:
            with self.assertRaises(ValueError):
                commands.call('console_execute', dict(command=command, axes=axes, values=values, allow_writes=True), lambda *a: calls.append(a))
        self.assertEqual(calls, [])

    def test_readonly_gate_and_preview(self):
        calls = []
        request = dict(command=0x48, axes=[1], values=[10], allow_writes=False)
        commands.call('console_build', request, lambda *a: calls.append(a))
        with self.assertRaises(ValueError):
            commands.call('console_execute', request, lambda *a: calls.append(a))
        self.assertEqual(calls, [])

    def test_reply_validation(self):
        self.assertIn('moving=1', commands.describe(0x120, bytes([0, 1, 0, 0, 123, 2, 1, 0]), [1]))
        self.assertIn('no ACK', commands.describe(0xA0FF, b'', [1]))
        for command, data, axes in [(0x101, b'', [1]), (0x101, struct.pack('<f', float('nan')), [1]),
                                     (0xA101, bytes([4, 3, 2, 1])+bytes(16), [1, 2, 3, 4])]:
            with self.assertRaises(IOError):
                commands.describe(command, data, axes)
