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
        /// <summary>A unit-length copy of <paramref name="q"/> (identity if it is degenerate or
        /// non-finite).
        ///
        /// THIS IS NOT COSMETIC. FK places a joint with <c>grot[parent] * offset</c>, and Unity's
        /// quaternion*vector operator scales the vector by |q|² — so a rotation that drifts off the
        /// unit sphere does not merely rotate the skeleton, it STRETCHES it. The drift compounds:
        /// Unity's Quaternion.Inverse is a bare conjugate (correct only for a unit quaternion), so
        /// every IK write multiplied the error instead of cancelling it, and each mouse-move event of
        /// a drag ran another solve. That is why a dragged arm bent the wrong way and grew longer and
        /// longer, and why its handle marched off into the distance and drew ever bigger. Every place
        /// that composes or stores a rotation in this pipeline goes through here.</summary>
        public static Quaternion Unit(Quaternion q)
        {
            float m2 = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (!(m2 > 1e-12f) || float.IsInfinity(m2)) return Quaternion.identity;   // also catches NaN
            float inv = 1f / Mathf.Sqrt(m2);
            return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
        }

        /// <summary>Inverse of a rotation, normalised first. Prefer this over Quaternion.Inverse,
        /// which is the bare conjugate — see <see cref="Unit"/>.</summary>
        public static Quaternion Inv(Quaternion q)
        {
            var u = Unit(q);
            return new Quaternion(-u.x, -u.y, -u.z, u.w);
        }

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
                var lq = Unit(new Quaternion(clip.localQuats[qi + 1], clip.localQuats[qi + 2],
                                             clip.localQuats[qi + 3], clip.localQuats[qi + 0]));
                if (b.parent < 0)
                {
                    grot[i] = lq;
                    gpos[i] = root;
                }
                else
                {
                    // Parents precede children in SOMA bone order, so grot[parent] is ready.
                    grot[i] = Unit(grot[b.parent] * lq);
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
                var lq = Unit(new Quaternion(clip.localQuats[qi + 1], clip.localQuats[qi + 2],
                                             clip.localQuats[qi + 3], clip.localQuats[qi + 0]));
                if (b.parent < 0) { grot[i] = lq; gpos[i] = root; }
                else
                {
                    grot[i] = Unit(grot[b.parent] * lq);
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
