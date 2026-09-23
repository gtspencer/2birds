#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;

namespace TwoBirds
{
    public static class GripAuthoringSession
    {
        public const string SceneName = "AvatarPresentationDemo";
        public const string ScenePath = "Assets/Scenes/AvatarPresentationDemo.unity";
        public static GripAuthoringDrafts Drafts { get; private set; }
        public static Func<GripAuthoringDrafts> EditorDrafts;
        public static Action BeforeEditorEdit;
        public static event Action Changed;
        private static ItemRegistry sourceItems;
        private static bool ownsDrafts;
        public static void RetainForEditor(GripAuthoringDrafts drafts) { if (Drafts == drafts) ownsDrafts = false; }
        public static void ReleaseToSession() => ownsDrafts = true;
        public static void ResetEditorSession() { Drafts = null; sourceItems = null; ownsDrafts = false; }
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntime() => ResetEditorSession();
        public static void Begin(SessionController session)
        {
            if (Drafts != null) return;
            sourceItems = WorldItemRegistry.Instance.Catalog;
            Drafts = EditorDrafts?.Invoke(); ownsDrafts = Drafts == null;
            Drafts ??= new GripAuthoringDrafts();
            if (!Drafts.Items) Drafts.Initialize(sourceItems, session.Avatars);
            WorldItemRegistry.Instance.BindCatalog(Drafts.Items);
            session.PresentationRegistryOverride = Drafts.Avatars;
            Changed?.Invoke();
        }
        public static void End()
        {
            if (Drafts == null) return;
            if (WorldItemRegistry.Instance) WorldItemRegistry.Instance.BindCatalog(sourceItems);
            if (SessionController.Instance) SessionController.Instance.PresentationRegistryOverride = null;
            if (ownsDrafts) Drafts.Dispose();
            Drafts = null; sourceItems = null; Changed?.Invoke();
        }
    }
}
#endif
