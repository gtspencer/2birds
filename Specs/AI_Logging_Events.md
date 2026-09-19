# AI logging events

All events below use envelope version `v=1` and payload version `event_v=1`. Read [the specification](AI_Logging_Spec.md) before interpreting missing records. Unknown fields and event names may be ignored; malformed complete JSON records must be reported.

## Envelope and identity

Every line, including segment headers, has `v`, `run`, `seq`, `t_us`, `frame`, `event`, `event_v`, `level`, `ctx`, and `data`. Sequence numbers are admitted under one lock and written in that order. Observation time can precede that of a lower sequence when concurrent producers take different amounts of time to serialize. `frame=null` means unavailable, including worker outcomes and background Unity messages.

`t_us` is monotonic microseconds from this run's start. UTC in `run.json` helps find a reproduction; it does not synchronize peers. Positions are world-space metres, velocities metres/second, angular velocities radians/second, quaternion arrays `[x,y,z,w]`, direction vectors dimensionless, and camera FOV degrees. Durations carry `_s` or `_us`. Non-finite numeric values are `null`, with their payload paths in `non_finite_fields`.

Context fields are optional:

| Field | Scope |
| --- | --- |
| `attempt` | This process's `SessionController.SessionId`; never a shared multiplayer ID. |
| `network_session` | Existing admission `wireSession`. Combine with run metadata and connections to identify a reproduction. |
| `connection` | Cached local connection, or explicitly observed remote connection in a server callback. |
| `subject`, `related_subject` | Namespaced entity IDs, such as `player:7` and `item:12`. |
| `generation`, `epoch` | Player impact generation, or item world epoch. Incoming impact events carry the incoming generation; `current_generation` is the receiver's current value. |
| `incarnation` | Run-local player lifetime counter. It cannot resolve cross-peer ambiguity when a network object ID and generation are reused. |
| `owner`, `is_owner` | Player's owner connection and ownership on this process. |
| `side` | Actual `server` or `client` execution path. A host has both. Presentation sampling is a client observation. |
| `local_tick`, `movement_tick`, `server_tick` | Separately named tick domains. Do not substitute one for another. |
| `operation` | `impact:<player>:<generation>:request:<request>` or `...:sequence:<sequence>`; `release:<epoch>:<releaser-player>:<operation>`; or `inventory:<epoch>:<player>:<operation>`. |

Player generations and impact requests are not globally unique. Item identity is `(network_session, epoch, item ID)` within the reproduction. Releases can contain multiple item IDs. Pair request and confirmation records to map owner request IDs to server sequences and application ticks. `replay=true` is another observation of simulation, not a new impact.

## Recorder, Unity, and markers

Sources: [AILogger.cs](../Assets/Game/Runtime/Diagnostics/AILogger.cs), [AILogWriter.cs](../Assets/Game/Runtime/Diagnostics/AILogWriter.cs), [Log.cs](../Assets/Game/Runtime/Diagnostics/Log.cs).

| Event | Payload |
| --- | --- |
| `logger.run.started` | `capture_available`. Run/build/protocol, output root, limits, sampling, initial transport configuration, and event families are in immutable `run.json`. |
| `logger.run.ended` | `reason`; emitted after queued records and image work drain. |
| `logger.segment.started` | `index`, `first_retained_seq`, `first_retained_t_us` for this segment, `cumulative_losses`, `segments_evicted`, `images_evicted`. Segment numbers never repeat. |
| `logger.segment.ended` | `index` of the segment about to rotate. The final segment closes with `logger.run.ended`. |
| `logger.dropped` | `count`, `event_class`, `reason`, `from_t_us`, `to_t_us`, `cumulative`. Classes are bounded recorder families; pressure drops are distinct from deliberate sample cadence. |
| `logger.truncated` | `source_event`, `original_bytes`, `reason=record_limit`; replaces an oversized payload with complete JSON. |
| `logger.serialization_failed` | `source_event`, exception type in `reason`. Live Unity objects and raw Unity vectors/quaternions are rejected. |
| `logger.artifact.evicted` | Run-relative screenshot `path`, `bytes`. Artifact retention is independent of event retention. |
| `unity.log` | Original `message`, `stack`, `log_type`, `message_original_length`, `stack_original_length`, `truncated`. Message and stack each retain up to 4,000 UTF-16 code units. All Unity log types enter once through the threaded callback. |
| `marker` | `number`, matching the existing `MARKER (n)` console echo. Frame, time, and session are in the envelope. This never requests an image. |

A file-writer failure disables that run and reports `AI logging disabled: ...` once through Unity's console/player log. The failed sink cannot promise a structured failure record. A PNG write failure can emit `screenshot.failed` while the event sink remains writable. Disk/permission failures then disable further recording. The recorder exists only in the Editor and development builds.

