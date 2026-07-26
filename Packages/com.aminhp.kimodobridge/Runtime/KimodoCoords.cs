// SPDX-License-Identifier: Apache-2.0
// Coordinate-system conversion: Kimodo (right-handed, Y-up, +Z forward, metres)
// -> Unity (left-handed, Y-up, +Z forward).
//
// In Kimodo's right-handed frame the character's RIGHT is -X; in Unity's
// left-handed frame the character's RIGHT is +X. The change of basis that
// aligns them is S = diag(-1, 1, 1) (negate X). This is a reflection that
// exactly cancels the handedness difference, so left/right are preserved
// (it does NOT mirror the motion).
//
// Under S:
//   position  p = (x,y,z)            -> (-x, y, z)
//   rotation  R                      -> S * R * S   (S == S^-1)
//   quaternion(w,x,y,z)              -> (w, x, -y, -z)
// The same transform applies to LOCAL offsets/rotations because both a bone
// frame and its parent frame undergo S.

using UnityEngine;

namespace AminHP.KimodoBridge
{
    public static class KimodoCoords
    {
        /// <summary>Convert a Kimodo position (metres) to a Unity position.</summary>
        public static Vector3 Position(float x, float y, float z)
        {
            return new Vector3(-x, y, z);
        }

        public static Vector3 Position(float[] flat, int i3)
        {
            return new Vector3(-flat[i3], flat[i3 + 1], flat[i3 + 2]);
        }

        /// <summary>
        /// Convert a Kimodo quaternion given as (w, x, y, z) to a Unity Quaternion.
        /// Unity's constructor order is (x, y, z, w).
        /// </summary>
        public static Quaternion Quat(float w, float x, float y, float z)
        {
            // (w, x, y, z) -> (w, x, -y, -z)  =>  Unity (x, -y, -z, w)
            return new Quaternion(x, -y, -z, w);
        }

        public static Quaternion Quat(float[] flat, int i4)
        {
            return Quat(flat[i4], flat[i4 + 1], flat[i4 + 2], flat[i4 + 3]);
        }
    }
}
