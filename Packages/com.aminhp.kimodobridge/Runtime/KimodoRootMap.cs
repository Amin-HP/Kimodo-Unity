// SPDX-License-Identifier: Apache-2.0
// Affine world<->Kimodo mapping, measured from the current preview. The character's root is
// placed by a direct affine (world = worldHips0 + charRot·k·FlipX(kimodo - kimodoRoot0)), so this
// is cleanly invertible — used to convert authored world targets (waypoints, hand/foot targets)
// into Kimodo coordinates. Anchored at frame 0; scale from the max-travel frame.

using UnityEngine;

namespace AminHP.KimodoBridge
{
    public static class KimodoRootMap
    {
        public struct Map
        {
            public Vector3 worldHips0;
            public Vector3 kimodoRoot0;
            public Quaternion charRot;
            public float k;          // world units per Kimodo metre (~1)
            public bool valid;
        }

        private static Vector3 FlipX(Vector3 v) => new Vector3(-v.x, v.y, v.z);

        private static Vector3 RootAt(KimodoClip clip, int f) =>
            new Vector3(clip.rootPositions[f * 3], clip.rootPositions[f * 3 + 1], clip.rootPositions[f * 3 + 2]);

        public static Map Compute(KimodoGenerator g)
        {
            var m = new Map { charRot = Quaternion.identity, k = 1f, valid = false };
            if (g == null) return m;
            var tgt = g.ResolvedTarget;
            var hips = tgt != null ? tgt.GetBoneTransform(HumanBodyBones.Hips) : null;
            if (hips == null) return m;

            // No motion yet: estimate from the character's current (bind) pose so pose gizmos can be
            // authored before the first generate. Approximate scale/height until a real motion refines it.
            if (g.Motion == null || !g.IsPreviewBound)
            {
                m.charRot = tgt.transform.rotation;
                m.worldHips0 = hips.position;
                m.k = tgt.humanScale > 1e-3f ? tgt.humanScale : 1f;
                m.kimodoRoot0 = new Vector3(0f, hips.position.y / m.k, 0f);
                m.valid = true;
                return m;
            }

            var clip = g.Motion.clips[Mathf.Clamp(g.clipIndex, 0, g.Motion.clips.Count - 1)];
            m.charRot = tgt.transform.rotation;
            m.kimodoRoot0 = RootAt(clip, 0);

            float saved = g.PreviewTime;
            g.Preview.SampleFrame(g.clipIndex, 0);
            m.worldHips0 = hips.position;

            int fN = 0; float best = 0f;
            for (int f = 1; f < g.Motion.frameCount; f++)
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

        /// <summary>World position -> Kimodo global position (metres).</summary>
        public static Vector3 WorldToKimodo(Vector3 world, in Map m) =>
            m.kimodoRoot0 + FlipX(Quaternion.Inverse(m.charRot) * (world - m.worldHips0)) / m.k;

        /// <summary>Kimodo global position -> world (for drawing).</summary>
        public static Vector3 KimodoToWorld(Vector3 kimodo, in Map m) =>
            m.worldHips0 + m.charRot * (FlipX(kimodo - m.kimodoRoot0) * m.k);

        /// <summary>World rotation -> Kimodo global rotation as [w, x, y, z].</summary>
        public static float[] WorldToKimodoQuatWXYZ(Quaternion world, in Map m)
        {
            Quaternion loc = Quaternion.Inverse(m.charRot) * world;  // Unity quat (x,y,z,w)
            // KimodoCoords.Quat maps kimodo(w,x,y,z)->Unity(x,-y,-z,w); invert it here.
            return new[] { loc.w, loc.x, -loc.y, -loc.z };
        }
    }
}
