// SPDX-License-Identifier: Apache-2.0
// Forward kinematics in RAW Kimodo coordinates (right-handed, metres) from a clip's
// per-frame local rotations + root position. Used to locate a joint (e.g. a hand) in
// Kimodo space so constraint targets can be expressed/edited there.
//
// Unity's Quaternion/Vector3 are used purely as math containers here — no KimodoCoords
// conversion — so the outputs are in Kimodo space. Distances are handedness-invariant,
// so scale ratios computed from these match world-space distances.

using UnityEngine;

namespace AminHP.KimodoBridge
{
    public static class KimodoFK
    {
        /// <summary>Global joint positions (Kimodo coords) for one frame of a clip.</summary>
        public static Vector3[] GlobalPositions(KimodoMotion motion, KimodoClip clip, int frame)
        {
            int J = motion.jointCount;
            var gpos = new Vector3[J];
            var grot = new Quaternion[J];
            int qBase = frame * J * 4;
            var root = new Vector3(
                clip.rootPositions[frame * 3 + 0],
                clip.rootPositions[frame * 3 + 1],
                clip.rootPositions[frame * 3 + 2]);

            for (int i = 0; i < J; i++)
            {
                var b = motion.bones[i];
                int qi = qBase + i * 4;
                // wxyz -> Unity ctor (x,y,z,w), treated as raw math (NOT coord-converted).
                var lq = new Quaternion(clip.localQuats[qi + 1], clip.localQuats[qi + 2],
                                        clip.localQuats[qi + 3], clip.localQuats[qi + 0]);
                if (b.parent < 0)
                {
                    grot[i] = lq;
                    gpos[i] = root;
                }
                else
                {
                    // Parents precede children in SOMA bone order, so grot[parent] is ready.
                    grot[i] = grot[b.parent] * lq;
                    gpos[i] = gpos[b.parent] + grot[b.parent] * new Vector3(b.ox, b.oy, b.oz);
                }
            }
            return gpos;
        }

        /// <summary>Global joint positions AND rotations (Kimodo coords) for one frame.</summary>
        public static void GlobalPose(KimodoMotion motion, KimodoClip clip, int frame,
            out Vector3[] gpos, out Quaternion[] grot)
        {
            int J = motion.jointCount;
            gpos = new Vector3[J];
            grot = new Quaternion[J];
            int qBase = frame * J * 4;
            var root = new Vector3(
                clip.rootPositions[frame * 3 + 0],
                clip.rootPositions[frame * 3 + 1],
                clip.rootPositions[frame * 3 + 2]);

            for (int i = 0; i < J; i++)
            {
                var b = motion.bones[i];
                int qi = qBase + i * 4;
                var lq = new Quaternion(clip.localQuats[qi + 1], clip.localQuats[qi + 2],
                                        clip.localQuats[qi + 3], clip.localQuats[qi + 0]);
                if (b.parent < 0) { grot[i] = lq; gpos[i] = root; }
                else
                {
                    grot[i] = grot[b.parent] * lq;
                    gpos[i] = gpos[b.parent] + grot[b.parent] * new Vector3(b.ox, b.oy, b.oz);
                }
            }
        }

        /// <summary>Index of a bone by name, or -1.</summary>
        public static int BoneIndex(KimodoMotion motion, string name)
        {
            for (int i = 0; i < motion.bones.Count; i++)
                if (motion.bones[i].name == name) return i;
            return -1;
        }
    }
}