`summary.json` contains `final_seq`, retained segment filenames, sequence/time endpoints and observed `min_t_us`/`max_t_us`, cumulative written `counts`, `losses`, segment/image eviction totals, and observed `screenshots` outcomes (including outcomes whose record was dropped). Retained segment endpoints are observations, not a synchronization guarantee. Missing summary means closure is unknown. A late worker may finish after the two-second shutdown wait, but only in its own run folder.

## Session

Source: [SessionController.cs](../Assets/Game/Runtime/Networking/SessionController.cs); active-scene notifications also originate in `AILogger.cs`.

| Event | Payload |
| --- | --- |
| `session.attempt` | `mode`, selected `transport`; context has the new local attempt. |
| `session.transport` | `kind`, `mode`, local `address`/`port` or joined `steam_host`. Local server startup records the actual bound port after an ephemeral-port request. |
| `session.admission` | `stage`: `assigned`, `accepted`, `sent`, `received`, or `rejected`. Depending on stage: `client_attempt`, `is_host`, `starting`, `reason`, `received_attempt`, `received_session`. Rejections preserve current context and identify the incoming values separately. |
| `session.authenticated` | `accepted`; context identifies the remote connection and server side. |
| `session.connection` | `state`, applicable `transport`, `remote` for remote disconnections; explicit executing side. |
| `session.phase` | `before`, `after`, `status`; emitted only when phase or status changes. |
| `session.scene` | `before`, `after` active scene names. |
| `session.scene_queue` | `loading` at network scene queue start/end. |
| `session.player_bound` | Local player `subject`. |
| `session.ready` | `scope` (`items`, `birds`, `player`), item/bird `epoch`, or player `stage` (`sent`, `received`). |
| `session.ui` | `panel_open`, `console_open`. |
| `session.leave` | `reason`, with old session identity. |
| `session.identity_cleared` | Empty payload; old identity is still in the envelope. Subsequent menu observations omit session identity. |
| `session.shutdown` | `reason` (`platform_shutdown` or `recorder_lifecycle_shutdown`); these describe separate shutdown boundaries. |

## Player simulation

Source: [PlayerMotor.cs](../Assets/Game/Runtime/Player/PlayerMotor.cs).

The shared **impact payload** contains `generation`, `current_generation`, `request_id`, `sequence`, `owner_tick`, `application_server_tick`, `velocity_change`, `recovery_ticks`, current `velocity`, `pending`, `reason`, and `replay`. Zero request/sequence values mean that the existing gameplay system has not assigned that identity, or the impact is server-originated. Context adds player, generation, local incarnation, ownership, side, and the current tick domains.

| Event | Payload / decision |
| --- | --- |
| `player.spawn`, `player.despawn` | `generation`; lifetime boundaries plus the context's local incarnation. |
| `player.generation` | `before`, `after`, `owner`; records the generation transition before the old state is cleared. |
| `player.mode` | `before`, `after`, `replay`; only transitions, including changes during prediction replay. |
| `player.reset` | `reason=fall_boundary`: `position_before`, `position_after`, new `reset_revision`; or `reason=seating`: `seated`, `revision`, `generation`, before/after position and velocity. |
| `player.impact.queued` | Server-originated `velocity_change`, `recovery_s`; context supplies server tick. |
| `player.impact.requested`, `player.impact.sent`, `player.impact.received` | Impact payload. Creation, successful RPC submission, and actual server entry are separate observations. Host requests enter the server handler directly. |
| `player.impact.accepted` | Impact payload with scheduled owner/server ticks and `reason=scheduled`. |
| `player.impact.rejected` | Impact payload for a received request or invalid sum; early submission rejection instead has `source` (`world`/`owner`), `velocity_change`, and `reason`. Reasons include seated, wrong side/owner, generation not ready, replay, invalid velocity/sum, stale generation, and duplicate/stale request. |
| `player.impact.confirmation_sent`, `player.impact.confirmation_received` | Impact payload at observer RPC send and receive, including confirmed sequence and actual application ticks. |
| `player.impact.confirmation_rejected` | Impact payload; `seated`, `server_already_applied`, or `stale_generation`. |
| `player.impact.confirmed` | Impact payload; `added` or `matched_pending_or_duplicate`. Matching an existing entry does not apply another impulse. |
| `player.impact.application` | Impact payload; `pending` or `already_incorporated` at the application decision. Deferred tick scans are not individually recorded. |
| `player.impact.pending` | `request_id`, `sequence`, `pending_before`, `pending_after`, `replay`; the exact accumulated velocity change. |
| `player.impact.force_submitted` | `last_request_id`, `last_sequence`, `velocity_before`, `pending_velocity_change`, `replay`, `aggregate=true`; forces have been submitted to Unity, before the physics result. |
| `player.impact.applied` | Same aggregate fields plus actual `velocity_after`, `position_after`, and `phase` (`post_tick_physics` or `replay_physics`). This result includes gravity, collisions, and other forces in that physics step. Pair the individual pending events at the same movement tick to identify all contributors; the watermark is not a single-impact claim. |
| `player.impact.cancelled` | `reason=fall_reset/impact_state_cleared`, `pending_velocity_change`; pending impact state was cleared before its physics result. |
| `player.reconcile` | `stage` (`decision`/`applied`), `accepted`, `reason`, `replay`, `state_tick`, `state_server_tick`, `state_generation`, `state_sequence`, `state_request`, observed `body_position`, `body_velocity`, incoming `state_position`, observed `pending`, incoming `state_pending`. Rejections have only a decision record. Emitted for outstanding/recovering impacts, generation transitions, and rejected reconciles. |
| `player.presentation.corrected` | `graphics_before`, `graphics_after`, `client_tick`, `server_tick`, `rejected`, around relevant reconcile/replay work. |

