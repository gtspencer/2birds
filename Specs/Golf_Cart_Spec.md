# Golf Cart Specification

## Goal and scope

Build one networked golf cart using the model in `Assets/_3rdParty/GolfCart/Prefabs/GolfCart_01-blue.prefab`.

- Provide four seats: driver, front passenger, and two rear passengers.
- Keep the existing two-player session limit, player spawning, and session display. Expanding player capacity is a separate task.
- Prioritize responsive driving, consistent cart state across clients, and stable passenger presentation.
- Build the gameplay implementation for this project. Imported temporary scripts (Assets/_3rdParty/GolfCart/_tempScripts) are disposable driving-feel references, not dependencies or the source of truth.

## Driving feel and controls

The cart should feel playful but controllable: bouncy suspension, predictable steering, and deliberate handbrake drifts. Ordinary corners and bumps should be manageable, while major crashes and rollovers can eject riders.

| Action | Keyboard and mouse | Controller |
| --- | --- | --- |
| Accelerate, brake, reverse, steer | WASD | Left stick |
| Free look | Mouse | Right stick |
| Handbrake | Hold Space | Hold south button |
| Enter or switch seats | Tap E while looking at an available seat | Tap north button while looking at an available seat |
| Exit | Hold E | Hold north button |

- Driving input is relative to the cart, independent of look direction.
- Opposite throttle brakes before reversing. Reverse steering follows the cart's direction of travel.
- Steering authority varies with speed. The cart should not rotate in place while parked.
- The handbrake reduces grip and brakes the rear wheels to initiate a slide. Releasing it restores grip progressively so the driver can recover.
- Distinguish tapping from holding the interaction button. Holding to exit must not first switch seats or activate another targeted action. Exiting does not require looking at a seat.
- Seated players cannot walk or jump. Space/south button operates the handbrake only for the driver.
- Opening a menu, losing focus, or leaving the driver's seat clears driving input so throttle and handbrake cannot remain latched.

## Seats and transitions

- Each seat has its own interaction target and placement anchor. Looking at an available seat shows `Enter seat` through the existing interaction tooltip.
- Players can enter, switch seats, and exit at any speed.
- Seated players can target other available seats. Occupied seats cannot be taken.
- The server resolves competing seat requests. A seat switch is atomic: either the destination is granted or the player remains in their original seat.
- Entering or switching to the driver's seat transfers driving control and applies the driver equipment restrictions.
- Exiting preserves the cart's velocity at the seat, including motion caused by rotation.
- Choose a clear exit position near the seat. Ignore collision with the cart until the exiting player clears it, preventing an immediate second impact.
- Each transition carries a revision so delayed movement or seat messages cannot return a player to an obsolete state.

## Camera and rider presentation

- Use a first-person camera while seated.
- The camera position follows the displayed seat anchor. Its heading follows the cart while preserving the player's free-look offset.
- Keep the camera horizon level; cart roll and pitch do not roll or pitch the view. Vertical suspension movement still moves the seat and camera.
- Rear-seat facing follows the rear-seat anchor orientation.
- The cart, rider graphics, interaction targets, and seated camera use the same displayed cart pose. Riders must not be smoothed independently behind the cart.
- Maintain a consistent world-space aim direction for the camera, interaction ray, and passenger item throws.

## Equipment while seated

- The driver cannot hold, equip, use, or throw items.
- Entering the driver's seat, including switching from a passenger seat, automatically unequips the held item and cancels any pending item use or charged throw.
- Unequipping keeps the item in its inventory slot and preserves its quantity. It must not drop, throw, or remove the item.
- Enforce driver restrictions through keyboard, controller, hotbar, and inventory equip/use paths. Other clients must also see the driver's hands empty.
- Passengers can equip, use, and throw items normally.
- Passenger throws inherit cart velocity at the rider's position, rather than reading velocity from the suspended walking Rigidbody.
- Leaving the driver's seat restores the ability to equip and use items.

## Pedestrian impacts, crashes, and ejection

- A moving cart can hit an on-foot player and send them flying.
- Reuse `WorldImpact` in `Assets/Game/Runtime/Player/PlayerMotor.cs`. The struck player's client reports the impact through `SubmitWorldImpact` for a responsive local reaction.
- Determine pedestrian impulse from closing speed and impact direction, with upward lift and a designer-adjustable collision multiplier.
- A sustained contact must not repeatedly launch the same player. Ordinary Rigidbody response must not apply a duplicate gameplay impulse.
- Major crashes and sustained rollovers eject riders. Ordinary bumps, moderate collisions, and routine jumps do not eject them.
- Define major crashes using collision severity, such as the cart's impact-induced velocity change. Expose the major-crash ejection threshold in the cart settings ScriptableObject.
- The cart's simulator reports each ejection event once. The server clears affected seats together and distributes the event.
- Ejected players resume normal movement with cart momentum plus an ejection impulse through the existing world-impact movement path.

## Recovery state and tooltips

- When the cart is overturned and settled, replace the seat tooltip with `Flip cart`.
- When the cart is immobilized by geometry, replace the seat tooltip with `Unstick cart`.
- Recovery state takes priority over entering or switching seats. `Flip cart` takes priority when both conditions apply.
- Detect a stuck cart from sustained attempted driving with negligible progress, with handbraking excluded. Parking, momentary collisions, and normal landings must not trigger recovery.
- The simulator reports recovery state so all clients show the same available action. Keep the state available after the driver exits, until the cart recovers or can move again.
- Entering a flipped or stuck recovery state ejects everyone once and returns cart simulation to the server. A stuck-cart recovery ejection is separate from the major-crash threshold.
- Players recover the empty cart by tapping interact on a seat target showing `Flip cart` or `Unstick cart`. Recovery cannot run while a seat is occupied.
- `Flip cart` rights the cart while preserving its heading, shifting it only as needed to find a clear, supported position.
- `Unstick cart` moves the cart to the nearest clear, supported position while preserving its heading and placing it upright.
- The server applies the recovery position and orientation, clears linear and angular velocity, and synchronizes the result. Only clear the recovery state after successfully placing the cart; if there is no suitable nearby position, keep recovery available.
- Automatic recovery is out of scope.

