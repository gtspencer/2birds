#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FishNet.Object.Synchronizing;
using UnityEngine;
using UnityEngine.UIElements;

namespace TwoBirds
{
    public sealed class InventoryValidation : MonoBehaviour
    {
        private static ItemCatalog fixture;
        private readonly HashSet<PlayerInventory> seeded = new();
        public static bool Enabled => MvpValidation.Argument("-inventoryValidation") == "true";
        public static ItemCatalog Catalog
        {
            get
            {
                if (fixture != null) return fixture;
                fixture = ScriptableObject.CreateInstance<ItemCatalog>();
                var stack = ScriptableObject.CreateInstance<ItemDefinition>();
                stack.DefinitionId = "fixture-stack"; stack.DisplayName = "Supplies"; stack.MaximumStack = 10;
                var unique = ScriptableObject.CreateInstance<ItemDefinition>();
                unique.DefinitionId = "fixture-unique"; unique.DisplayName = "Keepsake"; unique.MaximumStack = 1;
                fixture.Definitions = new[] { stack, unique };
                return fixture;
            }
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            if (!Enabled) return;
            var go = new GameObject("Inventory validation"); DontDestroyOnLoad(go); go.AddComponent<InventoryValidation>();
        }
        private void Update()
        {
            foreach (var player in FindObjectsByType<PlayerInventory>())
            {
                if (!player.IsServerInitialized || !seeded.Add(player)) continue;
                Check(player.ServerAdd("fixture-stack", 15).Accepted == 15, "server grant");
                Check(player.ServerAdd("fixture-unique", 2).Accepted == 2, "unique grant");
                var health = player.GetComponent<PlayerHealth>();
                Check(health.ServerSet(50, 100) && health.Snapshot.Current == 50, "health 50/100");
                Check(health.ServerSet(-5, 100) && health.Snapshot.Current == 0, "health clamps to zero");
                Check(!health.ServerSet(10, 0) && health.Snapshot.Maximum == 100, "health rejects zero maximum");
                Check(health.ServerSet(500, 50) && health.Snapshot.Current == 50, "health clamps to maximum");
                Check(health.ServerSet(25, 50), "health setter");
                StartCoroutine(ServerChanges(player));
            }
        }
        private static IEnumerator ServerChanges(PlayerInventory player)
        {
            yield return new WaitForSecondsRealtime(2);
            if (player == null || !player.IsServerInitialized) yield break;
            var health = player.GetComponent<PlayerHealth>();
            health.ServerSet(50, 100);
            yield return new WaitForSecondsRealtime(0.5f);
            health.ServerSet(0, 100);
            yield return new WaitForSecondsRealtime(0.5f);
            health.ServerSet(25, 50);
            yield return new WaitForSecondsRealtime(5);
            if (player != null && player.IsServerInitialized) player.ServerAdd("fixture-stack", 1);
        }
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("INVENTORY FAIL: " + message);
            Debug.Log("INVENTORY PASS: " + message);
        }
        private IEnumerator Start()
        {
            while (true)
            {
                var session = SessionController.Instance;
                if (session == null || session.Phase != SessionPhase.InGame || session.LocalPlayer == null) { yield return null; continue; }
                var player = session.LocalPlayer.GetComponent<PlayerInventory>();
                var ownerHealth = player.GetComponent<PlayerHealth>();
                var observedHealth = new HashSet<string>();
                void ObserveHealth() => observedHealth.Add($"{ownerHealth.Snapshot.Current}/{ownerHealth.Snapshot.Maximum}");
                ownerHealth.Changed += ObserveHealth;
                while (player != null && player.Snapshot.Slots == null) yield return null;
                if (player == null) continue;
                while (player != null && player.Snapshot.Slots.Sum(x => x.Quantity) < 17) yield return null;
                if (player == null) continue;
                yield return new WaitForSecondsRealtime(0.5f);
                Check(player.Snapshot.Slots.Sum(x => x.Quantity) == 17, "owner initial inventory");
                Check(player.GetComponent<PlayerHealth>().Snapshot.Maximum == 50, "owner health initial sync");
                foreach (var remote in FindObjectsByType<PlayerInventory>())
                    if (!remote.IsOwner && !remote.IsServerInitialized)
                    {
                        var sync = (SyncVar<InventorySnapshot>)typeof(PlayerInventory).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(remote);
                        Check(sync.Value.Slots == null && remote.Snapshot.Slots == null, "observer receives no inventory payload");
                        Check(!remote.Request(0, 1, false), "nonowner cannot submit mutation");
                        Check(remote.ServerAdd("fixture-stack", 1).Accepted == 0, "client cannot grant");
                        Check(!remote.GetComponent<PlayerHealth>().ServerSet(10, 100), "client cannot write health");
                    }
                var before = player.Snapshot;
                Check(player.Request(0, 1, false), "submit merge");
                Check(player.Pending || player.Snapshot.Revision > before.Revision, "pending or host completion");
                yield return Wait(player);
                Check(player.Snapshot.Get(0).Quantity == 5 && player.Snapshot.Get(1).Quantity == 10, "merge confirmed");
                typeof(PlayerInventory).GetMethod("Accept", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(player, new object[] { before });
                Check(player.Snapshot.Revision > before.Revision && player.Snapshot.Get(1).Quantity == 10, "late snapshot cannot roll back confirmed state");
                ulong revision = player.Snapshot.Revision;
                var submit = typeof(PlayerInventory).GetMethod("Submit", BindingFlags.Instance | BindingFlags.NonPublic);
                submit.Invoke(player, new object[] { new InventoryRequest { RequestId = 1, ExpectedRevision = before.Revision,
                    EntryId = before.Get(0).EntryId, Source = 0, Destination = 1 }, null });
                yield return new WaitForSecondsRealtime(1f);
                Check(player.Snapshot.Revision == revision, "duplicate suppressed");
                Check(player.Request(0, -1, true), "submit unavailable drop");
                yield return Wait(player);
                Check(player.Snapshot.Revision == revision && player.Message == "Dropping items is not available yet", "drop preserves contents and feedback");
                for (int i = 0; i < 6; i++)
                {
                    Check(player.Request(i % 2 == 0 ? 0 : 8, i % 2 == 0 ? 8 : 0, false), "repeated move");
                    session.SetInventory(false);
                    yield return Wait(player);
                    Check(player.Snapshot.Slots.Sum(x => x.Quantity) == 17, "close during request conserves contents");
                }
                for (int i = 0; i < 10; i++)
                {
                    session.SetPanel(false); session.SetInventory(true);
                    Check(!session.GameplayAllowed && session.InventoryOpen, "inventory gates gameplay");
                    session.CancelModal(); Check(!session.InventoryOpen && !session.PanelOpen, "cancel closes only inventory");
                    session.CancelModal(); Check(session.PanelOpen && !session.GameplayAllowed, "cancel opens session");
                    session.CancelModal(); Check(!session.PanelOpen, "cancel resumes");
                }
                var document = FindObjectsByType<UIDocument>().Single(x => x.GetComponent<InventoryHudPresenter>() != null);
                Check(document.rootVisualElement.Query<InventorySlotView>().ToList().Count == 32, "one HUD with shared slot presentations");
                session.SetInventory(true);
                yield return null;
                var grid = document.rootVisualElement.Q("inventory-row-0");
                var slot = grid.ElementAt(0);
                Vector2 position = slot.worldBound.center;
                using (var down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0, mousePosition = position })) slot.SendEvent(down);
                using (var move = PointerMoveEvent.GetPooled(new Event { type = EventType.MouseDrag, button = 0, mousePosition = position + Vector2.right * 10 })) slot.SendEvent(move);
                session.CancelModal();
                Check(session.InventoryOpen, "Escape cancels captured drag before closing inventory");
                Check(player.Snapshot.Slots.Sum(x => x.Quantity) == 17 && !player.Pending, "canceled drag preserves contents");
                using (var down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0, mousePosition = position })) slot.SendEvent(down);
                session.SetInventory(false);
                Check(!slot.HasPointerCapture(PointerId.mousePointerId), "close releases pointer capture");
                session.SetInventory(true);
                yield return null;
                position = slot.worldBound.center;
                using (var down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0, mousePosition = position })) slot.SendEvent(down);
                float grantDeadline = Time.unscaledTime + 12;
                while (player != null && player.Snapshot.Slots.Sum(x => x.Quantity) == 17 && Time.unscaledTime < grantDeadline) yield return null;
                Check(player != null && player.Snapshot.Slots.Sum(x => x.Quantity) == 18, "concurrent server grant converges");
                Check(!slot.HasPointerCapture(PointerId.mousePointerId), "server grant cancels captured drag");
                Check(observedHealth.Contains("50/100") && observedHealth.Contains("0/100") && observedHealth.Contains("25/50"), "owner receives all health transitions");
                ownerHealth.Changed -= ObserveHealth;
                Debug.Log("INVENTORY VALIDATION COMPLETE");
                while (player != null && session.Phase == SessionPhase.InGame) yield return null;
            }
        }
        private static IEnumerator Wait(PlayerInventory player)
        {
            float deadline = Time.unscaledTime + 12;
            while (player != null && player.Pending && Time.unscaledTime < deadline) yield return null;
            Check(player != null && !player.Pending, "request completed");
        }
        private void OnDestroy()
        {
            if (fixture == null) return;
            foreach (var definition in fixture.Definitions) Destroy(definition);
            Destroy(fixture); fixture = null;
        }
    }
}
#endif
