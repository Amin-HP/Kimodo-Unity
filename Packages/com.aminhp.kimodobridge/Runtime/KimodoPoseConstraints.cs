// SPDX-License-Identifier: Apache-2.0
// Whole-rig (full-body) pose constraints — like the demo's fullbody keyframes. Each key holds an
// editable per-joint pose (Kimodo local rotations + root) that you author in the Scene view with a
// per-joint rotation gizmo (see KimodoPoseConstraintsEditor). Sent as a 'fullbody' constraint.
//
// Each key can also DEACTIVATE joints (per key): inactive joints are dropped from the constraint so
// the model is free there (e.g. constrain only the upper body), and the ghost mesh fades them out.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace AminHP.KimodoBridge
{
    [AddComponentMenu("Kimodo/Kimodo Pose Constraints")]
    [RequireComponent(typeof(KimodoGenerator))]
    public class KimodoPoseConstraints : MonoBehaviour
    {
        [Serializable]
        public class Key
        {
            public int frame;
            public float[] localQuats;   // Kimodo local rotations, J*4 (wxyz); authored in the editor
            public Vector3 root;         // Kimodo pelvis position
            public bool hasPose;         // false = pin the current motion's pose at this frame
            public bool show = true;     // draw this key's pose skeleton in the Scene (default on)
            public bool[] jointActive;   // per-joint active flag (length J); null/empty = all active.
                                         // Inactive joints are dropped from the constraint + faded.
            public string prompt;        // optional: text used by "Generate pose" to fill localQuats
                                         // (Kimodo makes a short clip and samples one frame's pose).
            public bool useWaypoints;    // per-key: generate this pose IN-CONTEXT along the waypoint path
                                         // (slower). Off = prompt-only clip grafted onto this frame's facing.
        }

        // The body joints a user can toggle/constrain (SOMA names). Fingers/eyes/jaw follow their
        // parent. Shared by the editor (gizmos) and the constraint builder.
        public static readonly string[] BodyJointNames =
        {
            "Hips", "Spine1", "Spine2", "Chest", "Neck1", "Neck2", "Head",
            "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
            "RightShoulder", "RightArm", "RightForeArm", "RightHand",
            "LeftLeg", "LeftShin", "LeftFoot", "LeftToeBase",
            "RightLeg", "RightShin", "RightFoot", "RightToeBase",
        };

        [Tooltip("Full-body pose keyframes (authored with per-joint gizmos), sent as 'fullbody' constraints.")]
        public List<Key> keys = new List<Key>();

        [Tooltip("Generator to feed constraints to. Defaults to the one on this GameObject.")]
        public KimodoGenerator generator;

        [Header("Ghost mesh")]
        [Tooltip("Show the character's mesh (skin/mesh renderers) at each shown key's pose, as a " +
                 "transparent white ghost, so you can see the model — not just the skeleton — as you edit.")]
        public bool showGhostMesh = true;

        [Tooltip("Opacity of the ghost mesh (transparency slider).")]
        [Range(0f, 1f)] public float ghostOpacity = 0.2f;

        [Header("Generate pose from prompt")]
        [Tooltip("Length of the throwaway clip generated to sample a pose from a key's prompt (seconds).")]
        [Range(0.3f, 4f)] public float poseGenSeconds = 1.5f;

        [Tooltip("Which frame of that clip to take as the pose: 0 = first, 1 = last (poses usually settle by the end).")]
        [Range(0f, 1f)] public float poseSampleAt = 1f;

        [System.NonSerialized] public bool GeneratingPose;   // true while a pose-from-prompt request is in flight

        public KimodoGenerator ResolvedGenerator =>
            generator != null ? generator : (generator = GetComponent<KimodoGenerator>());

        /// <summary>True if the key deactivates at least one joint (so it is a partial pose).</summary>
        public static bool HasInactive(Key k)
        {
            if (k.jointActive == null) return false;
            for (int i = 0; i < k.jointActive.Length; i++) if (!k.jointActive[i]) return true;
            return false;
        }

        public List<KimodoConstraint> BuildConstraints()
        {
            var g = ResolvedGenerator;
            var sk = g != null ? g.PoseSkeleton : null;   // real motion, or the rest skeleton pre-generation
            if (sk == null || sk.bones == null || sk.bones.Count == 0 || keys.Count == 0) return null;

            // The real motion is only needed for the "pin the natural frame pose" fallback (hasPose=false).
            var motion = g.Motion;
            var clip = (motion != null && motion.clips != null && motion.clips.Count > 0)
                ? motion.clips[Mathf.Clamp(g.clipIndex, 0, motion.clips.Count - 1)] : null;
            int J = sk.jointCount;
            int fc = g.AuthoringFrameCount;

            var result = new List<KimodoConstraint>();
            foreach (var k in keys)
            {
                if (k.frame < 0 || k.frame >= fc) continue;
                float[] q;
                Vector3 root;
                if (k.hasPose && k.localQuats != null && k.localQuats.Length == J * 4)
                {
                    q = k.localQuats;
                    root = k.root;
                }
                else
                {
                    // No authored pose: pin the motion's natural pose here — needs a real motion.
                    if (clip == null || clip.localQuats == null || clip.localQuats.Length < (k.frame + 1) * J * 4) continue;
                    q = new float[J * 4];
                    Array.Copy(clip.localQuats, k.frame * J * 4, q, 0, J * 4);
                    root = new Vector3(clip.rootPositions[k.frame * 3], clip.rootPositions[k.frame * 3 + 1],
                                       clip.rootPositions[k.frame * 3 + 2]);
                }

                // If any joint is deactivated, send only the active BODY joints' names so the server
                // pins just those positions (partial pose). All active => omit => full-body pose.
                string[] active = null;
                if (HasInactive(k) && k.jointActive != null)
                {
                    var names = new List<string>();
                    for (int j = 0; j < sk.bones.Count && j < k.jointActive.Length; j++)
                    {
                        if (!k.jointActive[j]) continue;
                        string name = sk.bones[j].name;
                        if (Array.IndexOf(BodyJointNames, name) >= 0) names.Add(name);
                    }
                    if (names.Count == 0) continue; // nothing active -> no constraint for this key
                    active = names.ToArray();
                }

                result.Add(new KimodoConstraint
                {
                    type = "fullbody",
                    frameIndices = new[] { k.frame },
                    localQuats = q,
                    rootPositions = new[] { root.x, root.y, root.z },
                    jointNames = Array.Empty<string>(),
                    activeJoints = active,
                });
            }
            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// Fill <paramref name="key"/>'s pose (localQuats) from its <c>prompt</c>: Kimodo generates a short
        /// throwaway clip and one frame of it is taken as the pose. The key's frame + root (its position /
        /// waypoint) are left untouched — only the joint rotations are replaced. Async; onDone(ok, error).
        /// </summary>
        public void GeneratePoseForKey(Key key, Action<bool, string> onDone = null)
        {
            var g = ResolvedGenerator;
            var b = g != null ? g.ResolvedBridge : null;
            if (b == null || !b.IsOnline) { onDone?.Invoke(false, "Bridge is not connected (press Connect)."); return; }
            if (key == null || string.IsNullOrWhiteSpace(key.prompt)) { onDone?.Invoke(false, "This key has no prompt."); return; }

            // Per-key waypoint mode: generate over the full timeline WITH the waypoint path applied, then
            // sample this key's own frame. Otherwise a quick clip from the prompt alone.
            bool inContext = key.useWaypoints && g.Motion != null;
            string dur = inContext
                ? g.duration
                : Mathf.Max(0.3f, poseGenSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var cons = inContext ? GatherWaypointConstraints(g) : null;
            bool hasCons = cons != null && cons.Count > 0;

            var req = new KimodoGenerateRequest
            {
                prompt = key.prompt,
                model = b.model,
                duration = dur,
                num_samples = 1,
                diffusion_steps = g.diffusionSteps,
                seed = -1,
                postprocess = true,
                include_positions = false,
                constraints = hasCons ? cons : null,
                cfg_weight = hasCons ? new[] { 2f, g.constraintWeight } : null,
            };

            GeneratingPose = true;
            b.GetClient().Generate(req, (ok, motion, err) =>
            {
                GeneratingPose = false;
                if (!this) return;
                if (!ok || motion == null || motion.clips == null || motion.clips.Count == 0)
                { onDone?.Invoke(false, "Pose generation failed: " + err); return; }

                int J = motion.jointCount, T = motion.frameCount;
                var mainMotion = g.Motion;
                if (mainMotion != null && mainMotion.jointCount != J)
                { onDone?.Invoke(false, "Pose model skeleton doesn't match the current motion."); return; }

                var clip = motion.clips[0];
                if (T < 1 || clip.localQuats == null || clip.localQuats.Length < T * J * 4)
                { onDone?.Invoke(false, "Generated clip had no usable pose."); return; }

                // In-context: sample the key's own frame; otherwise the chosen fraction of the short clip.
                int f = inContext
                    ? Mathf.Clamp(key.frame, 0, T - 1)
                    : Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(poseSampleAt) * (T - 1)), 0, T - 1);
                var q = new float[J * 4];
                Array.Copy(clip.localQuats, f * J * 4, q, 0, J * 4);

                if (inContext)
                {
                    // The sampled frame is already at the waypoint with the right facing + height.
                    key.localQuats = q;
                    key.root = new Vector3(clip.rootPositions[f * 3], clip.rootPositions[f * 3 + 1], clip.rootPositions[f * 3 + 2]);
                }
                else
                {
                    // Graft the generated limbs onto THIS frame's root + facing from the main motion, so the
                    // pose keeps the character's real direction and pelvis height at key.frame.
                    GraftOntoMainFrame(g, key, q, J);
                }
                key.hasPose = true;
                onDone?.Invoke(true, null);
            });
        }

        // Replace the generated pose's root (Hips) rotation + root position with the main motion's values at
        // key.frame, so a prompt-only pose faces the same way and sits at the same height as the walk there.
        private static void GraftOntoMainFrame(KimodoGenerator g, Key key, float[] genQuats, int J)
        {
            var m = g.Motion;
            if (m != null && m.clips != null && m.clips.Count > 0)
            {
                var clip = m.clips[Mathf.Clamp(g.clipIndex, 0, m.clips.Count - 1)];
                int f = Mathf.Clamp(key.frame, 0, m.frameCount - 1);
                int root = m.rootIndex;
                if (clip.localQuats != null && clip.localQuats.Length >= (f + 1) * J * 4 && root >= 0 && root < J)
                {
                    Array.Copy(clip.localQuats, f * J * 4 + root * 4, genQuats, root * 4, 4); // main Hips rotation
                    key.root = new Vector3(clip.rootPositions[f * 3], clip.rootPositions[f * 3 + 1], clip.rootPositions[f * 3 + 2]);
                }
            }
            key.localQuats = genQuats;
        }

        // The sibling KimodoWaypoints' root path, used to ground a prompt-generated pose on the waypoint.
        private static List<KimodoConstraint> GatherWaypointConstraints(KimodoGenerator g)
        {
            var wp = g.GetComponent<KimodoWaypoints>();
            return wp != null ? wp.BuildRootConstraints() : null;
        }

        /// <summary>Seed a key from the generated motion's own pose at its frame ("align to frame").
        /// Returns false when there is no motion to read (caller can fall back to <see cref="SetIdlePose"/>).
        /// Undo/dirty is the caller's job.</summary>
        public bool AlignKeyToMotion(Key key)
        {
            var g = ResolvedGenerator;
            var m = g != null ? g.Motion : null;
            if (key == null || m == null || m.clips == null || m.clips.Count == 0) return false;
            var clip = m.clips[Mathf.Clamp(g.clipIndex, 0, m.clips.Count - 1)];
            int J = m.jointCount;
            int f = Mathf.Clamp(key.frame, 0, m.frameCount - 1);
            if (clip.localQuats == null || clip.localQuats.Length < (f + 1) * J * 4) return false;
            key.localQuats = new float[J * 4];
            Array.Copy(clip.localQuats, f * J * 4, key.localQuats, 0, J * 4);
            key.root = new Vector3(clip.rootPositions[f * 3], clip.rootPositions[f * 3 + 1],
                                   clip.rootPositions[f * 3 + 2]);
            key.hasPose = true;
            return true;
        }

        /// <summary>Reset a key to a neutral idle (rest / T-pose) while keeping its frame + root (pinned
        /// position). A quick way to clear a pose before editing or regenerating.</summary>
        public void SetIdlePose(Key key)
        {
            var g = ResolvedGenerator;
            int J = g != null && g.Motion != null ? g.Motion.jointCount
                   : (key.localQuats != null ? key.localQuats.Length / 4 : 0);
            if (J <= 0) return;
            var q = new float[J * 4];
            for (int j = 0; j < J; j++) q[j * 4] = 1f;   // identity quaternion (w,x,y,z) = (1,0,0,0)
            key.localQuats = q;
            key.hasPose = true;
        }
    }
}
