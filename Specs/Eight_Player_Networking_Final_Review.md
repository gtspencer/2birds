# Eight-player networking: final review

Fix the six numbered findings in the Codex review. Fold Claude's drop-placement concerns into the disconnect-drop fix. Then prioritize optional cosmetic rotation for suitable items and compact velocity messages while preserving physics, position accuracy, and update frequency. Defer optimizations with small savings or substantial gameplay tradeoffs.

Source reviews:

- [Claude review](Eight_Player_Networking_Review_Claude.md)
- [Codex review](Eight_Player_Networking_Review_Codex.md)
- [Performance review](Eight_Player_Networking_Review_Performance.md)

## Recommended fixes

### 1. Preserve reliable delivery when Steam rejects a send

**Priority: High. Source: Codex #1.**

Locations: [ServerSocket.SendToClient](Assets/Plugins/FishySteamworks/Core/ServerSocket.cs), [ClientSocket.SendToServer](Assets/Plugins/FishySteamworks/Core/ClientSocket.cs).

The transport logs queue-full send failures and continues, while FishNet resets the outgoing packet bundle. A rejected reliable message is therefore permanently lost even though the connection remains active. Inventory outcomes, seat transitions, scene messages, and item baselines depend on reliable delivery. Receiving baseline completion after a discarded baseline batch can enable gameplay with missing world state.

Preserve rejected reliable packets in order with bounded retries, or explicitly disconnect the affected peer when reliable delivery cannot be maintained. Disconnecting is the simpler correctness fix; bounded retry provides better continuity during transient congestion. Retried packets must own their payload because FishNet can reuse the original buffer. Later reliable messages must not overtake them.

