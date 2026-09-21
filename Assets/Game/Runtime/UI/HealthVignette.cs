using UnityEngine;
using UnityEngine.UIElements;

namespace TwoBirds
{
    internal sealed class HealthVignette : VisualElement
    {
        private readonly IVisualElementScheduledItem fade;
        private Vertex[] vertices;
        private ushort[] indices;
        private float hitOpacity, persistentOpacity, duration, threshold, hitTime = float.NegativeInfinity;
        private PlayerHealth health;

        internal HealthVignette()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = style.top = style.right = style.bottom = 0;
            RegisterCallback<GeometryChangedEvent>(_ => BuildMesh());
            generateVisualContent += Draw;
            fade = schedule.Execute(Refresh).Every(16);
            fade.Pause();
            style.opacity = 0f;
        }
        internal void Bind(PlayerHealth value, float hit, float persistent, float seconds, float lowHealth)
        {
            health = value;
            hitOpacity = hit;
            persistentOpacity = persistent;
            duration = seconds;
            threshold = lowHealth;
            hitTime = float.NegativeInfinity;
            Refresh();
        }
        internal void Hit()
        {
            hitTime = Time.unscaledTime;
            Refresh();
            fade.Resume();
        }
        internal void Refresh()
        {
            if (!health || health.IsDowned)
            {
                hitTime = float.NegativeInfinity;
                style.opacity = 0f;
                fade.Pause();
                return;
            }
            float remaining = duration > 0f ? Mathf.Clamp01(1f - (Time.unscaledTime - hitTime) / duration) : 0f;
            float persistent = health.Normalized < threshold ? persistentOpacity : 0f;
            style.opacity = Mathf.Lerp(persistent, hitOpacity, remaining);
            if (remaining == 0f) fade.Pause();
        }
        private void BuildMesh()
        {
            const int columns = 25, rows = 17;
            vertices = new Vertex[columns * rows];
            indices = new ushort[(columns - 1) * (rows - 1) * 6];
            int index = 0;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < columns; x++)
                {
                    float u = x / (float)(columns - 1), v = y / (float)(rows - 1);
                    float edge = Mathf.Max(Mathf.Abs(u * 2f - 1f), Mathf.Abs(v * 2f - 1f));
                    float alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 1f, edge));
                    int i = y * columns + x;
                    vertices[i] = new Vertex { position = new Vector3(u * contentRect.width, v * contentRect.height, Vertex.nearZ),
                        tint = new Color(0.85f, 0.015f, 0.025f, alpha) };
                    if (x == columns - 1 || y == rows - 1) continue;
                    indices[index++] = (ushort)i; indices[index++] = (ushort)(i + columns); indices[index++] = (ushort)(i + 1);
                    indices[index++] = (ushort)(i + 1); indices[index++] = (ushort)(i + columns); indices[index++] = (ushort)(i + columns + 1);
                }
            MarkDirtyRepaint();
        }
        private void Draw(MeshGenerationContext context)
        {
            if (vertices == null || contentRect.width <= 0f) return;
            var mesh = context.Allocate(vertices.Length, indices.Length);
            mesh.SetAllVertices(vertices);
            mesh.SetAllIndices(indices);
        }
    }
}
