#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace TwoBirds
{
    internal sealed class AILogCapture : IDisposable
    {
        private readonly SessionController host;
        private readonly AILogWriter run;
        private readonly List<WorldItem> focused = new(7);
        private readonly List<WorldItem> selected = new(7);
        private WorldItem target, held;
        private readonly WaitForSecondsRealtime baselineWait = new(0.5f), burstWait = new(0.1f);
        private PlayerMotor player;
        private PlayerInputReader input;
        private Camera camera;
        private Coroutine sampler, screenshot;
        private double burstUntil, lastSample = double.NegativeInfinity, lastAccepted = double.NegativeInfinity;
        private long pendingCapture;
        private bool active, bursting;

        internal AILogCapture(SessionController host, AILogWriter run) { this.host = host; this.run = run; }

        internal void Bind(PlayerMotor value)
        {
            player = value;
            input = player ? player.GetComponent<PlayerInputReader>() : null;
            camera = player ? player.GetComponent<PlayerPresentation>().ViewCamera : null;
            focused.Clear();
            target = held = null;
        }

        internal void CameraBound(PlayerMotor owner, Camera value)
        {
            if (owner == player) camera = value;
        }

        internal void PlayerStopped(PlayerMotor value)
        {
            if (player != value) return;
            SetGameplay(false, "player_stopped");
            Bind(null);
        }

        internal void SetGameplay(bool value, string reason = "session_phase")
        {
            if (active == value) return;
            active = value;
            if (AILogger.Enabled) AILogger.Log(value ? "capture.started" : "capture.ended", new { scope = "local_player_camera_focused_items", reason });
            if (value) sampler = host.StartCoroutine(SampleLoop());
            else
            {
                if (host && sampler != null) host.StopCoroutine(sampler);
                sampler = null;
                focused.Clear();
                EndBurst();
                burstUntil = 0;
            }
        }

        internal void Focus(WorldItem item)
        {
            if (!item) return;
            focused.Remove(item);
            if (focused.Count == 7) focused.RemoveAt(0);
            focused.Add(item);
        }

        internal void TargetChanged(WorldItem item)
        {
            target = item;
            Focus(item);
        }

        internal void HeldChanged(WorldItem item, bool equipped)
        {
            if (equipped) held = item;
            else if (held == item) held = null;
        }

        internal void Forget(WorldItem item)
        {
            focused.Remove(item);
            if (target == item) target = null;
            if (held == item) held = null;
        }

        internal void Burst(string reason)
        {
            if (!active) return;
            burstUntil = Time.realtimeSinceStartupAsDouble + 5;
            if (!bursting)
            {
                bursting = true;
                AILogger.Log("capture.burst.started", new { reason, duration_s = 5 });
                if (sampler != null) host.StopCoroutine(sampler);
                sampler = host.StartCoroutine(SampleLoop());
            }
            else AILogger.Log("capture.burst.extended", new { reason, duration_s = 5 });
        }

        private void EndBurst()
        {
            if (!bursting) return;
            bursting = false;
            AILogger.Log("capture.burst.ended", new { });
        }

        private IEnumerator SampleLoop()
        {
            bool first = true;
            while (active && run.Usable)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                bool burst = now < burstUntil;
                if (!burst) EndBurst();
                if (now - lastSample >= (burst ? 0.1 : 0.5))
                {
                    Sample(first ? "trigger" : "coroutine_after_update", null);
                    lastSample = now;
                }
                first = false;
                yield return burst ? burstWait : baselineWait;
            }
        }

        private void Sample(string phase, long? capture, long? captureTime = null)
        {
            if (!run.Usable) return;
            var items = new List<object>(7);
            selected.Clear();
            Select(target);
            Select(held);
            for (int i = focused.Count - 1; i >= 0; i--)
            {
                var item = focused[i];
                if (!item || !item.gameObject.activeInHierarchy) { focused.RemoveAt(i); continue; }
                Select(item);
            }
            foreach (var item in selected) items.Add(item.AISnapshot());
            AILogger.Log("state.sample", new
            {
                phase, capture_id = capture, capture_t_us = captureTime, cadence = bursting ? "burst" : "baseline",
                scope = "local_player_camera_focused_items", player = player ? player.AISnapshot() : null,
                camera = camera ? new { position = AILogger.V(camera.transform.position), rotation = AILogger.Q(camera.transform.rotation), fov_degrees = camera.fieldOfView } : null,
                gameplay_input = input && input.GameplayActive, inventory_open = input && input.InventoryOpen,
                input_suppressed = input && input.InputSuppressed,
                target_subject = target ? "item:" + target.Record.Motion.Id : null,
                held_subject = held ? "item:" + held.Record.Motion.Id : null,
                console_open = host.ConsoleOpen, session_panel_open = host.PanelOpen, items
            }, player ? player.AIContext("client") : default);
        }

        private void Select(WorldItem item)
        {
            if (item && item.gameObject.activeInHierarchy && selected.Count < 7 && !selected.Contains(item)) selected.Add(item);
        }

        internal void Screenshot()
        {
            long id = run.NextCapture();
            AILogger.Log("screenshot.requested", new { capture_id = id, request_frame = Time.frameCount, request_t_us = run.TimeUs });
            Burst("screenshot");
            string reason = !AILogger.CaptureAvailable ? "batch_or_headless" :
                pendingCapture != 0 || run.ImagePending ? "pending_image" :
                Time.realtimeSinceStartupAsDouble - lastAccepted < 1 ? "throttled" : null;
            if (reason != null) { Outcome(id, "skipped", reason); return; }
            pendingCapture = id;
            lastAccepted = Time.realtimeSinceStartupAsDouble;
            screenshot = host.StartCoroutine(CaptureFrame(id));
        }

        private IEnumerator CaptureFrame(long id)
        {
            yield return new WaitForEndOfFrame();
            Texture2D texture = null;
            try
            {
                int frame = Time.frameCount;
                long time = run.TimeUs;
                texture = ScreenCapture.CaptureScreenshotAsTexture();
                if (!texture) { Outcome(id, "failed", "no_texture"); yield break; }
                Sample("end_of_frame", id, time);
                byte[] png = texture.EncodeToPNG();
                if (png == null) Outcome(id, "failed", "encoding_failed");
                else if (png.Length > 16 * 1024 * 1024) Outcome(id, "skipped", "png_size_limit");
                else if (!run.QueueImage(png, new JObject
                {
                    ["capture_id"] = id, ["capture_frame"] = frame, ["capture_t_us"] = time,
                    ["width"] = texture.width, ["height"] = texture.height
                }, AILogger.Context(default))) Outcome(id, "skipped", "writer_unavailable");
            }
            catch (Exception exception) { Outcome(id, "failed", exception.GetType().Name); }
            finally
            {
                if (texture) UnityEngine.Object.Destroy(texture);
                pendingCapture = 0;
                screenshot = null;
            }
        }

        private static void Outcome(long id, string outcome, string reason) =>
            AILogger.Log("screenshot." + outcome, new { capture_id = id, reason });

        public void Dispose()
        {
            SetGameplay(false, "shutdown");
            if (host && screenshot != null) host.StopCoroutine(screenshot);
            if (pendingCapture != 0) Outcome(pendingCapture, "skipped", "shutdown_before_render");
            pendingCapture = 0;
            screenshot = null;
            Bind(null);
        }
    }
}
#endif