Steam documents `k_EResultLimitExceeded` as a failed send caused by excessive queued data. Platform retransmission does not recover data that the API never accepted. [Steam networking API](https://partner.steamgames.com/doc/api/ISteamNetworkingSockets#SendMessageToConnection)

### 2. Correct the Steam packet resize and channel marker

**Priority: High. Source: Codex #2.**

Location: [CommonSocket.Send](Assets/Plugins/FishySteamworks/Core/CommonSocket.cs).

The resize branch writes into a new local array, then constructs the outgoing segment from the original array. With no spare byte, segment construction throws. With one spare byte, the transmitted channel marker can be stale or unwritten.

Construct the outgoing segment from the actual buffer, write the marker at `segment.Offset + segment.Count`, and resize only when there is insufficient capacity for that one additional byte.

### 3. Ignore invitations to the current or already-requested lobby

**Priority: High. Source: Codex #3.**

Location: [SessionController.JoinSteamLobby](Assets/Game/Runtime/Networking/SessionController.cs).

Every invitation currently queues a join and leaves any active session. Accepting an invitation to the current lobby can stop the host's server for everyone. Guests unnecessarily disconnect, drop their inventories, and return as fresh players. Duplicate invitations during a join can also cancel and restart the attempt.

Compare the requested lobby ID with the current lobby and the active join target before changing pendingInvite or starting teardown. Preserve the ability to join that lobby again after an intentional leave.

### 4. Place departing players' items in clear, reachable space

**Priority: Medium. Sources: Codex #4; Claude #11 and #16.**

Location: [WorldItemRegistry.DropDepartingItems](Assets/Game/Runtime/Items/WorldItemRegistry.cs).

The drop grid separates items from one another but does not check walls, ceilings, or world bounds. Items can appear inside geometry, across a wall, or outside the cleanup bounds. Because rejoining creates an empty inventory, inaccessible drops can become permanent item loss.

Use each item's cached bounds to find nearby clear positions. Check both destination clearance and the path from the departure pose, preserve separation between placed items, and provide a reachable fallback when the initial candidates are obstructed. Include world-bound handling in the same placement logic.

The vertical grid's appearance does not warrant a separate redesign. Fix accessibility and item preservation first.

### 5. Close native handles for joins that fail before Connected

**Priority: Medium. Source: Codex #5.**

Location: [ServerSocket.OnRemoteConnectionState](Assets/Plugins/FishySteamworks/Core/ServerSocket.cs).

The server only adds a connection to its dictionary after Connected. Terminal callbacks received before that point skip native handle cleanup because the dictionary lookup fails. Failed AcceptConnection calls also only produce a log message.

Close native handles for terminal callbacks even when no FishNet connection ID exists, and release handles when acceptance fails. Only remove FishNet entries and notify disconnection for connections that were actually registered. Preserve the listener guards against stale callbacks.

Valve requires explicit connection destruction after terminal callbacks to release local resources. [Steam connection callbacks](https://partner.steamgames.com/doc/api/ISteamNetworkingSockets#SteamNetConnectionStatusChangedCallback_t)

### 6. Adapt the existing development runner to the lobby flow

**Priority: Medium for the development workflow. Source: Codex #6.**

Location: [MvpValidation.Start](Assets/Game/Runtime/Diagnostics/MvpValidation.cs).

The runner starts a multiplayer host and waits for InGame, but multiplayer now stops in InLobby until StartGame is called. Its direct address-based Join call also requires local networking mode.

Give the automated host an explicit start condition and call StartGame through the shared session flow. Document the `-localNetworking` launch requirement for address-based Host/Join runs. Preserve manual Start for interactive hosting. Fix this before relying on the runner's multiplayer results.

## Lower-priority improvements

### Allow recovery from delayed Steam lobby callbacks

**Recommended before release. Source: Codex cancellation limitation.**

[SessionController.StopSession](Assets/Game/Runtime/Networking/SessionController.cs) can remain in Stopping indefinitely while Steam lobby operations are pending. This blocks another join, hosting, and even Solo after cancellation or a connection timeout.

Allow the menu to recover while retaining cleanup for stale results. Keep protection against an old callback leaving a lobby that a newer attempt has joined. Simply deleting the wait or adding a timeout that forgets pending operations is insufficient.

### Update overlay text only when its displayed values change

**Optional cleanup. Source: Performance #11.**

[SessionOverlay.Update](Assets/Game/Runtime/UI/SessionOverlay.cs) formats the session label every frame, including while the panel is hidden. Cache the displayed values or update from suitable change notifications. Ensure the first render and changes to mode, capacity, and player count all refresh the label.

This is a small allocation improvement, not a shipping blocker or a significant eight-player optimization.

## Findings that do not warrant the proposed fixes

| Finding | Assessment |
| --- | --- |
| Claude #1: clear incidentPending immediately when an incident is accepted | Accepted incidents intentionally suppress further reports during handoff. InstallBaseline clears the flag on successful completion; timeout/resume paths also clear it. The review overlooks the normal completion path. Immediate clearing can admit duplicate crash reports during the handoff. |
| Claude #2: lobby condition precedence and ownership changes | The proposed parentheses preserve the existing behavior. No concrete failure path establishes ownership reassignment on an unrelated guest departure. Keep the fixed-host policy. |
| Claude #3: missing item dictionary entry during departure drops | No reachable path is identified. Remove marks the record Removed before pooling it, while departure processing selects Held records. An extra lookup guard would mask a hypothetical invariant violation. |
| Claude #4: attempt and SessionId can diverge | They intentionally diverge. Leaving increments attempt to invalidate callbacks; SessionId identifies the client session. The two counters are not compared against each other. |
| Claude #5: frame cap changes network-processing frequency | FishNet's frame-rate setters ultimately update Application.targetFrameRate. SessionRoot uses tick-based networking at 60 Hz. These setters are not the independent networking-frequency control described by the review. |
| Claude #6: Solo capacity differs from the prefab default | Runtime capacity overrides are intentional. Solo sets one local slot. |
| Claude #7: duplicate Steam shutdown | SessionController guards platform shutdown, and SteamLifetime clears Ready/InputReady after shutdown. The described duplicate-call concern does not demonstrate a defect. |
| Claude #8: mutable LobbyMember struct | ReceiveReady correctly writes the changed value back into the dictionary. No additional abstraction is needed. |
| Claude #9: no runtime assertion of the transport revision | UPSTREAM.md already records the source revision and local adaptations. Commit the vendored sources and maintain that note; a runtime checksum does not improve gameplay correctness. |
| Claude #10: traffic counters grow indefinitely across sessions | The transport reuses connection IDs and resets its allocation sequence when starting. Accumulated diagnostic counters do not establish the claimed unbounded growth. |
| Claude #12: no fallback for a missing MainMenu scene | MainMenu is required build configuration. A speculative runtime fallback is unnecessary. |
| Claude #13 and Performance #2: full roster snapshots | Eight-entry snapshots on membership/readiness changes are small and simple. Keep them. |
| Claude #14: linear spawn-slot allocation | The work is trivial at eight players and only runs when spawning. |
| Claude #15: more specific seat rejection feedback | Occupied, Busy, and Blocked already distinguish the useful outcomes. More detail is not required by the networking change. |
| Claude #16: vertical departure grid looks odd | Address obstruction and reachability in the drop-placement fix. Cosmetic scatter alone does not justify extra work. |
| Claude minor note: FindObjectsByType requires a sort argument | Incorrect for Unity 6000.5. The parameterless generic overload exists, and the sort-mode overloads are deprecated. |
| Claude minor note: guard the development App ID copy with File.Exists | The source file is tracked and required configuration. Silently skipping it would hide a broken development build setup. |
| Claude minor notes: unused import and SessionController size | These do not justify a separate cleanup or refactor in this change. |

Supporting references:

- [GolfCartNetwork handoff and baseline logic](Assets/Game/Runtime/Vehicles/GolfCartNetwork.cs)
- [FishNet frame-rate application](Assets/FishNet/Runtime/Managing/NetworkManager.cs)
- [Transport source pin and adaptations](Assets/Plugins/FishySteamworks/UPSTREAM.md)
- [Steam lobby ownership](https://partner.steamgames.com/doc/api/ISteamMatchmaking#GetLobbyOwner): ownership transfers automatically when the owner leaves; explicit transfers are owner-controlled.
- [Unity 6000.5 FindObjectsByType](https://docs.unity3d.com/6000.5/Documentation/ScriptReference/Object.FindObjectsByType.html): supports the parameterless generic overload.

## Performance recommendations

The recommended optimization scope is item-motion encoding plus optional cosmetic rotation for suitable item types. Keep 60 Hz simulation, approximately 20 Hz item motion, position interpolation timing, owner-only inventory snapshots, and the existing division of physics responsibility. Exact airborne orientation may differ across clients for cosmetic-rotation items; trajectories, collisions, and responsiveness must retain their existing behavior. See [implementation step 7](Eight_Player_Networking_Plan.md#7-compact-item-motion-while-preserving-gameplay).

### Prioritize optional cosmetic rotation for suitable items

**Worth implementing after the transport correctness fixes, alongside compact linear velocity.**

Add a rotation-sync toggle to the existing ItemDefinition, defaulting to enabled. Suitable rocks can disable it; directional tools and items whose collider or gameplay hit volumes meaningfully depend on orientation should retain synchronization. Apply cosmetic tumble to the existing visual root on remote observers while preserving host physics and the thrower's locally predicted rotation.

For eligible items, omit both rotation and angular velocity from regular unreliable motion snapshots. FishNet already compresses rotation to four bytes, while angular velocity currently uses twelve bytes. Omitting both saves 16 bytes per sample, approximately **1.15 Mbps of host payload upload** at 64 eligible active items, 20 updates per second, and seven guests. Rotation alone would save only approximately 0.29 Mbps.

Keep initial and settled rotation in reliable lifecycle/baseline data, and blend the visual mesh into the settled orientation. Late joiners should initialize from that baseline. Stop cosmetic spin when sleeping or held, and retain revision ordering so stale motion cannot restart it. No continuous angular messages are needed just to produce cosmetic tumble.

The current correction code applies rotation and angular velocity to rigidbodies. Handle omitted fields explicitly: preserve local predicted angular state instead of applying zero/identity defaults. Cosmetic animation must not move gameplay hit volumes. Use an explicit format indicator so mixed item batches and motion arriving before lifecycle state can still be decoded and buffered.

### Prioritize compact velocity fields in item-motion snapshots

**Worth implementing after the transport correctness fixes. Source: Performance #1, with a narrower scope.**

Compact linear velocity in the unreliable ItemMotionBatch path for all items. Compact angular velocity only for items that retain rotation synchronization; cosmetic-rotation items omit it entirely. FishNet currently writes each Vector3 as three 32-bit floats: 12 bytes. Encoding each component as a 16-bit fixed-point value reduces that to six bytes for representable samples. Keep position at full precision and retain FishNet's existing compressed quaternion encoding wherever rotation is sent.

At 64 active items, 20 snapshots per second, and seven receiving guests:

| Encoding | Bytes saved per sample versus current encoding | Potential host payload upload saved |
| --- | --- | --- |
| Rotation synchronized; compact linear and angular velocity | 12 | Approximately 0.86 Mbps |
| Cosmetic rotation; omit rotation/angular velocity and compact linear velocity | 22 | Approximately 1.58 Mbps |

These are alternative all-item workloads, not additive savings. Relative to compacting both velocity vectors, cosmetic rotation saves another ten bytes per eligible sample, approximately 0.72 Mbps in the all-eligible workload. Actual savings depend on the item mix, encoding flags, and fallback samples; transport overhead is separate. The recurring savings justify this focused change without waiting for upload to become a bottleneck.

Use an explicit precision budget. A proposed starting point is a 0.01 m/s step for linear velocity and a 0.01 rad/s step for angular velocity, giving at most 0.005 error per component when rounding to the nearest representable value. Confirm the representable range against actual item behavior; collision impulses and inherited cart velocity must not be assumed to equal the configured throw speed. Preserve out-of-range values through a full-precision fallback rather than clamping physics or silently saturating the encoding.

Keep the compression specific to unreliable motion snapshots. ItemMotion also appears in release requests and reliable lifecycle records; changing its global serializer would broaden the behavioral impact. Preserve full-precision local simulation, launch data, and reliable lifecycle/baseline state. Each unreliable snapshot must remain independently decodable, so losing a packet does not prevent decoding later snapshots. Increment the session protocol identifier when introducing an incompatible message layout.

Before implementation, establish the existing serialized sample size and document the expected size of the compact representation, including its flags. An exhaustive hardware benchmark is not a prerequisite for this encoding change. The acceptance criteria are meaningful payload savings, convincing cosmetic tumble, and unchanged trajectories, impact response, and responsiveness across clients. Exact airborne rotation is intentionally relaxed only for opted-in item types.

### Keep position compression as a follow-up

Position quantization is valid, but leave it out of the initial optimization. Direct position error is more visible, and relative-coordinate schemes add message structure and range handling. Consider bounded fixed-point positions only if additional savings justify that work, with a small explicit position-error budget. Avoid absolute half-float world positions.

### Corrections to the performance review

The performance review overstates several conclusions:

- Source inspection cannot establish that the target hardware will sustain the frame-rate target, that CPU or cart physics cannot bottleneck, or that total host upload will be 6–8 Mbps. Treat those figures as estimates, not minimum specifications.
- The host replay explanation is incorrect. FishNet's PredictionManager discards incoming reconciliation state when the server is running; the host does not replay each remote player's client reconciliation history as described. See [PredictionManager.ParseStateUpdate](Assets/FishNet/Runtime/Managing/Prediction/PredictionManager.cs).
- The 64-byte ItemMotion calculation is nominal field-size arithmetic, not its measured wire size. FishNet already uses a compressed default quaternion writer. Proposed quaternion savings must account for that. See [Writer.WriteQuaternion32](Assets/FishNet/Runtime/Serializing/Writer.cs).
- Absolute half-float positions near 1,000 metres have approximately half-metre spacing. Do not adopt half-float world positions without a suitable precision budget or a relative-coordinate scheme.
- Steam handles retransmission for accepted reliable sends. It does not resolve the transport's discarded-send bug.
- ItemMotionBatch and ItemLifecycleBatch are structs containing reused lists. Creating those wrappers does not itself require object pooling.

Keep these changes outside the initial optimization scope:

| Proposal | Revisit when |
| --- | --- |
| Position quantization | Additional savings justify its precision and encoding tradeoffs after compact velocity snapshots. |
| Velocity-based adaptive item updates | Further bandwidth reduction is needed. Lower rates can change visible rolling and impact response; speed alone does not capture sudden direction changes. |
| Separate compact sleep messages | Lifecycle traffic is a meaningful cost. Sleep transitions are not continuous motion traffic. |
| Delta roster messages | Session size or roster update frequency grows substantially. |
| Distance-based item observer culling | Map layout and player separation make irrelevant item traffic significant. |
| Reducing simulation to 30 Hz | CPU measurements establish the need and the gameplay consequences are acceptable. This affects physics and responsiveness, not just rendering. |
| Replacing remote-player prediction | A demonstrated CPU constraint justifies reworking movement presentation and collisions. |
| New message-routing abstractions or component splits | Concrete feature growth makes the existing organization difficult to maintain. |

## User visual acceptance checks

After fixes:

- Start Solo, then host with seven guests. Confirm eight distinct spawn positions, correct player counts, working input, and successful late joins. Wait in the lobby beyond the connection timeout before starting.
- Accept invitations to the current lobby as host and guest. Confirm no teardown, inventory drop, or respawn. Repeat invitations while a join is in progress.
- Cancel joins repeatedly, leave, rehost, and switch to Solo. Confirm the menu remains usable and delayed callbacks do not disturb a newer session.
- Disconnect with a full inventory beside walls, beneath ceilings, near world boundaries, and from moving cart seats. Compare remaining clients and confirm every dropped item is reachable. Rejoining should create an empty inventory.
- Under Steam congestion, compare late-join world items, inventories, and cart seats. A reliable-send failure must recover in order or end the affected connection instead of leaving an active session with missing state.
- Race players for items and seats, change passengers during a crash, and trigger later crashes and rollovers. Confirm rejection feedback, inventory correction, ejection, and recovery continue to work.
- Select 60, 90, and 120 FPS caps. Confirm persistence, smooth camera/cart motion, and unchanged movement, jumping, and throwing behavior.
- After compact motion encoding, compare fast throws, slow rolls, spinning items, impacts, and throws from moving carts across host and guests. Include distant world positions, packet loss, and high-speed motion that uses the precision fallback. Look for stepping, changed trajectories, delayed impact response, or additional visible corrections.
- Compare cosmetic-rotation rocks with rotation-synchronized items during flight, bounces, rolling, and settling. Watch for sliding, clipping, rotation snaps, or changed hit behavior. Confirm thrower prediction remains responsive and late joiners see consistent resting orientations. Repeat with mixed item types and packet loss.
- On the target hardware, compare frame pacing with eight players and 64 active items, including dense collisions and a late join. Compare payload traffic before and after compact encoding under the same workload. Use measured frame time, replay cost, and traffic before choosing further performance changes or publishing minimum requirements.
