using System;
using UnityEngine;

namespace TwoBirds
{
    internal sealed class GripSlotRig : IDisposable
    {
        private const int TargetCount = (int)GripTarget.LeftElbowCharge + 1;
        private static readonly GripTarget[] SlotTargets =
        {
            GripTarget.HoldPose, GripTarget.ChargePose, GripTarget.RightElbowHold, GripTarget.RightElbowCharge,
            GripTarget.LeftElbowHold, GripTarget.LeftElbowCharge
        };

        internal struct Applied
        {
            internal bool Defined;
            internal Pose Stored;
            internal GripLayer Layer;
        }

        internal sealed class Slot
        {
            internal Transform Root, ItemAnchor, Pouch;
            internal HoldSlotMode Mode;
            internal readonly Transform[] Targets = new Transform[TargetCount];
            internal Transform ItemLeft, PouchLeft;
            internal readonly Applied[] Records = new Applied[TargetCount];
            internal (HoldSlot, ItemDefinition, AvatarId, bool, float) Key;
            internal bool Keyed;
        }

        internal Transform Frame { get; }
        internal readonly Slot Hand, Heavy;
        internal bool Locked;

        internal GripSlotRig(Transform host)
        {
            Frame = new GameObject("HeldItemSlots").transform;
            Frame.SetParent(host, false);
            Vector3 scale = host.lossyScale;
            Frame.localScale = new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
            Hand = Create("HandSlot"); Heavy = Create("HeavySlot");
        }

        private Slot Create(string name)
        {
            var slot = new Slot { Root = Child(Frame, name) };
            foreach (var target in SlotTargets) slot.Targets[(int)target] = Child(slot.Root, target.ToString());
            slot.ItemAnchor = Child(slot.Root, "ItemAnchor");
            slot.Targets[(int)GripTarget.RightHand] = Child(slot.ItemAnchor, nameof(GripTarget.RightHand));
            slot.ItemLeft = Child(slot.ItemAnchor, nameof(GripTarget.LeftHand));
            slot.Targets[(int)GripTarget.PouchDraw] = Child(slot.ItemAnchor, nameof(GripTarget.PouchDraw));
            slot.Pouch = Child(slot.ItemAnchor, "Pouch");
            slot.PouchLeft = Child(slot.Pouch, nameof(GripTarget.LeftHand));
            return slot;
        }

        private static Transform Child(Transform parent, string name)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            return child;
        }

        internal Slot For(HoldSlotMode mode) => mode == HoldSlotMode.Heavy ? Heavy : Hand;

        internal Transform Target(Slot slot, GripTarget target) => target == GripTarget.LeftHand
            ? slot.Mode == HoldSlotMode.Slingshot ? slot.PouchLeft : slot.ItemLeft
            : (int)target < TargetCount ? slot.Targets[(int)target] : null;

        internal void Apply(Slot slot, HoldSlot holdSlot, ItemDefinition item, AvatarId avatar, bool firstPerson, float scale, bool force = false)
        {
            var key = (holdSlot, item, avatar, firstPerson, scale);
            if (Locked && !force && slot.Keyed && slot.Key.Equals(key)) return;
            slot.Key = key; slot.Keyed = true;
            slot.Mode = item ? item.HoldMode : holdSlot ? holdSlot.Mode : HoldSlotMode.Hand;
            slot.ItemLeft.gameObject.SetActive(slot.Mode == HoldSlotMode.Heavy);
            slot.Pouch.gameObject.SetActive(slot.Mode == HoldSlotMode.Slingshot);
            for (int i = 0; i < TargetCount; i++)
            {
                var target = (GripTarget)i;
                bool used = GripPoses.Uses(slot.Mode, target);
                var transform = Target(slot, target);
                if (target != GripTarget.LeftHand) transform.gameObject.SetActive(used);
                if (!used) { slot.Records[i] = default; continue; }
                bool defined = GripPoses.TryResolve(holdSlot, item, avatar, target, firstPerson, out var pose, out var layer);
                if (GripPoses.InSlotFrame(target)) pose.position *= scale;
                transform.SetLocalPositionAndRotation(pose.position, pose.rotation);
                slot.Records[i] = new Applied { Defined = defined, Stored = new Pose(transform.localPosition, transform.localRotation), Layer = layer };
            }
        }

        internal void Place(Pose frame) => Frame.SetPositionAndRotation(frame.position, frame.rotation);

