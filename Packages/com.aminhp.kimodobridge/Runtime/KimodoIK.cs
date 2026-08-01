// SPDX-License-Identifier: Apache-2.0
// Analytic two-bone IK (leg/arm) in Kimodo space. Given the upper/mid/end joint positions and a
// target for the end, it bends the mid joint (knee/elbow) so the end reaches the target, and
// returns new LOCAL rotations for the upper and mid joints. The end position is clamped to the
// chain's reach; the caller can shift the root to cover any remaining deficit.

using UnityEngine;

namespace AminHP.KimodoBridge
{
    public static class KimodoIK
    {
        public struct Result
        {
            public Quaternion localUpper;   // new local rotation for the upper joint (hip/shoulder-side)
            public Quaternion localMid;     // new local rotation for the mid joint (knee/elbow)
            public Vector3 endReached;      // where the end actually lands (clamped to reach)
        }

        /// <param name="pUp">upper joint global pos (thigh top / upper arm)</param>
        /// <param name="pMid">mid joint global pos (knee / elbow)</param>
        /// <param name="pEnd">end joint global pos (ankle / wrist)</param>
        /// <param name="gUp">upper joint global rotation</param>
        /// <param name="gMid">mid joint global rotation</param>
        /// <param name="parentGlobalUp">global rotation of the upper joint's parent</param>
        public static Result SolveTwoBone(
            Vector3 pUp, Vector3 pMid, Vector3 pEnd, Vector3 target,
            Quaternion gUp, Quaternion gMid, Quaternion parentGlobalUp)
        {
            float lUM = (pMid - pUp).magnitude;   // upper bone length
            float lME = (pEnd - pMid).magnitude;  // lower bone length

            // Work from unit rotations and hand back unit rotations. Anything else compounds into a
            // stretching skeleton once the caller re-solves off its own output — see KimodoFK.Unit.
            gUp = KimodoFK.Unit(gUp);
            gMid = KimodoFK.Unit(gMid);

            // A zero-length bone has no direction to aim, and FromToRotation on a zero vector returns
            // nonsense — leave the chain as it is rather than write that into the pose.
            if (lUM < 1e-6f || lME < 1e-6f)
                return new Result
                {
                    localUpper = KimodoFK.Inv(parentGlobalUp) * gUp,
                    localMid = KimodoFK.Inv(gUp) * gMid,
                    endReached = pEnd,
                };

            Vector3 toT = target - pUp;
            float c0 = toT.magnitude;
            float maxReach = (lUM + lME) * 0.999f;
            float minReach = Mathf.Abs(lUM - lME) + 1e-4f;
            float c = Mathf.Clamp(c0, minReach, maxReach);
            // Fully folded (end sitting on top of the upper joint) leaves no axis to keep, so fall
            // back to the upper bone's own direction, which a real bone always has.
            Vector3 axis = c0 > 1e-6f ? toT.normalized
                         : ((pEnd - pUp).sqrMagnitude > 1e-8f ? (pEnd - pUp).normalized
                                                             : (pMid - pUp).normalized);

            // Knee/elbow bend direction (pole): the current bend, flattened onto the plane
            // perpendicular to the NEW axis. It must be perpendicular to THAT axis, not to the old
            // upper->end one: h and r below are the two legs of a right triangle on the new axis, so a
            // bendDir that still leans along it makes |pMidNew - pUp| come out longer than the upper
            // bone. FK then keeps the real bone length and the aim lands the hand short of the target,
            // so the next mouse-move solved from an already-wrong pose — the limb bending off in its
            // own direction instead of following the handle.
            Vector3 upToMid = pMid - pUp;
            Vector3 bendDir = upToMid - Vector3.Dot(upToMid, axis) * axis;
            if (bendDir.sqrMagnitude < 1e-8f)
            {
                bendDir = Vector3.Cross(axis, Vector3.up);
                if (bendDir.sqrMagnitude < 1e-8f) bendDir = Vector3.Cross(axis, Vector3.right);
            }
            bendDir = bendDir.normalized;

            float h = (lUM * lUM - lME * lME + c * c) / (2f * c);
            float r = Mathf.Sqrt(Mathf.Max(0f, lUM * lUM - h * h));
            Vector3 pMidNew = pUp + axis * h + bendDir * r;
            Vector3 pEndNew = pUp + axis * c;

            // Aim rotations (world, minimal-twist) applied to the current global rotations.
            Quaternion dUp = Quaternion.FromToRotation((pMid - pUp).normalized, (pMidNew - pUp).normalized);
            Quaternion gUpNew = KimodoFK.Unit(dUp * gUp);
            Quaternion dMid = Quaternion.FromToRotation((pEnd - pMid).normalized, (pEndNew - pMidNew).normalized);
            Quaternion gMidNew = KimodoFK.Unit(dMid * gMid);

            return new Result
            {
                localUpper = KimodoFK.Unit(KimodoFK.Inv(parentGlobalUp) * gUpNew),
                localMid = KimodoFK.Unit(KimodoFK.Inv(gUpNew) * gMidNew),
                endReached = pEndNew,
            };
        }
    }
}
