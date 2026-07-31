// SPDX-License-Identifier: Apache-2.0
// Per-character generation component. Lives on a Humanoid character (next to its
// Animator), holds the generation parameters, requests motion from a KimodoBridge,
// and previews the result on the character via KimodoPlayer (Mecanim retarget).
//
// The editor (KimodoGeneratorEditor) drives play/scrub and baking; this component
// owns the state so a sibling KimodoEndEffectors/KimodoWaypoints can read the current motion + frame.
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
        [Tooltip("Timeline asset driving this character. When assigned, its segments compose the prompt " +
                 "and durations below (edit it in Window ▸ Kimodo ▸ Timeline).")]
        public KimodoTimeline timeline;

        [Tooltip("Motion prompt. Periods separate sequential segments. Ignored while a timeline is assigned.")]
        [TextArea(2, 5)] public string prompt = "A person walks forward.";

        [Tooltip("Seconds. Space-separated to give one duration per segment. Ignored while a timeline is assigned.")]
        public string duration = "4.0";

        [Header("Sampling")]
        [Tooltip("How many variations one Generate returns. The editor always asks for one: extra " +
                 "samples cost a full generation each, and everything downstream — preview, bake, " +
                 "frozen timeline segments — works on a single clip. Kept for code that wants a batch.")]
        [HideInInspector] [Range(1, 8)] public int numSamples = 1;
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

        // Identifies this character's cached motion on disk (Library/KimodoMotionCache). Serialized so
        // the same file is found again after a compile or a restart; hidden because it is plumbing.
        [SerializeField, HideInInspector] private string motionCacheId;

        // ---- transient runtime state ----
        [NonSerialized] public KimodoMotion Motion;
        [NonSerialized] public KimodoPlayer Preview;
        [NonSerialized] public bool Busy;
        [NonSerialized] public string Info = "";
        // Filled in while Busy by KimodoProgressPoller, from the server's /progress. -1 = not known
        // yet (an older server, or the request has not reached the model).
        [NonSerialized] public float Progress01 = -1f;
        [NonSerialized] public string ProgressLabel = "";
        [NonSerialized] public float PreviewTime;   // shared with the editors (playback head)
        [NonSerialized] public bool Playing;
        [NonSerialized] public float PlaybackSpeed = 1f;   // editor playback rate (see KimodoPlayback)

        public Animator ResolvedTarget => target != null ? target : (target = GetComponent<Animator>());
        public KimodoBridge ResolvedBridge => bridge != null ? bridge : (bridge = FindBridge());

        public int FrameCount => Motion != null ? Motion.frameCount : 0;
        public float Fps => Motion != null ? Motion.fps : 30f;

        /// <summary>True when a timeline asset with at least one usable segment drives this character.</summary>
        public bool UsingTimeline => timeline != null && timeline.HasContent;

        /// <summary>The prompt actually sent: composed from the timeline when there is one.</summary>
        public string EffectivePrompt => UsingTimeline ? timeline.BuildPrompt() : prompt;

        /// <summary>The duration string actually sent: one value per timeline segment when there is one.</summary>
        public string EffectiveDuration => UsingTimeline ? timeline.BuildDuration() : duration;

        /// <summary>Bones-only skeleton fetched by the bridge on connect (for pre-generation authoring).</summary>
        public KimodoMotion Skeleton => ResolvedBridge != null ? ResolvedBridge.Skeleton : null;

        /// <summary>The real motion if generated, else the connected bridge's rest skeleton — whichever can
        /// supply the bone list / joint count for drawing + building constraints.</summary>
        public KimodoMotion PoseSkeleton => Motion != null ? Motion : Skeleton;

        /// <summary>True when there's something (motion or fetched skeleton) to author constraints against.</summary>
        public bool HasAuthoringSkeleton =>
            PoseSkeleton != null && PoseSkeleton.bones != null && PoseSkeleton.bones.Count > 0;

        /// <summary>Estimated frame count from the requested duration (× fps) when there's no motion yet.</summary>
        public int EstimatedFrameCount
        {
            get
            {
                float fps = Skeleton != null && Skeleton.fps > 0f ? Skeleton.fps : 30f;
                float secs = 0f;
                foreach (var part in (EffectiveDuration ?? "").Split(' '))
                    if (float.TryParse(part, System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out var s)) secs += s;
                if (secs <= 0f) secs = 4f;
                return Mathf.Max(2, Mathf.RoundToInt(secs * fps));
            }
        }

        /// <summary>Frame count to author against — i.e. what the NEXT Generate will produce. With a
        /// timeline that is its own segment math (which mirrors the server's int(seconds × fps) per
        /// segment), so retiming a segment moves the authoring axis immediately instead of waiting for
        /// a regenerate; otherwise the current motion's length, or the estimate before the first one.</summary>
        public int AuthoringFrameCount =>
            UsingTimeline ? timeline.TotalFrames(Fps)
                          : (Motion != null ? Motion.frameCount : EstimatedFrameCount);
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

            var built = GatherUserConstraints();

            // Frozen segments are not generated at all: only the runs between them are requested, and
            // the kept frames are spliced in. See KimodoFrozenGenerate.
            if (UsingTimeline && PoseSkeleton != null && timeline.AnyFrozenContent)
            {
                Busy = true;
                Info = "Generating the live segments…";
                KimodoFrozenGenerate.Run(this, built,
                    progress => { Info = progress; },
                    (ok, motion, err) =>
                    {
                        if (!this) return;
                        Busy = false;
                        ClearProgress();
                        if (!ok) { Info = "Generation failed: " + err; onDone?.Invoke(); return; }
                        AdoptMotion(motion, " (frozen segments kept)");
                        onDone?.Invoke();
                    });
                return;
            }

            Busy = true;
            Info = "Requesting…";
            var req = new KimodoGenerateRequest
            {
                prompt = EffectivePrompt,
                model = b.model,
                duration = EffectiveDuration,
                num_samples = Mathf.Max(1, numSamples),
                diffusion_steps = diffusionSteps,
                seed = useSeed ? seed : -1,
                postprocess = postprocess,
                include_positions = false,
                constraints = built.Count > 0 ? built : null,
                // Only override the constraint guidance weight when we actually send constraints.
                cfg_weight = built.Count > 0 ? new[] { 2f, constraintWeight } : null,
                // Timeline segments may each ask for their own constraint strength.
                segment_cfg_weights = built.Count > 0 && UsingTimeline
                    ? timeline.BuildSegmentConstraintWeights(constraintWeight) : null,
            };
            b.GetClient().Generate(req, (ok, motion, err) =>
            {
                if (!this) return;
                Busy = false;
                if (!ok)
                {
                    ClearProgress();
                    Info = "Generation failed: " + err;
                    onDone?.Invoke();
                    return;
                }
                AdoptMotion(motion, null);
                onDone?.Invoke();
            });
        }

        /// <summary>The constraints the character's own components ask for (hand/foot keys, root
        /// waypoints, full-body poses), in absolute timeline frames.</summary>
        public List<KimodoConstraint> GatherUserConstraints()
        {
            var built = new List<KimodoConstraint>();
            var eff = GetComponent<KimodoEndEffectors>();
            if (eff != null) { var l = eff.BuildConstraints(); if (l != null) built.AddRange(l); }
            var wp = GetComponent<KimodoWaypoints>();
            if (wp != null) { var r = wp.BuildRootConstraints(); if (r != null) built.AddRange(r); }
            var pc = GetComponent<KimodoPoseConstraints>();
            if (pc != null) { var l = pc.BuildConstraints(); if (l != null) built.AddRange(l); }
            return built;
        }

        /// <summary>Clear the progress readout (a generate finished, failed, or never started).</summary>
        public void ClearProgress() { Progress01 = -1f; ProgressLabel = ""; }

        private void AdoptMotion(KimodoMotion motion, string note)
        {
            ClearProgress();
            Motion = motion;
            clipIndex = 0;
            PreviewTime = 0f;
            Info = $"Got {motion.frameCount} frames @ {motion.fps}fps · {motion.jointCount} joints · " +
                   $"{motion.clips.Count} sample(s) · {motion.generationSeconds}s" + note;
            RebindPreview();
#if UNITY_EDITOR
            CacheMotion();
#endif
        }

