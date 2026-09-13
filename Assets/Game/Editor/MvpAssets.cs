using System;
using System.IO;
using FishNet.Component.Transforming.Beta;
using FishNet.Managing;
using FishNet.Managing.Client;
using FishNet.Managing.Object;
using FishNet.Managing.Observing;
using FishNet.Managing.Predicting;
using FishNet.Managing.Server;
using FishNet.Managing.Timing;
using FishNet.Managing.Transporting;
using FishNet.Object;
using FishNet.Component.Observing;
using FishNet.Transporting.Tugboat;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace TwoBirds.Editor
{
    // Explicit editor commands keep the checked-in assets reproducible without runtime construction.
    public static class MvpAssets
    {
        private const string Root = "Assets/Game/";

        [MenuItem("Two Birds/Create MVP Assets (replaces generated scenes and prefabs)")]
        public static void Generate()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            foreach (string folder in new[] { "Prefabs", "Settings", "Materials" }) Directory.CreateDirectory(Root + folder);
            Directory.CreateDirectory("Assets/Scenes");
            AssetDatabase.Refresh();
            ConfigureLayers();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var settings = Asset<GameSettings>(Root + "Settings/GameSettings.asset");
            var panel = Asset<PanelSettings>(Root + "UI/SharedPanel.asset");
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1280, 720);
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 0.5f;
            // Unity supplies its runtime theme as a package asset.
            var themes = AssetDatabase.FindAssets("t:ThemeStyleSheet");
            foreach (string guid in themes)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("Default")) { panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(path); break; }
            }
            if (panel.themeStyleSheet == null)
            {
                string themePath = Root + "UI/RuntimeTheme.tss";
                File.WriteAllText(themePath, "@import url(\"unity-theme://default\");\n");
                AssetDatabase.ImportAsset(themePath);
                panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
            }
            EditorUtility.SetDirty(panel);
            var grey = Material("Capsule", new Color(0.55f, 0.57f, 0.59f));
            var ground = Material("Ground", new Color(0.26f, 0.34f, 0.33f));
            string frictionPath = Root + "Materials/PlayerContact.physicMaterial";
            var friction = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(frictionPath);
            if (friction == null) { friction = new PhysicsMaterial("Player contact"); AssetDatabase.CreateAsset(friction, frictionPath); }
            friction.dynamicFriction = 0f;
            friction.staticFriction = 0f;
            friction.bounciness = 0f;
            friction.frictionCombine = PhysicsMaterialCombine.Minimum;
            EditorUtility.SetDirty(friction);
            NetworkObject player = CreatePlayer(settings, grey, friction);
            var prefabs = Asset<SinglePrefabObjects>(Root + "Settings/SpawnablePlayers.asset");
            prefabs.Clear();
            prefabs.AddObject(player);
            EditorUtility.SetDirty(prefabs);
            var session = CreateSession(settings, prefabs);
            CreateMenu(session, panel);
            CreateGame(player, ground, panel);
            EditorBuildSettings.scenes = new[] {
                new EditorBuildSettingsScene("Assets/Scenes/MainMenu.unity", true),
                new EditorBuildSettingsScene("Assets/Scenes/Game.unity", true) };
            PlayerSettings.runInBackground = true;
            AssetDatabase.SaveAssets();
            EditorSceneManager.OpenScene("Assets/Scenes/MainMenu.unity");
            Debug.Log("MVP assets generated.");
        }

        private static NetworkObject CreatePlayer(GameSettings settings, Material material, PhysicsMaterial friction)
        {
            var go = new GameObject("Player");
            go.layer = 6;
            var body = go.AddComponent<Rigidbody>();
            body.mass = 80f;
            body.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            body.interpolation = RigidbodyInterpolation.None;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.height = 2f;
            capsule.radius = 0.5f;
            capsule.sharedMaterial = friction;
            var network = go.AddComponent<NetworkObject>();
            Set(network, "_enablePrediction", true);
            Set(network, "_predictionType", 1);
            Set(network, "_enableStateForwarding", true);
            go.AddComponent<PlayerNetworkState>();
            Set(go.AddComponent<PlayerInventory>(), "catalog", Asset<ItemCatalog>(Root + "Settings/ItemCatalog.asset"));
            go.AddComponent<PlayerHealth>();
            Set(go.AddComponent<PlayerMotor>(), "settings", settings);
            var graphics = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            graphics.name = "Graphics";
            graphics.layer = 6;
            graphics.transform.SetParent(go.transform, false);
            Object.DestroyImmediate(graphics.GetComponent<Collider>());
            graphics.GetComponent<Renderer>().sharedMaterial = material;
            var smoother = graphics.AddComponent<NetworkTickSmoother>();
            Set(smoother, "_initializationSettings.TargetTransform", go.transform);
            Set(smoother, "_controllerMovementSettings.InterpolationValue", 1);
            Set(smoother, "_spectatorMovementSettings.InterpolationValue", 2);
            Set(go.AddComponent<PlayerPresentation>(), "graphics", graphics.transform);
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, Root + "Prefabs/Player.prefab");
            Object.DestroyImmediate(go);
            return prefab.GetComponent<NetworkObject>();
        }

        private static GameObject CreateSession(GameSettings settings, SinglePrefabObjects prefabs)
        {
            var go = new GameObject("SessionRoot");
            var transport = go.AddComponent<GameTransport>();
            Set(transport, "_enableIpv6", false);
            Set(transport, "_reuseAddress", false);
            Set(transport, "_maximumClients", 2);
            var timing = go.AddComponent<TimeManager>();
            Set(timing, "_tickRate", 60);
            Set(timing, "_physicsMode", 1);
            Set(timing, "_allowTickDropping", false);
            var prediction = go.AddComponent<PredictionManager>();
            Set(prediction, "_stateOrder", (int)ReplicateStateOrder.Inserted);
            Set(prediction, "_reduceReconcilesWithFramerate", false);
            var auth = go.AddComponent<SessionAuthenticator>();
            var server = go.AddComponent<ServerManager>();
            Set(server, "_authenticator", auth);
            Set(server, "_startOnHeadless", false);
            Set(server, "_changeFrameRate", false);
            var client = go.AddComponent<ClientManager>();
            Set(client, "_changeFrameRate", false);
            var tm = go.AddComponent<TransportManager>();
            Set(tm, "Transport", transport);
            go.AddComponent<FishNet.Managing.Scened.SceneManager>();
            var observers = go.AddComponent<ObserverManager>();
            var condition = Asset<SceneCondition>(Root + "Settings/SceneObservation.asset");
            var observerData = new SerializedObject(observers);
            var conditions = observerData.FindProperty("_defaultConditions");
            conditions.arraySize = 1;
            conditions.GetArrayElementAtIndex(0).objectReferenceValue = condition;
            observerData.ApplyModifiedPropertiesWithoutUndo();
            var manager = go.AddComponent<NetworkManager>();
            manager.SpawnablePrefabs = prefabs;
            Set(go.AddComponent<SessionController>(), "settings", settings);
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, Root + "Prefabs/SessionRoot.prefab");
            Object.DestroyImmediate(go);
            return prefab;
        }

        private static void CreateMenu(GameObject session, PanelSettings panel)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            Set(new GameObject("Bootstrap").AddComponent<SessionBootstrap>(), "sessionPrefab", session);
            var camera = new GameObject("Menu camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.08f, 0.11f, 0.14f);
            Document("Menu", panel, "Menu").gameObject.AddComponent<MenuPresenter>();
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/MainMenu.unity");
        }

        private static void CreateGame(NetworkObject player, Material ground, PanelSettings panel)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            plane.name = "Ground (100m)";
            plane.layer = 7;
            plane.isStatic = true;
            plane.transform.localScale = new Vector3(10f, 1f, 10f);
            plane.GetComponent<Renderer>().sharedMaterial = ground;
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.5f;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            RenderSettings.ambientLight = new Color(0.65f, 0.68f, 0.72f);
            var spawner = new GameObject("Player spawner");
            // FishNet's throttled OnValidate can skip IDs during batch scene creation. This is the
            // only scene NetworkObject in the MVP, so give it an explicit stable, nonzero scene ID.
            spawner.AddComponent<NetworkObject>().SetSceneId(0x2B170001);
            var component = spawner.AddComponent<GamePlayerSpawner>();
            Set(component, "playerPrefab", player);
            var data = new SerializedObject(component);
            var points = data.FindProperty("spawnPoints");
            points.arraySize = 2;
            for (int i = 0; i < 2; i++)
            {
                var marker = new GameObject("Spawn " + (i + 1)).transform;
                marker.position = new Vector3(i == 0 ? -2f : 2f, 1.1f, 0f);
                points.GetArrayElementAtIndex(i).objectReferenceValue = marker;
            }
            data.ApplyModifiedPropertiesWithoutUndo();
            var overlay = Document("Session overlay", panel, "Session").gameObject;
            overlay.AddComponent<SessionOverlay>();
            overlay.AddComponent<InventoryHudPresenter>();
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/Game.unity");
        }

        private static UIDocument Document(string name, PanelSettings panel, string uxml)
        {
            var document = new GameObject(name).AddComponent<UIDocument>();
            document.panelSettings = panel;
            document.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(Root + "UI/" + uxml + ".uxml");
            // The stylesheet reference belongs to the UXML so it survives document recreation.
            return document;
        }

        private static T Asset<T>(string path) where T : ScriptableObject
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null) return asset;
            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }
        private static Material Material(string name, Color color)
        {
            string path = Root + "Materials/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { material = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(material, path); }
            material.color = color;
            EditorUtility.SetDirty(material);
            return material;
        }
        private static void Set(Object target, string path, object value)
        {
            var data = new SerializedObject(target);
            var property = data.FindProperty(path) ?? throw new InvalidOperationException($"Missing serialized setting {target.GetType().Name}.{path}");
            if (value is bool boolean) property.boolValue = boolean;
            else if (value is int number) property.intValue = number;
            else property.objectReferenceValue = (Object)value;
            data.ApplyModifiedPropertiesWithoutUndo();
        }
        private static void ConfigureLayers()
        {
            var data = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = data.FindProperty("layers");
            layers.GetArrayElementAtIndex(6).stringValue = "Player";
            layers.GetArrayElementAtIndex(7).stringValue = "Ground";
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        public static void Build()
        {
            MvpChecks.Run();
            string output = Environment.GetEnvironmentVariable("TWOBIRDS_BUILD_PATH") ?? "Builds/Windows/TwoBirds.exe";
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { "Assets/Scenes/MainMenu.unity", "Assets/Scenes/Game.unity" },
                locationPathName = output, target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development });
            if (report.summary.result != BuildResult.Succeeded) throw new Exception("MVP build failed: " + report.summary.result);
        }
        public static void GenerateAndBuild() { Generate(); Build(); }
    }
}

