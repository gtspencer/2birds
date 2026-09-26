using System;
using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    public enum GripTarget : byte
    {
        HoldPose, ChargePose, RightHand, LeftHand, PouchDraw, RightElbowHold, RightElbowCharge, LeftElbowHold, LeftElbowCharge,
        ContactPalm, ContactElbow
    }
    public enum GripLayer : byte { None, Slot, Item, Avatar, Contact }

    [Serializable]
    public struct GripPose
    {
        public GripTarget Target;
        public bool FirstPerson;
        public Vector3 Position;
        public Quaternion Rotation;
    }

    [Serializable]
    public sealed class GripPoseTable
    {
        public List<GripPose> Entries = new();

        public bool TryGet(GripTarget target, bool firstPerson, out Pose pose)
        {
            foreach (var entry in Entries)
                if (entry.Target == target && entry.FirstPerson == firstPerson)
                {
                    pose = new Pose(entry.Position, entry.Rotation.normalized);
                    return true;
                }
            pose = Pose.identity;
            return false;
        }

        public void Set(GripTarget target, bool firstPerson, Pose pose)
        {
            var value = new GripPose { Target = target, FirstPerson = firstPerson, Position = pose.position, Rotation = pose.rotation };
            int index = Entries.FindIndex(entry => entry.Target == target && entry.FirstPerson == firstPerson);
            if (index >= 0) Entries[index] = value; else Entries.Add(value);
        }

        public bool Remove(GripTarget target, bool firstPerson) =>
            Entries.RemoveAll(entry => entry.Target == target && entry.FirstPerson == firstPerson) > 0;

        public void CopyFrom(GripPoseTable source)
        {
            Entries.Clear();
            if (source != null) Entries.AddRange(source.Entries);
        }
    }

    [Serializable]
    public sealed class AvatarGripPoses
    {
        public AvatarId Avatar;
        public GripPoseTable Poses = new();
        public AnimationClip Fingers;
    }

    public static class GripPoses
    {
        public const float ReferenceHeight = 1.8f;
        public static float Scale(AvatarSettings settings) => settings ? settings.VisualHeight / ReferenceHeight : 1f;
        internal const float DirtyDistance = 0.00001f, DirtyAngle = 0.01f;
        internal static bool Differs(Pose a, Pose b) =>
            (a.position - b.position).sqrMagnitude > DirtyDistance * DirtyDistance || Quaternion.Angle(a.rotation, b.rotation) > DirtyAngle;

        public static bool IsHint(GripTarget target) => target is GripTarget.RightElbowHold or GripTarget.RightElbowCharge or
            GripTarget.LeftElbowHold or GripTarget.LeftElbowCharge or GripTarget.ContactElbow;

        public static bool InSlotFrame(GripTarget target) => target is GripTarget.HoldPose or GripTarget.ChargePose or
            GripTarget.RightElbowHold or GripTarget.RightElbowCharge or GripTarget.LeftElbowHold or GripTarget.LeftElbowCharge;

        public static bool Uses(HoldSlotMode mode, GripTarget target) => target switch
        {
            GripTarget.HoldPose or GripTarget.ChargePose or GripTarget.RightHand or
                GripTarget.RightElbowHold or GripTarget.RightElbowCharge => true,
            GripTarget.LeftHand or GripTarget.LeftElbowCharge => mode != HoldSlotMode.Hand,
            GripTarget.LeftElbowHold => mode == HoldSlotMode.Heavy,
            GripTarget.PouchDraw => mode == HoldSlotMode.Slingshot,
            _ => false
        };

        public static bool Required(HoldSlotMode mode, GripTarget target) => Uses(mode, target) && !IsHint(target);

        public static AvatarGripPoses FindEntry(List<AvatarGripPoses> list, AvatarId avatar)
        {
            if (list == null) return null;
            foreach (var entry in list) if (entry != null && entry.Avatar == avatar) return entry;
            return null;
        }

        public static GripPoseTable Find(List<AvatarGripPoses> list, AvatarId avatar) => FindEntry(list, avatar)?.Poses;

        public static AvatarGripPoses EnsureEntry(List<AvatarGripPoses> list, AvatarId avatar)
        {
            var entry = FindEntry(list, avatar);
            if (entry != null) return entry;
            entry = new AvatarGripPoses { Avatar = avatar };
            list.Add(entry);
            return entry;
        }

        public static GripPoseTable Ensure(List<AvatarGripPoses> list, AvatarId avatar) => EnsureEntry(list, avatar).Poses;

        public static AnimationClip ResolveFingers(ItemDefinition item, AvatarId avatar, out GripLayer layer)
        {
            if (item)
            {
                var entry = FindEntry(item.AvatarGripPoses, avatar);
                if (entry != null && entry.Fingers) { layer = GripLayer.Avatar; return entry.Fingers; }
                if (item.GripFingers) { layer = GripLayer.Item; return item.GripFingers; }
                if (item.HoldSlot && item.HoldSlot.Fingers) { layer = GripLayer.Slot; return item.HoldSlot.Fingers; }
            }
            layer = GripLayer.None;
            return null;
        }

        public static bool TryResolve(HoldSlot slot, ItemDefinition item, AvatarId avatar, GripTarget target, bool firstPerson,
            out Pose pose, out GripLayer layer)
        {
            if (item)
            {
                var overrides = Find(item.AvatarGripPoses, avatar);
                if (overrides != null && overrides.TryGet(target, firstPerson, out pose)) { layer = GripLayer.Avatar; return true; }
                if (item.GripPoses != null && item.GripPoses.TryGet(target, firstPerson, out pose)) { layer = GripLayer.Item; return true; }
            }
            if (slot && slot.Defaults.TryGet(target, firstPerson, out pose)) { layer = GripLayer.Slot; return true; }
            pose = Pose.identity; layer = GripLayer.None;
            return false;
        }
    }
}