#if UNITY_EDITOR
        /// <summary>Stable id for this character's cached motion, created on first use.</summary>
        public string MotionCacheId
        {
            get
            {
                if (string.IsNullOrEmpty(motionCacheId))
                {
                    motionCacheId = Guid.NewGuid().ToString("N");
                    UnityEditor.EditorUtility.SetDirty(this);
                    // SetDirty alone does not mark a SCENE dirty, and an unsaved scene would lose the id
                    // (and with it the cache) on the next restart.
                    if (gameObject.scene.IsValid())
                        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
                }
                return motionCacheId;
            }
        }

        public bool HasCachedMotion => !string.IsNullOrEmpty(motionCacheId) && KimodoMotionCache.Exists(motionCacheId);
        public long CachedMotionBytes => string.IsNullOrEmpty(motionCacheId) ? 0L : KimodoMotionCache.SizeOf(motionCacheId);

        /// <summary>Keep the current motion on disk so a compile / restart does not lose it.</summary>
        public void CacheMotion()
        {
            if (Motion != null) KimodoMotionCache.Save(MotionCacheId, Motion);
        }

        /// <summary>Bring back the motion this character had before the last domain reload. Returns
        /// false when there is nothing cached (or it no longer loads).</summary>
        public bool RestoreCachedMotion()
        {
            if (Motion != null || string.IsNullOrEmpty(motionCacheId)) return false;
            var m = KimodoMotionCache.Load(motionCacheId);
            if (m == null) return false;
            Motion = m;
            clipIndex = Mathf.Clamp(clipIndex, 0, Mathf.Max(0, m.clips.Count - 1));
            PreviewTime = 0f;
            Info = $"Restored {m.frameCount} frames from the cache (not regenerated).";
            RebindPreview(false);   // keep the root scale the user settled on
            return true;
        }

        /// <summary>Forget the cached motion (the next Generate writes a new one).</summary>
        public void ClearCachedMotion()
        {
            if (!string.IsNullOrEmpty(motionCacheId)) KimodoMotionCache.Delete(motionCacheId);
        }
#endif

        /// <summary>(Re)build the retarget preview for the current motion + target. Auto-fits root scale.</summary>
        public bool RebindPreview() => RebindPreview(true);

        /// <param name="autoFitRootMotion">Re-measure the root travel scale. True on a fresh generate;
        /// false when restoring a cached motion, where the serialized scale is the one the user settled
        /// on and re-fitting it would quietly undo their tuning on every script compile.</param>
        public bool RebindPreview(bool autoFitRootMotion)
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
            if (autoFitRootMotion) rootMotionScale = Preview.AutoCalibrateRootMotion(clipIndex);
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
