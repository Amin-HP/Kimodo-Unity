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
        [Range(0f, 1f)] public float ghostOpacity = 0.51f;

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
            var motion = g != null ? g.Motion : null;
            if (motion == null || keys.Count == 0 || motion.clips == null || motion.clips.Count == 0)
                return null;

            int ci = Mathf.Clamp(g.clipIndex, 0, motion.clips.Count - 1);
            var clip = motion.clips[ci];
            int J = motion.jointCount;
            int fc = motion.frameCount;

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
                    if (clip.localQuats == null || clip.localQuats.Length < (k.frame + 1) * J * 4) continue;
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
                    for (int j = 0; j < motion.bones.Count && j < k.jointActive.Length; j++)
                    {
                        if (!k.jointActive[j]) continue;
                        string name = motion.bones[j].name;
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
    }
}