        internal void Anchor(Slot slot, float charge, float draw, Vector3 restPouch, float pitch, Vector3 pivot)
        {
            var hold = slot.Targets[(int)GripTarget.HoldPose];
            var charged = slot.Targets[(int)GripTarget.ChargePose];
            slot.ItemAnchor.SetLocalPositionAndRotation(Vector3.Lerp(hold.localPosition, charged.localPosition, charge),
                Quaternion.Slerp(hold.localRotation, charged.localRotation, charge));
            if (pitch != 0f && charge > 0f)
            {
                Quaternion rotation = Pitch(pitch * charge);
                slot.ItemAnchor.GetPositionAndRotation(out var position, out var current);
                slot.ItemAnchor.SetPositionAndRotation(pivot + rotation * (position - pivot), rotation * current);
            }
            if (slot.Mode != HoldSlotMode.Slingshot) return;
            var draw01 = Mathf.Clamp01(draw);
            var pouch = slot.Targets[(int)GripTarget.PouchDraw];
            slot.Pouch.SetLocalPositionAndRotation(Vector3.Lerp(restPouch, pouch.localPosition, draw01),
                Quaternion.Slerp(Quaternion.identity, pouch.localRotation, draw01));
        }

        internal bool Hint(Slot slot, bool right, float charge, float pitch, Vector3 pivot, out Vector3 world, out float weight)
        {
            var holdTarget = right ? GripTarget.RightElbowHold : GripTarget.LeftElbowHold;
            var chargeTarget = right ? GripTarget.RightElbowCharge : GripTarget.LeftElbowCharge;
            bool hold = Live(slot, holdTarget), charged = Live(slot, chargeTarget);
            world = default; weight = 0f;
            if (!hold && !charged) return false;
            Vector3 holdLocal = slot.Targets[(int)holdTarget].localPosition, chargeLocal = slot.Targets[(int)chargeTarget].localPosition;
            Vector3 local = hold && charged ? Vector3.Lerp(holdLocal, chargeLocal, charge) : hold ? holdLocal : chargeLocal;
            weight = hold && charged ? 1f : hold ? 1f - charge : charge;
            world = slot.Root.TransformPoint(local);
            if (pitch != 0f && charge > 0f) world = pivot + Pitch(pitch * charge) * (world - pivot);
            return true;
        }

        private Quaternion Pitch(float degrees) => Quaternion.AngleAxis(degrees, Frame.rotation * Vector3.right);

        private bool Live(Slot slot, GripTarget target) =>
            GripPoses.Uses(slot.Mode, target) && (slot.Records[(int)target].Defined || Moved(slot, target));

        private static bool Moved(Slot slot, GripTarget target) =>
            (slot.Targets[(int)target].localPosition - slot.Records[(int)target].Stored.position).sqrMagnitude >
            GripPoses.DirtyDistance * GripPoses.DirtyDistance;

#if UNITY_INCLUDE_INSTRUMENTATION
        internal void FollowAutomaticHint(Slot slot, GripTarget target, Vector3 world)
        {
            ref var record = ref slot.Records[(int)target];
            if (!GripPoses.Uses(slot.Mode, target) || record.Defined || Moved(slot, target)) return;
            var transform = slot.Targets[(int)target];
            transform.position = world;
            record.Stored = new Pose(transform.localPosition, transform.localRotation);
        }

        internal bool TryAuthored(Slot slot, GripTarget target, float scale, out Transform transform, out Pose stored, out GripLayer layer, out bool dirty)
        {
            transform = GripPoses.Uses(slot.Mode, target) ? Target(slot, target) : null;
            stored = default; layer = GripLayer.None; dirty = false;
            if (!transform) return false;
            var record = slot.Records[(int)target];
            var local = new Pose(transform.localPosition, transform.localRotation);
            dirty = GripPoses.IsHint(target)
                ? (local.position - record.Stored.position).sqrMagnitude > GripPoses.DirtyDistance * GripPoses.DirtyDistance
                : GripPoses.Differs(local, record.Stored);
            stored = local;
            if (GripPoses.InSlotFrame(target) && scale > 0f) stored.position /= scale;
            if (GripPoses.IsHint(target)) stored.rotation = Quaternion.identity;
            layer = record.Defined ? record.Layer : GripLayer.None;
            return true;
        }
#endif

        public void Dispose() { if (Frame) UnityEngine.Object.Destroy(Frame.gameObject); }
    }
}
