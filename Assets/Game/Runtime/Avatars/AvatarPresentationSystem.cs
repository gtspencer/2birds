using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwoBirds
{
    [DefaultExecutionOrder(200)]
    public sealed class AvatarPresentationSystem : MonoBehaviour
    {
        internal static readonly ProfilerMarker PrepareMarker = new("Avatar.Prepare"), EvaluateMarker = new("Avatar.Evaluate"),
            IkMarker = new("Avatar.IK"), VrmMarker = new("Avatar.Vrm"), SpringsMarker = new("Avatar.Springs"), CommitMarker = new("Avatar.Commit");
        private static readonly Dictionary<Scene, AvatarPresentationSystem> systems = new();
        private readonly List<AvatarPresentation> hosts = new(8);
        private Transform staging;

        internal static AvatarPresentationSystem ForScene(Scene scene)
        {
            if (systems.TryGetValue(scene, out var found) && found) return found;
            var root = new GameObject("Avatar presentation system");
            SceneManager.MoveGameObjectToScene(root, scene);
            return root.AddComponent<AvatarPresentationSystem>();
        }

        private void Awake()
        {
            systems[gameObject.scene] = this;
            var stage = new GameObject("Avatar staging");
            stage.transform.SetParent(transform, false);
            stage.SetActive(false);
            staging = stage.transform;
            AvatarSpringBatch.Retain();
        }

        internal void Register(AvatarPresentation host) { if (!hosts.Contains(host)) hosts.Add(host); }
        internal void Unregister(AvatarPresentation host) => hosts.Remove(host);

        private void LateUpdate()
        {
            // One driver orders all scene hosts before the singleton spring batch.
            foreach (var pair in systems)
            {
                if (!pair.Value || !pair.Value.isActiveAndEnabled) continue;
                if (pair.Value != this) return;
                break;
            }
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            bool prepared = false;
            foreach (var pair in systems)
            {
                var system = pair.Value;
                for (int i = 0; i < system.hosts.Count; i++)
                {
                    var host = system.hosts[i];
                    if (!host || host.Failed) continue;
                    try
                    {
                        host.UpdateInput(dt, Time.deltaTime > 0.25f);
                        if (!prepared && host.NeedsPreparation)
                        {
                            prepared = true;
                            using (PrepareMarker.Auto())
                                try { host.Prepare(system.staging); }
                                catch (Exception exception) { host.PreparationFailed(exception); }
                        }
                        host.PrepareTargets();
                        using (EvaluateMarker.Auto()) host.Evaluate(dt);
                    }
                    catch (Exception exception) { host.PresentationFailed(exception); }
                }
            }
            using (SpringsMarker.Auto())
            {
                AvatarSpringBatch.Process(dt);
            }
            foreach (var pair in systems)
                for (int i = 0; i < pair.Value.hosts.Count; i++)
                {
                    var host = pair.Value.hosts[i];
                    if (!host || host.Failed) continue;
                    host.Commit();
                }
        }

        private void OnDisable() => AvatarSpringBatch.Flush();

        private void OnDestroy()
        {
            for (int i = 0; i < hosts.Count; i++) if (hosts[i]) hosts[i].ReleaseInstances();
            hosts.Clear();
            systems.Remove(gameObject.scene);
            AvatarSpringBatch.Release();
        }
    }
}