## Items and operations

Sources: [WorldItemRegistry.cs](../Assets/Game/Runtime/Items/WorldItemRegistry.cs), [WorldItemRegistry.Motion.cs](../Assets/Game/Runtime/Items/WorldItemRegistry.Motion.cs), [WorldItem.cs](../Assets/Game/Runtime/Items/WorldItem.cs), [PlayerInventory.cs](../Assets/Game/Runtime/Inventory/PlayerInventory.cs), [PlayerItemHitbox.cs](../Assets/Game/Runtime/Player/PlayerItemHitbox.cs).

The shared **item payload** contains `id`, `revision`, `sequence`, `path`, `motion_tick`, `launch_tick`, `state`, `holder`, `releaser`, `operation`, `simulator`, `sleeping`, `equipped`, `position`, `rotation`, `velocity`, `angular_velocity`, `boundary`, `removed`, `rotation_omitted`, and `reason`. Context supplies world epoch, subject, release identity, side, and current tick domains. `simulator=-1` means host simulation. `releaser` and `holder` are player object IDs, not connection IDs.

Motion received before lifecycle knowledge has `lifecycle_known=false` and only the motion fields; it does not invent a holder, simulator, or release identity. When `rotation_omitted=true`, transmitted rotation/angular velocity are unavailable until the receiver restores them from its record; interpret those events using that flag.