## Networking and simulation

### Cart authority

- Exactly one peer simulates the cart: the driver's client while driven, and the host/server when there is no driver.
- Run cart physics on the existing 60 Hz FishNet physics clock.
- Use one client-authoritative `NetworkTransform` for cart position and rotation. Other peers follow the reported motion rather than independently simulating the driven cart.
- Protect cart physics from extra simulation during player prediction replays, using FishNet's `OfflineRigidbody` support.
- Transfer position, rotation, linear velocity, and angular velocity explicitly when driving authority changes. Driver changes and disconnects must preserve motion.
- Retain the latest reported motion on the server so a disconnect can resume simulation without requiring a final message from the departed driver.
- Prevent the cart from despawning when its owner disconnects. Release that player's seat and return simulation to the server.

### Shared state and passenger motion

- The server coordinates seats, ownership changes, and ejection events. Trust client simulation and impact reports; anti-cheat enforcement is outside scope.
- Keep one authoritative seat occupancy representation, identifying riders by player object and seat index. Replicate changes reliably and provide current state to joining clients.
- While seated, suspend walking simulation, player movement replication/reconciliation, and independent player graphics smoothing. Derive the rider's position from the cart and seat locally.
- Add an explicit seated state; `PlayerMotor`'s current external-control mode alone is insufficient because it still runs physics and reconciliation.
- Reset movement history appropriately on entry and exit. Reject stale transition revisions so reconciliation cannot move a rider away from a seat or undo an exit.
- Use one smoothing path for each displayed cart. Keep riders and their cameras attached to that same result.
- Send motion while it changes and compact presentation data only as needed. Seat membership and body color are state changes, not per-frame rider position streams.

### Consistency target

All clients must follow the same accepted cart trajectory, seat occupancy, and recovery/ejection state. Remote motion has unavoidable transport and interpolation delay; the design must prevent independent rider drift and conflicting cart simulations rather than promise zero network delay.

## Components and integration

Keep responsibilities separate without introducing a general vehicle framework.

| Component | Responsibility |
| --- | --- |
| `GolfCartController` | Rigidbody driving, four-point raycast suspension, handbrake, collision severity, and rollover/stuck detection |
| `GolfCartNetwork` | Ownership, synchronized occupancy and recovery state, handoffs, and ejection events |
| `CartSeat` | Seat anchor, interaction target, tooltip, and interaction request |
| `PlayerSeating` | Entry/exit transitions, movement suspension, equipment restrictions, and rider attachment |
| `GolfCartPresentation` | Wheel and steering visuals, suspension presentation, and body color |
| `GolfCartSettings` | ScriptableObject containing designer-facing cart tuning, including pedestrian collision multiplier and major-crash ejection threshold |

- Extend `PlayerInputReader` to route walking, passenger, and driving input while preserving free look and menu behavior.
- Extend `PlayerPresentation` for seat-relative camera placement and heading.
- Reuse `IInteractable`, `PlayerInteraction`, and the UI Toolkit interaction tooltip. Adjust targeting to support interaction-only seat colliders while respecting solid obstructions.
- Integrate `PlayerEquipment` and `PlayerInventory` so driver unequip preserves inventory and passenger throws use cart velocity.
- Cache component, seat, camera, renderer, and material-slot references during initialization or state transitions.

## Prefab, tuning asset, and body color

- Create one gameplay cart prefab under `Assets/Game/Prefabs`, using the imported mesh and retained shared materials.
- Author its Rigidbody, chassis colliders, four suspension points, four seat targets/anchors, and required networking/presentation components.
- Add `PlayerSeating` to the player prefab and assign one `GolfCartSettings` asset to the cart prefab.
- Expose body color in the Inspector and through a setter. Apply color only to explicitly assigned paint material slots, using cached renderer references and a material property block.
- Replicate runtime body-color changes and provide the current color to joining clients. An in-game customization UI is a later task.
- Keep scene changes limited to necessary cart placement/setup. Do not add player spawn points or increase session capacity.
- Let Unity create metadata. Do not create one-off migration editor tools.

## Asset cleanup

After the gameplay implementation replaces them, delete imported temporary cart scripts and obsolete color-prefab/material variants. Migrate references before removing assets, and preserve the mesh and shared materials required by the single gameplay prefab.

## Visual acceptance checks

The user checks these in-game after implementation, using the existing host and guest sessions:

- Driver, passenger, and outside views show consistent cart motion and riders aligned to their seats during turns, bumps, and drifts.
- Free look remains independent of steering, turns with the cart, and keeps the horizon level.
- Entering and switching seats works while moving; occupied seats cannot be taken. Holding interact exits without first switching seats.
- Driver entry hides the held item on both clients, preserves it in inventory, and cancels a charged throw. Passengers can equip and throw with inherited cart momentum.
- Moving exits, driver changes, and disconnects preserve momentum without snapping riders back to old positions.
- Pedestrian impacts launch once per contact. Ordinary bumps keep riders seated; major crashes and rollovers eject them according to the settings asset.
- Recovery tooltips agree across clients, remain available after exit, and never appear during ordinary stops or handbraking.
- Flipped or stuck recovery ejects everyone once. Interacting then rights or frees the empty cart without overlapping geometry; the cart never recovers automatically.
- Body-color changes affect the intended panels and appear consistently to other clients, including clients joining later.
