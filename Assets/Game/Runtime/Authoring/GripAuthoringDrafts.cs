#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TwoBirds
{
    public enum GripAuthoringContext { Item, AvatarCalibration, SharedDefaults }

    [Serializable]
    public sealed class GripItemValues
    {
        public ItemPalmContact RightPalmContact, LeftPalmContact;
        public AnimationClip GripFingers;
        public float ThrowChargeTime;
        public ItemPalmContact PullingPalmContact;
        public float RecoverySeconds;
    }

    [Serializable]
    public sealed class GripAvatarValues
    {
        public PalmCorrection LeftPalmCorrection, RightPalmCorrection;
        public float VisualHeight, YawOffset;
        public Vector3 StandingOffset, FirstPersonPlacementOffset, FirstPersonReachOffset;
    }

    [Serializable]
    public sealed class GripHeldDefaultsValues
    {
        public HoldModePoses OneHand, TwoHand, Slingshot;
    }

    [Serializable]
    public sealed class GripFirstPersonValues
    {
        public Vector3 ShoulderOffset;
        public FirstPersonHandsSettings.Hand Left, Right;
        public float BlendTime, PlacementBlendTime, Bounce, FallStrength, LandingStrength, LandingDuration;
    }

    [Serializable]
    public sealed class GripAnimationValues
    {
        public AnimationClip RelaxedFingers, GripFingers, OpenFingers;
    }

    [Serializable]
    public sealed class GripAuthoringDraft
    {
        public string Key, DisplayName, SourceIdentity;
        public GripAuthoringContext Context;
        public ScriptableObject Source;
        public byte ItemId;
        public AvatarId AvatarId;
        [SerializeReference] public object Values;
        [SerializeReference] public object Baseline;
        [NonSerialized] public ScriptableObject Runtime;
        public bool Dirty => JsonUtility.ToJson(Values) != JsonUtility.ToJson(Baseline);
        public string Label => DisplayName + (Dirty ? " *" : "");

        public void Apply()
        {
            if (!Runtime) return;
            CopyFields(Values, Runtime);
            Notify(Runtime);
        }
        public void Saved() => Baseline = Copy(Values);
        public void Revert() { Values = Copy(Baseline); Apply(); }
        public void RefreshBaseline() => CopyFields(Source, Baseline);
        public void Dispose()
        {
            if (!Runtime) return;
            if (Application.isPlaying) Object.Destroy(Runtime); else Object.DestroyImmediate(Runtime);
            Runtime = null;
        }
        public static object Copy(object value) => JsonUtility.FromJson(JsonUtility.ToJson(value), value.GetType());
        public static void CopyFields(object from, object to)
        {
            foreach (var field in to.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                var source = from.GetType().GetField(field.Name);
                if (source == null || source.FieldType != field.FieldType) continue;
                object value = source.GetValue(from);
                if (value is FirstPersonHandsSettings.Hand) value = Copy(value);
                field.SetValue(to, value);
            }
        }
        public static void Notify(ScriptableObject value)
        {
            switch (value)
            {
                case ItemDefinition item: item.NotifyContentChanged(); break;
                case AvatarSettings avatar: avatar.NotifyContentChanged(); break;
                case HeldItemSettings held: held.NotifyContentChanged(); break;
                case FirstPersonHandsSettings hands: hands.NotifyContentChanged(); break;
                case AvatarAnimationSet animations: animations.NotifyContentChanged(); break;
            }
        }
    }

    [Serializable]
    public sealed class GripAuthoringDrafts : IDisposable
    {
        public List<GripAuthoringDraft> Records = new();
        public byte SelectedItem = 1;
        public AvatarId SelectedAvatar;
        public GripAuthoringContext Context;
        public string SharedKey = "shared-held-item-settings";
        [NonSerialized] public ItemRegistry Items;
        [NonSerialized] public AvatarRegistry Avatars;
        [NonSerialized] private ItemRegistry sourceItems;
        [NonSerialized] private AvatarRegistry sourceAvatars;
        public event Action Changed;
        public event Action<GripAuthoringDraft> ContentChanged;
        public GripAuthoringDraft Current => Find(Context switch
        {
            GripAuthoringContext.Item => "item-" + SelectedItem,
            GripAuthoringContext.AvatarCalibration => "avatar-" + SelectedAvatar,
            _ => SharedKey
        });
        public GripAuthoringDraft Find(string key) => Records.Find(record => record.Key == key);
        public HeldItemSettings Held => (HeldItemSettings)Find("shared-held-item-settings").Runtime;

        public void Initialize(ItemRegistry items, AvatarRegistry avatars)
        {
            DisposeRuntime(); sourceItems = items; sourceAvatars = avatars;
            Items = Object.Instantiate(items); Items.hideFlags = HideFlags.HideAndDontSave;
            Avatars = Object.Instantiate(avatars); Avatars.hideFlags = HideFlags.HideAndDontSave;
            items.ContentChanged += Reconcile; avatars.ContentChanged += Reconcile;
            Reconcile();
            if (!SelectedAvatar.IsValid) SelectedAvatar = avatars.DefaultId;
        }
        private GripAuthoringDraft Add(string key, ScriptableObject source, object values, GripAuthoringContext context)
        {
            var record = Find(key);
            if (record == null)
            {
                GripAuthoringDraft.CopyFields(source, values);
                record = new GripAuthoringDraft { Key = key, Source = source, SourceIdentity = source.name,
                    DisplayName = source.name, Context = context, Values = values, Baseline = GripAuthoringDraft.Copy(values) };
                Records.Add(record);
            }
            record.Source = source;
            if (!record.Runtime)
            {
                record.Runtime = Object.Instantiate(source);
                record.Runtime.name = source.name; record.Runtime.hideFlags = HideFlags.HideAndDontSave;
                record.Apply();
            }
            return record;
        }
        public void Reconcile()
        {
            if (!sourceItems || !sourceAvatars) return;
            Items.HeldItemDefaults = (HeldItemSettings)Add("shared-held-item-settings", sourceItems.HeldItemDefaults,
                new GripHeldDefaultsValues(), GripAuthoringContext.SharedDefaults).Runtime;
            Avatars.FirstPerson = (FirstPersonHandsSettings)Add("shared-first-person-hands-settings", sourceAvatars.FirstPerson,
                new GripFirstPersonValues(), GripAuthoringContext.SharedDefaults).Runtime;
            Avatars.Animations = (AvatarAnimationSet)Add("shared-avatar-animation-set", sourceAvatars.Animations,
                new GripAnimationValues(), GripAuthoringContext.SharedDefaults).Runtime;
            var definitions = new List<ItemDefinition>();
            foreach (var source in sourceItems.Items)
            {
                if (!source) continue;
                var record = Add("item-" + source.ItemId, source, new GripItemValues(), GripAuthoringContext.Item);
                record.ItemId = source.ItemId; record.DisplayName = source.ItemName;
                definitions.Add((ItemDefinition)record.Runtime);
            }
            Items.Items = definitions.ToArray();
            if (!Items.Get(SelectedItem) && definitions.Count > 0) SelectedItem = definitions[0].ItemId;
            Avatars.Entries = new List<AvatarRegistry.Entry>();
            foreach (var entry in sourceAvatars.Entries)
            {
                if (entry == null || !entry.Settings) continue;
                var record = Add("avatar-" + entry.Id, entry.Settings, new GripAvatarValues(), GripAuthoringContext.AvatarCalibration);
                record.AvatarId = entry.Id; record.DisplayName = entry.Settings.DisplayName;
                Avatars.Entries.Add(new AvatarRegistry.Entry { Id = entry.Id, Source = entry.Source, SourceGuid = entry.SourceGuid,
                    Prefab = entry.Prefab, FirstPersonPrefab = entry.FirstPersonPrefab, Settings = (AvatarSettings)record.Runtime });
            }
            if (!Avatars.Entries.Exists(entry => entry.Id == SelectedAvatar)) SelectedAvatar = Avatars.DefaultId;
            Items.NotifyContentChanged(); Avatars.Invalidate(); Changed?.Invoke();
        }
        public void Edit(GripAuthoringDraft record, Action mutation)
        {
            mutation(); record.Apply(); ContentChanged?.Invoke(record); Changed?.Invoke();
        }
        public void Revert(GripAuthoringDraft record)
        { record.Revert(); ContentChanged?.Invoke(record); Changed?.Invoke(); }
        public void Saved(GripAuthoringDraft record) { record.Saved(); Changed?.Invoke(); }
        public void Refresh()
        {
            foreach (var record in Records) { record.Apply(); ContentChanged?.Invoke(record); }
            Changed?.Invoke();
        }
        public void SelectionChanged() => Changed?.Invoke();
        public void DisposeRuntime()
        {
            if (sourceItems) sourceItems.ContentChanged -= Reconcile;
            if (sourceAvatars) sourceAvatars.ContentChanged -= Reconcile;
            foreach (var record in Records) record.Dispose();
            if (Items) { if (Application.isPlaying) Object.Destroy(Items); else Object.DestroyImmediate(Items); }
            if (Avatars) { if (Application.isPlaying) Object.Destroy(Avatars); else Object.DestroyImmediate(Avatars); }
            Items = null; Avatars = null; sourceItems = null; sourceAvatars = null;
        }
        public void Dispose() => DisposeRuntime();
    }
}
#endif
