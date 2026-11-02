# Limit switches, linked motion, and speed

## What is read from SM-10

Command `0x0120` returns the limit-switch state in its first byte: `0` means
clear, `1` means the negative limit, and `2` means the positive limit. Unknown
values are treated as an active limit with unknown direction. Sources are
SM10 Interface description V1.0, printed page 22, and the German original, page
18. The original has two reserved bytes before step resolution; motion status
is in byte 6.

The supplied serial protocol has no command for reading numeric minimum and
maximum travel coordinates. The application does not substitute the coordinates
of the last limit event or search for bounds through motion/homing. Numeric C1
limits in Hardware setup remain separate software limits. Reaching or exceeding
them also makes the row red, labelled SOFTWARE C1 LIMIT. Hardware switches are
labelled HARDWARE LIMIT −/+. A fresh clear status removes the red indication.

## Stopping

Before moving, the application reads coordinates and status for every member of
the linked operation. The leader does not start if a member has an active switch
in the requested direction, the target exceeds software bounds, a device is not
ready, or motion is disabled. Moving away from a known active switch is allowed
if the target satisfies software limits.

After sending a command, the operation remains active until arrival is confirmed.
Position updates are monitored after ACK as well. A switch in the direction of
travel, reaching a software limit, a communication error, stopping short of the
target, or no updates for more than 3 seconds cancels remaining commands and
stops all active operations, including independently held axes. Membership of
an active linked operation is retained even if its checkbox changes.

Emergency SM-10 Stop cancels the pending response wait and sends documented
group command `0xA0FF` without waiting for ACK. Motion is not retried. After an
emergency, the connection stays locked: disconnect and reconnect all groups on
the shared controller. This prevents a late response from being used for the
next command. Stop failures are logged; stopping the other members is still attempted.

Releasing a key or losing focus prevents new steps. A completed exchange cannot
start a deferred axis, and already issued moves receive Stop. The documented
160 ms pause is observed before subsequent normal commands. Normal key release
does not require reconnection when communication is healthy.

Limit detection depends on polling: by default 200 ms plus exchanges and the
other axes. This is a software response, not a synchronous hardware interlock;
communication loss prevents guaranteeing a physical stop. Real limit switches
had not been tested with this version: COM3 was absent from Windows on September
30, 2026.

## L / M / H

Tastenfeld-SM10_v05-eng, printed page 10, describes L/M/H as configurable knob
sensitivity and C as coarse mode. Position commands provide Fast/Slow. No
documented command was found for switching the physical L/M/H buttons.

The GUI selector therefore explicitly defines application modes:

| GUI | Step while a direction key is held | Motor command mode |
|---|---:|---|
| L | 1 × Keyboard step | Slow |
| M | 5 × Keyboard step | Fast |
| H | 20 × Keyboard step | Fast |

The ± buttons execute one step of the adjacent value; Go and ± use the selected
Slow/Fast mode. Stored controller speed settings and the physical keypad mode
are not overwritten. New steps do not accumulate while the previous target is
unconfirmed. Errors clear the held-key state; key auto-repeat does not restart
it. The mode is saved with the configuration.

All moves have bounded targets. Continuous commands 0012–0015 are not used for
held-key jogging: the documentation forbids requests other than Stop during
these commands, preventing limit polling.

## Verification

Automated checks cover: rejecting the leader when a follower has an active
limit or invalid target; stopping both sides on a limit after ACK; retaining
members after disabling a link; stopping concurrent independent axes; failure
after partial transmission; key release during reads/ACK; moving away from a
limit; missing/stale feedback; red indication and reset; and all three speed
modes. Tests use a simulated transport and do not move hardware.
