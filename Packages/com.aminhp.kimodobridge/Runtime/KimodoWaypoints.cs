// SPDX-License-Identifier: Apache-2.0
// Root-path (waypoint) constraints. The user places waypoints on the ground where the
// character's pelvis should pass at given frames; these become a Kimodo `root2d` constraint.
//
// Unlike hand/foot pins, the root is placed by a direct affine retarget
// (worldRoot ≈ anchor + charRot · scale · FlipX(kimodoRoot)), so world<->Kimodo is cleanly
// invertible here. That also makes waypoints a good check that the Unity↔model coordinate
// mapping is correct.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace AminHP.KimodoBridge
{
    [AddComponentMenu("Kimodo/Kimodo Waypoints")]
    [RequireComponent(typeof(KimodoGenerator))]
    public class KimodoWaypoints : MonoBehaviour
    {
        [Serializable]
        public class Waypoint
        {
            public int frame;
            public Vector3 world;          // ground position the pelvis should reach (y ignored)
            public bool constrainFacing;   // if true, also force the facing direction below
            public float headingDeg;       // world yaw the character should face (0 = +Z), used when constrainFacing
        }

        [Tooltip("Waypoints the pelvis must pass through, sent as a root2d constraint on Generate.")]
        public List<Waypoint> waypoints = new List<Waypoint>();

        [Header("Path display (editor only)")]
        [Tooltip("World Y the route + markers are drawn at (put it on the ground). Use 'Snap to feet' in the inspector to match the character.")]
        public float groundY = 0f;

        [Tooltip("Radius of each waypoint's ground disc (metres), for the demo-style marker.")]
        public float markerRadius = 0.25f;

        [Tooltip("Generator to feed constraints to. Defaults to the one on this GameObject.")]
        public KimodoGenerator generator;

        public KimodoGenerator ResolvedGenerator =>
            generator != null ? generator : (generator = GetComponent<KimodoGenerator>());

        // Unity(world) <-> Kimodo vector conversion is a single-axis flip (negate X); self-inverse.
        private static Vector3 FlipX(Vector3 v) => new Vector3(-v.x, v.y, v.z);

        private static Vector3 RootAt(KimodoClip clip, int f) =>
            new Vector3(clip.rootPositions[f * 3], clip.rootPositions[f * 3 + 1], clip.rootPositions[f * 3 + 2]);

        /// <summary>Affine mapping between the Kimodo root and the target's world root, measured from
        /// the current preview. Anchored at frame 0; scale from the max-travel frame.</summary>
        public struct Mapping
        {
            public Vector3 worldHips0;   // target hips world pos at frame 0
            public Vector3 kimodoRoot0;  // clip root (Kimodo) at frame 0
            public Quaternion charRot;   // target root rotation
            public float k;              // world units per Kimodo metre (~1)
            public bool valid;
        }

        public Mapping ComputeMapping()
        {
            var m = new Mapping { charRot = Quaternion.identity, k = 1f, valid = false };
            var g = ResolvedGenerator;
            if (g == null) return m;
            var tgt = g.ResolvedTarget;
            var hips = tgt != null ? tgt.GetBoneTransform(HumanBodyBones.Hips) : null;
            if (hips == null) return m;

            // No motion yet: estimate the mapping from the character's current (bind) pose. XZ is exact
            // relative to the character; scale/height are approximate until a real motion refines them.
            if (g.Motion == null || !g.IsPreviewBound)
            {
                m.charRot = tgt.transform.rotation;
                m.worldHips0 = hips.position;
                m.k = tgt.humanScale > 1e-3f ? tgt.humanScale : 1f;
                m.kimodoRoot0 = new Vector3(0f, hips.position.y / m.k, 0f);
                m.valid = true;
                return m;
            }

            var motion = g.Motion;
            var clip = motion.clips[Mathf.Clamp(g.clipIndex, 0, motion.clips.Count - 1)];
            m.charRot = tgt.transform.rotation;
            m.kimodoRoot0 = RootAt(clip, 0);

            float saved = g.PreviewTime;
            g.Preview.SampleFrame(g.clipIndex, 0);
            m.worldHips0 = hips.position;

            // Max horizontal travel frame -> scale.
            int fN = 0; float best = 0f;
            for (int f = 1; f < motion.frameCount; f++)
            {
                var d = RootAt(clip, f) - m.kimodoRoot0; d.y = 0f;
                if (d.sqrMagnitude > best) { best = d.sqrMagnitude; fN = f; }
            }
            if (best > 1e-6f)
            {
                g.Preview.SampleFrame(g.clipIndex, fN);
                Vector3 wN = hips.position;
                Vector3 kd = RootAt(clip, fN) - m.kimodoRoot0; kd.y = 0f;
                Vector3 wd = wN - m.worldHips0; wd.y = 0f;
                if (kd.magnitude > 1e-5f) m.k = wd.magnitude / kd.magnitude;
            }
            if (m.k <= 1e-5f) m.k = 1f;
            g.SampleTime(saved);
            m.valid = true;
            return m;
        }

        /// <summary>World ground position -> Kimodo root (x, z).</summary>
        public static Vector2 WorldToKimodoXZ(Vector3 world, in Mapping m)
        {
            Vector3 local = Quaternion.Inverse(m.charRot) * (world - m.worldHips0);
            Vector3 dK = FlipX(local) / m.k;
            Vector3 kroot = m.kimodoRoot0 + dK;
            return new Vector2(kroot.x, kroot.z);
        }

        /// <summary>Kimodo root (x, z) -> world ground position (for drawing the path / new waypoints).</summary>
        public static Vector3 KimodoXZToWorld(float kx, float kz, in Mapping m)
        {
            Vector3 dK = new Vector3(kx - m.kimodoRoot0.x, 0f, kz - m.kimodoRoot0.z);
            return m.worldHips0 + m.charRot * (FlipX(dK) * m.k);
        }

        /// <summary>Project a world position onto the display ground plane.</summary>
        public Vector3 OnGround(Vector3 world) => new Vector3(world.x, groundY, world.z);

        /// <summary>Estimate ground Y from the character's lowest foot bone at frame 0 (or NaN if unavailable).</summary>
        public float EstimateGroundY()
        {
            var g = ResolvedGenerator;
            if (g == null || g.Motion == null || !g.IsPreviewBound) return float.NaN;
            var tgt = g.ResolvedTarget;
            if (tgt == null) return float.NaN;
            float saved = g.PreviewTime;
            g.Preview.SampleFrame(g.clipIndex, 0);
            float y = float.PositiveInfinity;
            foreach (var hb in new[] { HumanBodyBones.LeftToes, HumanBodyBones.RightToes, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot })
            {
                var b = tgt.GetBoneTransform(hb);
                if (b != null) y = Mathf.Min(y, b.position.y);
            }
            g.SampleTime(saved);
            return float.IsInfinity(y) ? float.NaN : y;
        }

        /// <summary>World yaw (0=+Z) -> Kimodo heading [cos, sin] for a global_root_heading constraint.</summary>
        public static Vector2 HeadingCosSin(float headingDeg, in Mapping m)
        {
            Vector3 dirWorld = Quaternion.Euler(0f, headingDeg, 0f) * Vector3.forward;
            Vector3 dK = FlipX(Quaternion.Inverse(m.charRot) * dirWorld);
            dK.y = 0f;
            dK = dK.sqrMagnitude < 1e-8f ? Vector3.forward : dK.normalized;
            float a = Mathf.Atan2(dK.x, dK.z);   // Kimodo heading angle (0 = facing +Z)
            return new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        }

        /// <summary>Build the root2d constraint(s): one position-only, one position+facing. Null if none.</summary>
        public List<KimodoConstraint> BuildRootConstraints()
        {
            var g = ResolvedGenerator;
            if (g == null || waypoints.Count == 0) return null;
            var m = ComputeMapping();          // estimated from the bind pose before the first generate
            if (!m.valid) return null;
            int fc = g.AuthoringFrameCount;     // real frame count, or estimate from duration pre-generation

            var posF = new List<int>(); var posP = new List<float>();
            var facF = new List<int>(); var facP = new List<float>(); var facH = new List<float>();
            foreach (var wp in waypoints)
            {
                if (wp.frame < 0 || wp.frame >= fc) continue;
                var xz = WorldToKimodoXZ(wp.world, m);
                if (wp.constrainFacing)
                {
                    facF.Add(wp.frame); facP.Add(xz.x); facP.Add(xz.y);
                    var cs = HeadingCosSin(wp.headingDeg, m);
                    facH.Add(cs.x); facH.Add(cs.y);
                }
                else
                {
                    posF.Add(wp.frame); posP.Add(xz.x); posP.Add(xz.y);
                }
            }

            var result = new List<KimodoConstraint>();
            if (posF.Count > 0) result.Add(MakeRoot2d(posF, posP, null));
            if (facF.Count > 0) result.Add(MakeRoot2d(facF, facP, facH));
            return result.Count > 0 ? result : null;
        }

        private static KimodoConstraint MakeRoot2d(List<int> frames, List<float> path, List<float> heading) =>
            new KimodoConstraint
            {
                type = "root2d",
                frameIndices = frames.ToArray(),
                rootPath2d = path.ToArray(),
                rootHeading2d = heading != null ? heading.ToArray() : null,
                localQuats = Array.Empty<float>(),
                rootPositions = Array.Empty<float>(),
                jointNames = Array.Empty<string>(),
            };
    }
}
