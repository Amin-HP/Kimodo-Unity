// SPDX-License-Identifier: Apache-2.0
// Hand/foot effector targets. Each target is an explicit world position (+ optional rotation) that
// a chosen hand or foot must reach at a given frame — authored with a transform gizmo, like the
// demo's keyframes. Converted to Kimodo coordinates via KimodoRootMap and sent as a single-joint
// target constraint (the server pins that joint's global position/rotation at that frame).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace AminHP.KimodoBridge
{
    [AddComponentMenu("Kimodo/Kimodo Effectors")]
    [RequireComponent(typeof(KimodoGenerator))]
    public class KimodoEffectors : MonoBehaviour
    {
        public enum Kind { LeftFoot, RightFoot, LeftHand, RightHand }

        [Serializable]
        public class Target
        {
            public int frame;
            public Kind kind = Kind.LeftFoot;
            public Vector3 world;                       // where the hand/foot should be
            public Quaternion rot = Quaternion.identity; // desired orientation (used if constrainRotation)
            public bool constrainRotation = false;
        }

        [Tooltip("Hand/foot targets. Each is sent as a single-joint constraint at its frame.")]
        public List<Target> targets = new List<Target>();

        [Tooltip("Generator to feed constraints to. Defaults to the one on this GameObject.")]
        public KimodoGenerator generator;

        public KimodoGenerator ResolvedGenerator =>
            generator != null ? generator : (generator = GetComponent<KimodoGenerator>());

        public static string TypeString(Kind k) => k switch
        {
            Kind.LeftFoot => "left-foot",
            Kind.RightFoot => "right-foot",
            Kind.LeftHand => "left-hand",
            Kind.RightHand => "right-hand",
            _ => "left-foot",
        };

        public static HumanBodyBones Bone(Kind k) => k switch
        {
            Kind.LeftFoot => HumanBodyBones.LeftFoot,
            Kind.RightFoot => HumanBodyBones.RightFoot,
            Kind.LeftHand => HumanBodyBones.LeftHand,
            Kind.RightHand => HumanBodyBones.RightHand,
            _ => HumanBodyBones.LeftFoot,
        };

        private static string SomaBone(Kind k) => k switch
        {
            Kind.LeftFoot => "LeftFoot",
            Kind.RightFoot => "RightFoot",
            Kind.LeftHand => "LeftHand",
            Kind.RightHand => "RightHand",
            _ => "LeftFoot",
        };

        // IK chain (upper, mid, end) SOMA bone names for each effector.
        private static (string upper, string mid, string end) Chain(Kind k) => k switch
        {
            Kind.LeftFoot => ("LeftLeg", "LeftShin", "LeftFoot"),
            Kind.RightFoot => ("RightLeg", "RightShin", "RightFoot"),
            Kind.LeftHand => ("LeftArm", "LeftForeArm", "LeftHand"),
            Kind.RightHand => ("RightArm", "RightForeArm", "RightHand"),
            _ => ("LeftLeg", "LeftShin", "LeftFoot"),
        };

        // Fit the effector's vertical world<->Kimodo relationship over the motion: worldY = a*kimodoY + b.
        // The foot/hand HEIGHT is set by leg/arm length + retarget, NOT the root's vertical mapping, so
        // Y needs its own (measured) scale. Returns (a, b); (1,0) fallback if it can't measure.
        private static (float a, float b) FitVerticalY(KimodoGenerator g, KimodoMotion motion, KimodoClip clip, Kind kind)
        {
            int ji = KimodoFK.BoneIndex(motion, SomaBone(kind));
            var bone = g.ResolvedTarget != null ? g.ResolvedTarget.GetBoneTransform(Bone(kind)) : null;
            if (ji < 0 || bone == null) return (1f, 0f);

            int T = motion.frameCount;
            int step = Mathf.Max(1, T / 24);
            float sk = 0, sw = 0, skk = 0, skw = 0; int n = 0;
            float saved = g.PreviewTime;
            for (int f = 0; f < T; f += step)
            {
                g.Preview.SampleFrame(g.clipIndex, f);
                float wy = bone.position.y;
                float ky = KimodoFK.GlobalPositions(motion, clip, f)[ji].y;
                sk += ky; sw += wy; skk += ky * ky; skw += ky * wy; n++;
            }
            g.SampleTime(saved);
            float denom = n * skk - sk * sk;
            if (n < 2 || Mathf.Abs(denom) < 1e-9f) return (1f, 0f);
            float a = (n * skw - sk * sw) / denom;
            float b = (sw - a * sk) / n;
            if (Mathf.Abs(a) < 1e-6f) return (1f, 0f);
            return (a, b);
        }

        /// <summary>Build one single-joint target constraint per target, or null if none/unavailable.</summary>
        public List<KimodoConstraint> BuildConstraints()
        {
            var g = ResolvedGenerator;
            if (g == null || g.Motion == null || targets.Count == 0) return null;
            var m = KimodoRootMap.Compute(g);
            if (!m.valid) return null;
            var motion = g.Motion;
            var clip = motion.clips[Mathf.Clamp(g.clipIndex, 0, motion.clips.Count - 1)];
            int fc = motion.frameCount;

            // Measured vertical fit per effector kind (reused across targets of the same kind).
            var vfit = new Dictionary<Kind, (float a, float b)>();
            (float a, float b) VFit(Kind k)
            {
                if (!vfit.TryGetValue(k, out var r)) { r = FitVerticalY(g, motion, clip, k); vfit[k] = r; }
                return r;
            }

            int J = motion.jointCount;
            var result = new List<KimodoConstraint>();
            foreach (var t in targets)
            {
                if (t.frame < 0 || t.frame >= fc) continue;

                // Target in Kimodo coords: x,z via the root affine, y via the measured foot fit.
                Vector3 kh = KimodoRootMap.WorldToKimodo(t.world, m);
                var (a, bb) = VFit(t.kind);
                float ky = Mathf.Abs(a) > 1e-6f ? (t.world.y - bb) / a : kh.y;
                Vector3 kpos = new Vector3(kh.x, ky, kh.z);

                // Full-body IK on the current pose: bend the leg/arm to reach the target, stepping
                // the body (root) toward it when it's out of reach — then constrain that pose.
                KimodoFK.GlobalPose(motion, clip, t.frame, out var gpos, out var grot);
                var (upN, midN, enN) = Chain(t.kind);
                int iUp = KimodoFK.BoneIndex(motion, upN);
                int iMid = KimodoFK.BoneIndex(motion, midN);
                int iEnd = KimodoFK.BoneIndex(motion, enN);
                if (iUp < 0 || iMid < 0 || iEnd < 0) continue;
                int iParent = motion.bones[iUp].parent;

                Vector3 pUp = gpos[iUp], pMid = gpos[iMid], pEnd = gpos[iEnd];
                float reach = ((pMid - pUp).magnitude + (pEnd - pMid).magnitude) * 0.999f;

                // Step the whole body toward the target if the limb can't reach.
                Vector3 rootShift = Vector3.zero;
                float dist = (kpos - pUp).magnitude;
                if (dist > reach) rootShift = (kpos - pUp).normalized * (dist - reach);
                pUp += rootShift; pMid += rootShift; pEnd += rootShift;

                Quaternion parentGlobal = iParent >= 0 ? grot[iParent] : Quaternion.identity;
                var ik = KimodoIK.SolveTwoBone(pUp, pMid, pEnd, kpos, grot[iUp], grot[iMid], parentGlobal);

                // Modified full-body local rotations: start from the current pose, replace the two
                // IK'd joints (others stay natural, so the constrained foot chain is consistent).
                var localQuats = new float[J * 4];
                Array.Copy(clip.localQuats, t.frame * J * 4, localQuats, 0, J * 4);
                WriteQuat(localQuats, iUp, ik.localUpper);
                WriteQuat(localQuats, iMid, ik.localMid);

                Vector3 root = new Vector3(
                    clip.rootPositions[t.frame * 3], clip.rootPositions[t.frame * 3 + 1],
                    clip.rootPositions[t.frame * 3 + 2]) + rootShift;

                result.Add(new KimodoConstraint
                {
                    type = TypeString(t.kind),
                    frameIndices = new[] { t.frame },
                    localQuats = localQuats,                     // pose-based (server FKs it)
                    rootPositions = new[] { root.x, root.y, root.z },
                    jointNames = Array.Empty<string>(),
                });
            }
            return result.Count > 0 ? result : null;
        }

        private static void WriteQuat(float[] q, int joint, Quaternion v)
        {
            int i = joint * 4;
            q[i + 0] = v.w; q[i + 1] = v.x; q[i + 2] = v.y; q[i + 3] = v.z;
        }
    }
}
