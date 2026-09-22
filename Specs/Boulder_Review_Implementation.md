# Boulder Review Implementation

## 1. Resting boulders have different effective mass across clients

Fixed. Items with `CollideWhileSleeping` use dynamic rigidbodies on each peer, including while resting. Cart contacts transfer motion reporting to the cart's reporting peer using a reliable, packed contact snapshot and the existing revisioned motion stream. The contacting client keeps its local motion while the handoff is pending; the server rejects stale handoffs and returns the current item record.

`ItemCartPhysics` retains local body history for cart replay and restores the live body afterward, replacing `OfflineRigidbody` pausing for these items. Observer motion updates correct the dynamic body with visual smoothing instead of moving an interpolated kinematic collider.

## 2. Heavy-item poses discard the passenger's pitch and roll

Fixed. Attached heavy-item frames retain the body's full rotation; standing frames retain yaw-only orientation. Bound hand rigs retain their actual left and right shoulder positions for reach constraints while using the shared heavy-item reference center.

## 3. Clearance processing removes the left-hand transition when switching items

Fixed. Both initial clearance and committed-pose correction preserve the outgoing left-palm pose and two-handed transition flag while its blend weight remains positive. The destination item and right-hand target still receive the clearance correction.

## Findings left unchanged

None. All three findings describe reachable defects and warrant fixes.
