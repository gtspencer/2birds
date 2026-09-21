using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace TwoBirds
{
    public sealed class CauldronPresentation : MonoBehaviour
    {
        [SerializeField] private PanelRenderer contentsPanel;
        [SerializeField] private GameObject occupied, ready;
        [SerializeField] private ParticleSystem insertionVfx, brewVfx, successVfx, failureVfx, disposalVfx;
        private sealed class Arrival
        {
            public IngredientRecord Ingredient;
            public GameObject Visual;
            public bool Predicted, Arrived, Silent;
        }
        private readonly List<Arrival> arrivals = new();
        private readonly Image[] icons = new Image[3];
        private Cauldron cauldron;
        private CauldronRecord state;
        private WorldItemRegistry Registry => WorldItemRegistry.Instance;
        private void Awake()
        {
            cauldron = GetComponent<Cauldron>();
            contentsPanel.RegisterUIReloadCallback(Reload);
        }
        private void Reload(PanelRenderer panel, VisualElement root, int version)
        {
            root.pickingMode = PickingMode.Ignore;
            for (int i = 0; i < icons.Length; i++) icons[i] = root.Q<Image>("ingredient-" + i);
            RefreshIcons();
        }
        internal void Apply(CauldronRecord next, bool snapshot)
        {
            var previous = state;
            state = next;
            if (occupied) occupied.SetActive(next.Phase is CauldronPhase.Occupied or CauldronPhase.Brewing);
            if (ready) ready.SetActive(next.Phase == CauldronPhase.Ready);
            RefreshIcons();
            if (next.Ingredients != null)
                foreach (var ingredient in next.Ingredients)
                {
                    var existing = arrivals.Find(value => value.Ingredient.WorldId == ingredient.WorldId && value.Ingredient.Player == ingredient.Player && value.Ingredient.Operation == ingredient.Operation &&
                        (value.Predicted || value.Ingredient.StartTick == ingredient.StartTick));
                    if (existing != null) { existing.Ingredient = ingredient; existing.Predicted = false; continue; }
                    if (Registry.ServerTick < ingredient.StartTick + Registry.DurationTicks(cauldron.InsertionSeconds)) AddArrival(ingredient, false, snapshot);
                }
            if (!snapshot && next.Phase != previous.Phase)
            {
                var vfx = next.Phase switch { CauldronPhase.Brewing => brewVfx, CauldronPhase.Rising => successVfx,
                    CauldronPhase.Failed => failureVfx, CauldronPhase.Disposing => disposalVfx, _ => null };
                if (vfx) vfx.Play();
            }
            if (next.Phase != CauldronPhase.Occupied)
            {
                foreach (var arrival in arrivals) if (arrival.Visual) Destroy(arrival.Visual);
                arrivals.Clear();
            }
        }
        private void RefreshIcons()
        {
            for (int i = 0; i < icons.Length; i++)
            {
                if (icons[i] == null) continue;
                icons[i].sprite = Registry && state.Ingredients != null && i < state.Ingredients.Count
                    ? Registry.GetDefinition(state.Ingredients[i].Definition).Icon : null;
            }
        }
        internal void Predict(InventoryRequest request, int player)
        {
            AddArrival(new IngredientRecord { Definition = request.DefinitionId, WorldId = request.Ids[0], Player = player, Operation = request.Operation,
                StartTick = Registry.ServerTick, Position = request.Position, Rotation = request.Rotation }, true, false);
        }
        internal void Reject(uint operation)
        {
            for (int i = arrivals.Count - 1; i >= 0; i--)
            {
                if (!arrivals[i].Predicted || arrivals[i].Ingredient.Operation != operation) continue;
                if (arrivals[i].Visual) Destroy(arrivals[i].Visual);
                arrivals.RemoveAt(i);
            }
        }
        private void AddArrival(IngredientRecord ingredient, bool predicted, bool silent)
        {
            var definition = Registry.GetDefinition(ingredient.Definition);
            var source = definition.WorldPrefab.transform.Find("VisualRoot");
            var visual = Instantiate(source.gameObject, ingredient.Position, ingredient.Rotation);
            visual.transform.localScale = Vector3.Scale(source.localScale, definition.WorldPrefab.transform.localScale);
            if (definition.WorldPrefab.TryGetComponent<PotionPresentation>(out var presentation)) presentation.ApplyVisual(visual, definition);
            arrivals.Add(new Arrival { Ingredient = ingredient, Visual = visual, Predicted = predicted, Silent = silent });
        }
        private void LateUpdate()
        {
            if (!Registry || Registry.Replaying || !cauldron.IsSpawned) return;
            for (int i = arrivals.Count - 1; i >= 0; i--)
            {
                var arrival = arrivals[i];
                float t = Mathf.Clamp01(((long)Registry.ServerTick - arrival.Ingredient.StartTick) * (float)Registry.TickDelta / cauldron.InsertionSeconds);
                if (arrival.Visual)
                {
                    Vector3 a = Vector3.Lerp(arrival.Ingredient.Position, cauldron.CurveAnchor.position, t);
                    Vector3 b = Vector3.Lerp(cauldron.CurveAnchor.position, cauldron.IntakeAnchor.position, t);
                    arrival.Visual.transform.position = Vector3.Lerp(a, b, t);
                }
                if (t < 1f || arrival.Arrived) continue;
                arrival.Arrived = true;
                if (!arrival.Silent && insertionVfx) insertionVfx.Play();
                if (arrival.Visual) Destroy(arrival.Visual);
            }
            if (state.Output == 0 || !Registry.TryGetItem(state.Output, out var item)) return;
            float elapsed = Mathf.Max(0f, (float)(((long)Registry.ServerTick - state.StartTick +
                cauldron.TimeManager.GetTickPercentAsDouble()) * Registry.TickDelta));
            bool available = state.Phase == CauldronPhase.Ready;
            float rise = available ? 1f : Mathf.Clamp01(elapsed / cauldron.RiseSeconds);
            Vector3 position = Vector3.Lerp(cauldron.IntakeAnchor.position, cauldron.OutputAnchor.position, rise);
            Quaternion rotation = cauldron.OutputAnchor.rotation;
            if (available)
            {
                position += Vector3.up * (Mathf.Sin(elapsed * Mathf.PI * .75f) * 0.05f);
                rotation *= Quaternion.Euler(0f, elapsed * 90f, 0f);
            }
            item.PresentOutput(new Pose(position, rotation), available);
        }
        private void OnDestroy()
        {
            if (contentsPanel) contentsPanel.UnregisterUIReloadCallback(Reload);
            foreach (var arrival in arrivals) if (arrival.Visual) Destroy(arrival.Visual);
        }
    }
}
