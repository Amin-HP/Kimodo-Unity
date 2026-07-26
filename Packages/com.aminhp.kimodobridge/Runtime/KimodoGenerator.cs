// SPDX-License-Identifier: Apache-2.0
// Per-character generation component. Lives on a Humanoid character (next to its
// Animator), holds the generation parameters, requests motion from a KimodoBridge,
// and previews the result on the character via KimodoPlayer (Mecanim retarget).
//
// The editor (KimodoGeneratorEditor) drives play/scrub and baking; this component
// owns the state so a sibling KimodoEffectors/KimodoWaypoints can read the current motion + frame.
// Generated data and the preview engine are transient (rebuilt on Generate).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace AminHP.KimodoBridge
{
    [AddComponentMenu("Kimodo/Kimodo Generator")]
    [RequireComponent(typeof(Animator))]
    public class KimodoGenerator : MonoBehaviour
    {
        [Tooltip("Bridge manager to generate through. Auto-found in the scene if left empty.")]
        public KimodoBridge bridge;

        [Tooltip("Humanoid Animator to preview on. Defaults to this GameObject's Animator.")]
        public Animator target;

        [Header("Prompt")]
        [Tooltip("Motion prompt. Periods separate sequential segments.")]
        [TextArea(2, 5)] public string prompt = "A person walks forward.";

        [Tooltip("Seconds. Space-separated to give one duration per segment.")]
        public string duration = "4.0";

        [Header("Sampling")]
        [Range(1, 8)] public int numSamples = 1;
        [Range(10, 200)] public int diffusionSteps = 100;
        public bool useSeed = false;
        public int seed = 0;
        [Tooltip("Foot-skate cleanup (ignored for G1).")]
        public bool postprocess = true;

        [Tooltip("How strongly constraints (hand/foot targets, waypoints) are enforced. Raise it if a " +
                 "target is ignored (e.g. a foot won't lift to a raised target); too high can look stiff.")]
        [Range(1f, 8f)] public float constraintWeight = 3f;

        [Header("Preview / Bake")]
        [Tooltip("Multiplies root travel. Auto-fit on each Generate; tweak if the character shoots across the scene.")]
        public float rootMotionScale = 1f;
        public KimodoRootBake rootBakeMode = KimodoRootBake.Travel;
        public bool loop = true;
        public int clipIndex = 0;

        // ---- transient runtime state ----
        [NonSerialized] public KimodoMotion Motion;
        [NonSerialized] public KimodoPlayer Preview;
        [NonSerialized] public bool Busy;
        [NonSerialized] public string Info = "";
        [NonSerialized] public float PreviewTime;   // shared with the editors (playback head)
        [NonSerialized] public bool Playing;

        public Animator ResolvedTarget => target != null ? target : (target = GetComponent<Animator>());
        public KimodoBridge ResolvedBridge => bridge != null ? bridge : (bridge = FindBridge());

        public int FrameCount => Motion != null ? Motion.frameCount : 0;
        public float Fps => Motion != null ? Motion.fps : 30f;
        public float Duration => Preview != null ? Preview.Duration : (FrameCount / Mathf.Max(1f, Fps));

        /// <summary>Preview frame currently under the playback head.</summary>
        public int CurrentFrame =>
            (Motion == null || Motion.frameCount <= 0)
                ? 0
                : Mathf.Clamp(Mathf.RoundToInt(PreviewTime * Fps), 0, Motion.frameCount - 1);

        private static KimodoBridge FindBridge()
        {
#if UNITY_2022_2_OR_NEWER
            return UnityEngine.Object.FindFirstObjectByType<KimodoBridge>();
#else
            return UnityEngine.Object.FindObjectOfType<KimodoBridge>();
#endif
        }

        /// <summary>Request motion from the bridge (with any sibling constraints) and preview it.</summary>
        public void Generate(Action onDone = null)
        {
            var b = ResolvedBridge;
            if (b == null) { Info = "No KimodoBridge in the scene. Add one (GameObject ▸ Kimodo ▸ Bridge Manager)."; onDone?.Invoke(); return; }
            if (!b.IsOnline) { Info = "Bridge is not connected. Press Connect on the KimodoBridge."; onDone?.Invoke(); return; }

            // Gather constraints from any sibling providers (hand/foot targets + root waypoints).
            var built = new List<KimodoConstraint>();
            var eff = GetComponent<KimodoEffectors>();
            if (eff != null) { var l = eff.BuildConstraints(); if (l != null) built.AddRange(l); }
            var wp = GetComponent<KimodoWaypoints>();
            if (wp != null) { var r = wp.BuildRootConstraints(); if (r != null) built.AddRange(r); }
            var pc = GetComponent<KimodoPoseConstraints>();
            if (pc != null) { var l = pc.BuildConstraints(); if (l != null) built.AddRange(l); }

            Busy = true;
            Info = "Requesting…";
            var req = new KimodoGenerateRequest
            {
                prompt = prompt,
                model = b.model,
                duration = duration,
                num_samples = numSamples,
                diffusion_steps = diffusionSteps,
                seed = useSeed ? seed : -1,
                postprocess = postprocess,
                include_positions = false,
                constraints = built.Count > 0 ? built : null,
                // Only override the constraint guidance weight when we actually send constraints.
                cfg_weight = built.Count > 0 ? new[] { 2f, constraintWeight } : null,
            };
            b.GetClient().Generate(req, (ok, motion, err) =>
            {
                if (!this) return;
                Busy = false;
                if (!ok)
                {
                    Info = "Generation failed: " + err;
                    onDone?.Invoke();
                    return;
                }
                Motion = motion;
                clipIndex = 0;
                PreviewTime = 0f;
                Info = $"Got {motion.frameCount} frames @ {motion.fps}fps · {motion.jointCount} joints · " +
                       $"{motion.clips.Count} sample(s) · {motion.generationSeconds}s";
                RebindPreview();
                onDone?.Invoke();
            });
        }

        /// <summary>(Re)build the retarget preview for the current motion + target. Auto-fits root scale.</summary>
        public bool RebindPreview()
        {
            Playing = false;
            Preview?.Dispose();
            Preview = null;
            var tgt = ResolvedTarget;
            if (tgt == null || Motion == null) return false;
            if (tgt.avatar == null || !tgt.avatar.isHuman)
            {
                Info = "Target's avatar is not Humanoid. Set the model's Rig → Animation Type → Humanoid.";
                return false;
            }
            Preview = new KimodoPlayer();
            if (!Preview.Bind(tgt, Motion))
            {
                Preview.Dispose();
                Preview = null;
                return false;
            }
            rootMotionScale = Preview.AutoCalibrateRootMotion(clipIndex);
            Preview.RootMotionScale = rootMotionScale;
            SampleCurrent();
            return true;
        }

        public bool IsPreviewBound => Preview != null && Preview.IsBound;

        /// <summary>Pose the target at an explicit time (seconds) and record it as the playback head.</summary>
        public void SampleTime(float seconds)
        {
            PreviewTime = seconds;
            if (IsPreviewBound) Preview.SampleTime(clipIndex, seconds, loop);
        }

        /// <summary>Re-pose the target at the current playback head.</summary>
        public void SampleCurrent() => SampleTime(PreviewTime);

        public void ApplyRootScale()
        {
            if (!IsPreviewBound) return;
            Preview.RootMotionScale = rootMotionScale;
            SampleCurrent();
        }

        public float AutoFitRootScale()
        {
            if (!IsPreviewBound) return rootMotionScale;
            rootMotionScale = Preview.AutoCalibrateRootMotion(clipIndex);
            ApplyRootScale();
            return rootMotionScale;
        }

        public void DisposePreview()
        {
            Playing = false;
            Preview?.Dispose();
            Preview = null;
        }

        private void OnDisable() => DisposePreview();
    }
}