| Event | Payload / source |
| --- | --- |
| `item.initialized` | Item payload; `predicted` or `confirmed`. A pooled view may initialize again for the same item; use epoch/ID, not the Unity object, for identity. `WorldItem`. |
| `item.transition` | Item payload at state assignment; `reason=from_<previous-state>`. Includes revision, simulator, sleep, equipment, and release changes. `WorldItem`. |
| `item.pickup.predicted` | Item payload before hiding the owner's optimistic pickup. `WorldItem`. |
| `item.despawn` | Item payload, `reason=pooled`. World teardown is also bounded by `item.world.ended`. `WorldItem`. |
| `item.release.predicted`, `item.release.accepted` | Item payload at local launch/server commitment. `WorldItemRegistry`. |
| `item.release.rollback` | `id`, `operation`; current world epoch in context. `WorldItemRegistry`. |
| `item.lifecycle.sent`, `item.lifecycle.received`, `item.lifecycle.accepted` | Item payload; broadcast/baseline send, received record, then `applied` or `removed` result. `WorldItemRegistry`. |
| `item.lifecycle.rejected`, `item.lifecycle.deferred` | Item payload; `stale_or_duplicate` or `pending_release_mismatch`. A deferred view update can coexist with an updated stored lifecycle record. `WorldItemRegistry`. |
| `item.motion.sent`, `item.motion.received`, `item.motion.accepted` | Item payload; send channel, incoming simulator motion, and stored/pending-release/removal result. Client receipt proceeds directly to accepted/deferred/rejected. `WorldItemRegistry` and `.Motion`. |
| `item.motion.rejected`, `item.motion.deferred` | Item payload or motion-only payload; reasons include awaiting lifecycle, stale early motion, not world, stale/duplicate, local simulator, unknown item, wrong simulator, and revision mismatch. `WorldItemRegistry` and `.Motion`. |
| `item.batch.rejected` | `kind`, `received_epoch`, `reason` (host echo, world not ready, epoch). Entire batch rejected before examining individual records. `WorldItemRegistry` and `.Motion`. |
| `item.simulator.changed` | Item payload, `simulator_disconnected`; records host takeover before its new motion revision is published. `.Motion`. |
| `item.presentation.motion` | Item payload; `local_simulator`, `settled`, `optimistic_pickup`, `buffered`, `future_history_tick`, `within_correction_threshold`, or `prediction_corrected`. These are the view's actual decisions after registry receipt. `WorldItem`. |
| `item.body.corrected` | `smooth`, `body_before`, `body_after`, `graphics_before`, `velocity_before`, `incoming_velocity`, `revision`, `sequence`, `path`, `motion_tick`, `kinematic`. Incoming velocity is not assigned to a kinematic body. `WorldItem`. |
| `item.world.ended` | `count` of records before teardown; old epoch in context. `WorldItemRegistry`. |
| `item.impact.detected` | `player`, `player_generation`, `speed`, `minimum_speed`, `rock_velocity`, `player_velocity`, `normal`, `accepted`, `reason`, `revision`, `sequence`, `path`; item/release in context. `WorldItem`. |
| `item.impact.rejected` | `source` item ID, `releaser`, `release`, `detector`, `reason` (suspended, not owner, replay). Player/related item/epoch/release in context. `PlayerItemHitbox`. |
| `item.impact.reported` | Same identities plus `batch_id`, rock/player velocities, `normal`, `velocity_change`, `accepted`, `reason` (batched/non-finite). `PlayerItemHitbox`. |
| `item.impact.batch` | `batch_id`, `count`, uncapped `raw` velocity-change sum, `capped`, resulting `velocity_change`, `request_id`. Batch IDs are local to a player hitbox lifetime; context links the owner request and generation. `PlayerItemHitbox`. |
| `item.impact.batch_discarded` | `batch_id`, discarded batch `generation`, `count`, `reason` (suspension changed, suspended, disabled, generation changed). Context identifies the player's current lifetime. `PlayerItemHitbox`. |
| `item.operation.requested`, `item.operation.sent`, `item.operation.received`, `item.operation.accepted`, `item.operation.rejected` | `operation`, `kind`, `intent`, `seating_revision`, `from_slot`, `to_slot`, `ids`, `releases` (each ID, position, rotation, velocity, angular velocity), `reason`. Rejections name duplicate/stale, seating revision, equipment restriction, invalid slots, pickup state, full inventory, release shape, stack mismatch, or invalid item/motion. `PlayerInventory`. |
| `item.operation.reply`, `item.operation.confirmed` | `accepted`, `operation`, `revision`, `acknowledged`; confirmation also has `outcome=stale_revision/applied`. The operation counter belongs to the player inventory lifetime; join with its request records. `PlayerInventory`. |

Motion observations are continuous events; state snapshots do not replace them. Under queue pressure, motion events may be dropped, with losses reported by family. Accepted transmission never proves receipt or presentation.

## Sampling and input

Sources: [AILogCapture.cs](../Assets/Game/Runtime/Diagnostics/AILogCapture.cs), `PlayerMotor.cs`, `WorldItem.cs`, [PlayerInputReader.cs](../Assets/Game/Runtime/Player/PlayerInputReader.cs), [PlayerInteraction.cs](../Assets/Game/Runtime/Player/PlayerInteraction.cs). [PlayerPresentation.cs](../Assets/Game/Runtime/Player/PlayerPresentation.cs) supplies camera lifecycle notifications.

| Event | Payload |
| --- | --- |
| `capture.availability` | `available`, `reason` (`available`/`batch_or_headless`). |
| `capture.started`, `capture.ended` | `scope=local_player_camera_focused_items`, `reason` (`session_phase`, `player_stopped`, or `shutdown`). The coroutine runs only during gameplay. |
| `capture.burst.started`, `capture.burst.extended` | Trigger `reason` and `duration_s=5`. Markers, screenshot requests, relevant impacts, and movement anomaly paths share one deadline. |
| `capture.burst.ended` | Empty payload. |
| `state.sample` | `phase`, optional `capture_id`/`capture_t_us`, `cadence`, `scope`, `player`, `camera`, `items`, `target_subject`, `held_subject`, `gameplay_input`, `input_suppressed`, `inventory_open`, `console_open`, `session_panel_open`. |
| `input.gameplay` | `enabled`, at gameplay input enablement changes. |
| `input.inventory` | `open`, at inventory panel changes. |
| `input.target` | `item` subject when targeting an item, and interactable `kind`; unavailable values are null. |
| `input.edges` | `jump`, `drop`, `use_pressed`, `use_released`, `interact`, `exit`, plus their `*_blocked` flags. Observed meaningful edges after global input suppression, before action-specific gates; an edge is not proof of an executed action. |

Player snapshots contain subject, generation, incarnation, owner/ownership, tick domains, body position/rotation, graphics position/rotation, velocity, pending change, mode, grounded/seated state, reset revision, and impact sequence/request watermarks. Item snapshots contain subject/epoch, motion revision/sequence/path/tick, local/server ticks, releaser/operation/launch tick, simulator/simulating/predicted state, holder/state/sleep/equipment, body and graphics poses, `body_velocity`, `motion_velocity`, angular velocity, and kinematic state. Camera snapshots contain world pose and FOV.

The baseline is at most 2 Hz; a burst replaces it with at most 10 Hz for five seconds. Current target and equipped item take priority, followed by recently focused items, with at most seven items plus the local player. Screenshot-associated samples are explicit manual observations outside the periodic cadence and use reserved record capacity. No generic scene traversal or remote-player polling occurs.

`phase=trigger` is the coroutine's initial inline observation. `coroutine_after_update` is a resumed realtime wait; body state is the most recent simulation, and graphics/camera state is what is available then. Only `end_of_frame` with `capture_id` claims the screenshot's rendered frame. Tick domains in each snapshot remain separate.

## Screenshots

Sources: `AILogger.cs`, `AILogCapture.cs`, `AILogWriter.cs`, [LogMarkerService.cs](../Assets/Game/Runtime/Diagnostics/LogMarkerService.cs).

| Event | Payload |
| --- | --- |
| `screenshot.requested` | Run-scoped `capture_id`, `request_frame`, `request_t_us`. Emitted before availability/throttle decisions. |
| `screenshot.saved` | `capture_id`, `capture_frame`, `capture_t_us`, `width`, `height`, `file_name`, run-relative `path`, originating-machine `absolute_path`, `bytes`. Outer time is write completion; outer frame is null on the worker. |
| `screenshot.skipped` | `capture_id`, `reason`: session host unavailable, batch/headless, pending image, throttled, PNG size limit, writer unavailable, or shutdown before render. |
| `screenshot.failed` | `capture_id`, `reason`: no texture, encoding failure, or exception type. |

Follow the relative path after copying a run folder. A saved artifact can later be evicted. One pending image and a one-second accepted-request interval are independent of markers. An inactive Editor Game view can postpone capture until rendering resumes; actual capture time must not be inferred from request time.

## Reading a reproduction

1. Read `run.json`, `summary.json` if present, and segment headers. Establish coverage, losses, retained range, and peer identity before drawing conclusions.
2. Locate the user's marker or saved screenshot. Open the linked PNG, then stream a bounded time window around its capture time. Also follow related subject/operation identities outside the window.
3. Compare request, receipt, acceptance, pending force, post-physics result, reconcile, and displayed state. Pair supplied host/client folders using existing network identities, not wall-clock alignment.
4. Cite `run`, segment filename, and `seq`; cite images by relative path. Report evicted artifacts, incomplete trailing lines, and unknown closure. Separate observed behavior from inferred cause.

PowerShell narrowing example (replace the run directory):

```powershell
$runDir = 'C:\path\to\run'
Get-Content -LiteralPath (Join-Path $runDir 'run.json')
rg -n '"event":"(marker|screenshot.saved|logger.dropped)"' $runDir -g 'events-*.jsonl'
```

Streaming Python example; it never loads an entire run. Change `start_us`/`end_us` and optionally `operation` after finding a marker. It reports an incomplete trailing line and surfaces malformed complete records.

```python
import json
from pathlib import Path

run_dir = Path(r'C:\path\to\run')
start_us, end_us = 10_000_000, 20_000_000
operation = None  # e.g. 'impact:7:3:request:12'
for segment in sorted(run_dir.glob('events-*.jsonl')):
    with segment.open('rb') as stream:
        for number, line in enumerate(stream, 1):
            if not line.endswith(b'\n'):
                print(f'INCOMPLETE: {segment.name}:{number}')
                break
            try:
                record = json.loads(line)
            except (ValueError, UnicodeError) as error:
                raise ValueError(f'{segment.name}:{number}: {error}') from error
            in_window = start_us <= record['t_us'] <= end_us
            related = operation is not None and record['ctx'].get('operation') == operation
            if in_window or related or record['event'] == 'logger.segment.started':
                print(record['run'], segment.name, record['seq'], json.dumps(record))
```

Add coverage by adding a typed source event and documenting it here. Add a focused snapshot only when that event cannot explain the presentation. No custom reader, replay engine, upload service, or live command endpoint is required.
