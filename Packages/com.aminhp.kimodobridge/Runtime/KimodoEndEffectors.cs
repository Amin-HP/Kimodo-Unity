// SPDX-License-Identifier: Apache-2.0
// Hand / foot (end-effector) keyframes, authored the way Kimodo's own demo authors them.
//
// WHY THIS IS A POSE AND NOT A POINT: in Kimodo an end-effector constraint is not "put this point
// here". Look at kimodo/constraints.py — EndEffectorConstraintSet takes a FULL-BODY pose and pins,
// from it, (a) the effector chain's joint positions (wrist + hand end, or heel + toe), (b) the
// effector's global rotation, and (c) the root: smooth_root_2d, root height and facing. The demo
// therefore never sends a bare target: it captures the whole pose of the frame you are editing and
// records which limbs to enforce (kimodo/viz/constraint_ui.py: EEJointsKeyframeSet stores
// joints_pos [J,3] + joints_rot [J,3,3] + joint_names).
//
// So each key here owns a pose (seeded from the motion at its frame, then edited in the Scene view)
// plus a set of active limbs. Dragging a hand/foot handle solves IK *on that pose*, which means the
// handle always sits exactly where the constraint will be — no per-limb height guessing, no hidden
// root shifting. Both were in the previous target-based version and were why it missed.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace AminHP.KimodoBridge
{
    [AddComponentMenu("Kimodo/Kimodo End-Effectors")]
    [RequireComponent(typeof(KimodoGenerator))]
    public class KimodoEndEffectors : MonoBehaviour, ISerializationCallbackReceiver
    {
        /// <summary>Legacy single-limb selector, kept so pre-pose scenes and takes still deserialize.</summary>
        public enum Kind { LeftFoot, RightFoot, LeftHand, RightHand }

        /// <summary>Which limbs a key pins. Several may be active at one frame (they then share the
        /// key's pose, exactly like the demo, which regroups one keyframe into per-limb constraints).</summary>
        [Flags]
        public enum Limb
        {
            None = 0,
            LeftFoot = 1 << 0,
            RightFoot = 1 << 1,
            LeftHand = 1 << 2,
            RightHand = 1 << 3,
        }

        public static readonly Limb[] AllLimbs = { Limb.LeftFoot, Limb.RightFoot, Limb.LeftHand, Limb.RightHand };

        [Serializable]
        public class Target
        {
            public int frame;
            public Limb limbs = Limb.None;   // None => migrate from the legacy `kind`

            // The pose this key constrains (same layout as a KimodoPoseConstraints key).
            public float[] localQuats;       // Kimodo local rotations, J*4 (wxyz)
            public Vector3 root;             // Kimodo pelvis position
            public bool hasPose;
            public bool show = true;         // draw this key's ghost in the Scene

            [Tooltip("Let the body rise/turn to reach: the server then pins only the limb (and the " +
                     "ground position), leaving root height + facing free. Off = demo behaviour, where " +
                     "the pose's root height and facing are pinned too.")]
            public bool freeRoot;

            // ---- legacy fields (target-based version). Kept so old data migrates instead of resetting:
            // the first time a key is used, its pose is seeded and the limb is IK'd onto `world`.
            public Kind kind = Kind.LeftFoot;
            public Vector3 world;
            public Quaternion rot = Quaternion.identity;
            public bool constrainRotation;
        }

        [Tooltip("Hand/foot keyframes. Each holds a pose; only the active limbs (and the root) are pinned.")]
        public List<Target> targets = new List<Target>();

        [Tooltip("Generator to feed constraints to. Defaults to the one on this GameObject.")]
        public KimodoGenerator generator;

        [Header("Scene view")]
        [Tooltip("Fade the ghost mesh down to the part of the body the pinned limbs belong to — an arm " +
                 "for a hand, the lower body for a foot — so the torso and head stay out of the way. " +
                 "The skeleton is always drawn in full.")]
        public bool ghostOnlyPinnedLimbs = true;

        [Tooltip("Show only the key at the preview frame (the demo's 'show only current constraint').")]
        public bool showOnlyCurrentFrame;

        [Tooltip("Also show the character's mesh at each shown key's pose, as a transparent ghost. " +
                 "On by default — seeing the hand on an actual arm is what makes a key readable; " +
                 "the skeleton alone is not.")]
        public bool showGhostMesh = true;

        [Range(0f, 1f)] public float ghostOpacity = 0.2f;

        // Bumped whenever a default above changes, so components ALREADY saved in a scene pick the new
        // default up instead of keeping the old serialized value forever. See OnAfterDeserialize.
        private const int CurrentSettingsVersion = 1;
        [SerializeField, HideInInspector] private int settingsVersion;

        [Tooltip("When you drag a hand/foot, keep its orientation instead of letting it swing with the " +
                 "limb — e.g. a foot stays flat on the ground. Its rotation is part of the constraint.")]
        public bool keepEffectorOrientation = true;

        public KimodoGenerator ResolvedGenerator =>
            generator != null ? generator : (generator = GetComponent<KimodoGenerator>());

        public void OnBeforeSerialize() { }

        /// <summary>Carry a component saved before <see cref="CurrentSettingsVersion"/> up to the
        /// current defaults. A plain field initialiser only reaches components created from now on —
        /// one already in a scene deserializes whatever it was saved with, so the ghost stayed off for
        /// everyone who had ever opened this component.</summary>
        public void OnAfterDeserialize()
        {
            if (settingsVersion >= CurrentSettingsVersion) return;
            if (settingsVersion < 1) showGhostMesh = true;   // ghost mesh went from opt-in to on
            settingsVersion = CurrentSettingsVersion;
        }

        // ---------------------------------------------------------------------------------------
        // Limb metadata. Names are SOMA bone names; the joints Kimodo actually constrains per limb
        // come from the skeleton's *_joint_names lists (somaskel30: hand = wrist + hand end,
        // foot = heel + toe base) — see kimodo/skeleton/definitions.py and expand_joint_names().
        // ---------------------------------------------------------------------------------------
        public static Limb LimbFromKind(Kind k) => k switch
        {
            Kind.LeftFoot => Limb.LeftFoot,
            Kind.RightFoot => Limb.RightFoot,
            Kind.LeftHand => Limb.LeftHand,
            Kind.RightHand => Limb.RightHand,
            _ => Limb.LeftFoot,
        };

        /// <summary>The limbs a key pins, migrating pre-flags keys from their old single `kind`.</summary>
        public static Limb LimbsOf(Target t) => t.limbs != Limb.None ? t.limbs : LimbFromKind(t.kind);

        /// <summary>First active limb (what a single handle acts on when only one is expected).</summary>
        public static Limb PrimaryLimb(Target t)
        {
            var l = LimbsOf(t);
            foreach (var one in AllLimbs) if ((l & one) != 0) return one;
            return Limb.LeftFoot;
        }

        /// <summary>Kimodo constraint type name for a limb ("left-hand", …).</summary>
        public static string TypeString(Limb l) => l switch
        {
            Limb.LeftFoot => "left-foot",
            Limb.RightFoot => "right-foot",
            Limb.LeftHand => "left-hand",
            Limb.RightHand => "right-hand",
            _ => "left-foot",
        };

        public static string ShortName(Limb l) => l switch
        {
            Limb.LeftFoot => "LF",
            Limb.RightFoot => "RF",
            Limb.LeftHand => "LH",
            Limb.RightHand => "RH",
            _ => "?",
        };

        /// <summary>The effector joint itself — the one whose position AND rotation Kimodo pins.</summary>
        public static string BaseBone(Limb l) => l switch
        {
            Limb.LeftFoot => "LeftFoot",
            Limb.RightFoot => "RightFoot",
            Limb.LeftHand => "LeftHand",
            Limb.RightHand => "RightHand",
            _ => "LeftFoot",
        };

        public static HumanBodyBones HumanBone(Limb l) => l switch
        {
            Limb.LeftFoot => HumanBodyBones.LeftFoot,
            Limb.RightFoot => HumanBodyBones.RightFoot,
            Limb.LeftHand => HumanBodyBones.LeftHand,
            Limb.RightHand => HumanBodyBones.RightHand,
            _ => HumanBodyBones.LeftFoot,
        };

        /// <summary>Chain whose POSITIONS the model pins for this limb, base joint first. Mirrors the
        /// skeleton's left/right_hand/foot_joint_names, so the Scene highlight shows exactly what is
        /// constrained (ToeEnd only exists on somaskel77 and is skipped when absent).</summary>
        public static string[] ConstrainedChain(Limb l) => l switch
        {
            Limb.LeftFoot => new[] { "LeftFoot", "LeftToeBase", "LeftToeEnd" },
            Limb.RightFoot => new[] { "RightFoot", "RightToeBase", "RightToeEnd" },
            Limb.LeftHand => new[] { "LeftHand", "LeftHandMiddleEnd" },
            Limb.RightHand => new[] { "RightHand", "RightHandMiddleEnd" },
            _ => Array.Empty<string>(),
        };

        /// <summary>The body part this limb belongs to, used to fade the ghost MESH down to what you
        /// are actually working on: a hand shows its arm (shoulder to fingers), a foot shows the lower
        /// body on that side. The torso and head are left out — they are not what the key is about.
        /// The ghost fade is skin-weighted, so the cut is smooth rather than a hard edge.</summary>
        public static string[] GhostChain(Limb l) => l switch
        {
            Limb.LeftFoot => new[] { "Hips", "LeftLeg", "LeftShin", "LeftFoot", "LeftToeBase", "LeftToeEnd" },
            Limb.RightFoot => new[] { "Hips", "RightLeg", "RightShin", "RightFoot", "RightToeBase", "RightToeEnd" },
            Limb.LeftHand => new[] { "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand", "LeftHandMiddleEnd", "LeftHandThumbEnd" },
            Limb.RightHand => new[] { "RightShoulder", "RightArm", "RightForeArm", "RightHand", "RightHandMiddleEnd", "RightHandThumbEnd" },
            _ => Array.Empty<string>(),
        };

        /// <summary>Per-joint visibility mask for a key's ghost mesh (null = show the whole body).
        /// The effector's whole SUBTREE is included — fingers for a hand, toes for a foot. Listing the
        /// chain alone is not enough: every SOMA finger joint maps to a HumanBodyBones finger, so any
        /// joint left out of the mask is explicitly switched OFF and the hand ghosts without fingers.</summary>
        public bool[] GhostMask(Target t, KimodoMotion sk)
        {
            if (!ghostOnlyPinnedLimbs || sk == null || sk.bones == null) return null;
            var limbs = LimbsOf(t);
            if (limbs == Limb.None) return null;
            var mask = new bool[sk.jointCount];
            foreach (var l in AllLimbs)
            {
                if ((limbs & l) == 0) continue;
                foreach (var name in GhostChain(l))
                {
                    int j = KimodoFK.BoneIndex(sk, name);
                    if (j >= 0 && j < mask.Length) mask[j] = true;
                }
                MarkSubtree(mask, sk, KimodoFK.BoneIndex(sk, BaseBone(l)));
            }
            return mask;
        }

        // Mark a joint and everything hanging off it. Only ever called with an EFFECTOR as the root —
        // rooting it at the pelvis would mark the entire body.
        private static void MarkSubtree(bool[] mask, KimodoMotion sk, int root)
        {
            if (root < 0) return;
            for (int j = 0; j < sk.bones.Count && j < mask.Length; j++)
            {
                int t = j, guard = 0;
                while (t >= 0 && guard++ < 200)
                {
                    if (t == root) { mask[j] = true; break; }
                    t = sk.bones[t].parent;
                }
            }
        }

        /// <summary>Two-bone IK chain (upper, mid, end) used when you drag the effector.</summary>
        public static (string upper, string mid, string end) IKChain(Limb l) => l switch
        {
            Limb.LeftFoot => ("LeftLeg", "LeftShin", "LeftFoot"),
            Limb.RightFoot => ("RightLeg", "RightShin", "RightFoot"),
            Limb.LeftHand => ("LeftArm", "LeftForeArm", "LeftHand"),
            Limb.RightHand => ("RightArm", "RightForeArm", "RightHand"),
            _ => ("LeftLeg", "LeftShin", "LeftFoot"),
        };

        // ---------------------------------------------------------------------------------------
        // Constraint building
        // ---------------------------------------------------------------------------------------

        /// <summary>One constraint per active limb per key, all sharing that key's pose — the same
        /// shape the demo sends (kimodo/demo/generation.py regroups a keyframe's limbs into one
        /// constraint object per limb type, each carrying the whole pose).</summary>
        public List<KimodoConstraint> BuildConstraints()
        {
            var g = ResolvedGenerator;
            var sk = g != null ? g.PoseSkeleton : null;   // real motion, or the rest skeleton pre-generation
            if (sk == null || sk.bones == null || sk.bones.Count == 0 || targets.Count == 0) return null;

            int fc = g.AuthoringFrameCount;
            var result = new List<KimodoConstraint>();
            foreach (var t in targets)
            {
                if (t.frame < 0 || t.frame >= fc) continue;
                if (!EnsurePose(t)) continue;

                var limbs = LimbsOf(t);
                foreach (var limb in AllLimbs)
                {
                    if ((limbs & limb) == 0) continue;
                    result.Add(new KimodoConstraint
                    {
                        type = TypeString(limb),
                        frameIndices = new[] { t.frame },
                        localQuats = t.localQuats,
                        rootPositions = new[] { t.root.x, t.root.y, t.root.z },
                        jointNames = Array.Empty<string>(),
                        freeRoot = t.freeRoot,
                    });
                }
            }
            return result.Count > 0 ? result : null;
        }

        // ---------------------------------------------------------------------------------------
        // Pose authoring
        // ---------------------------------------------------------------------------------------

        /// <summary>Index of the key at <paramref name="frame"/>, or -1.</summary>
        public int IndexOfKeyAt(int frame)
        {
            for (int i = 0; i < targets.Count; i++) if (targets[i].frame == frame) return i;
            return -1;
        }

        /// <summary>Add <paramref name="limb"/> at <paramref name="frame"/>, MERGING into the key already
        /// there instead of making a second one. Two keys on one frame would each pin the root from their
        /// own pose — contradictory constraints, and the reason a second hand used to wreck the result.
        /// The demo merges the same way (EEJointsKeyframeSet.add_keyframe unions the joint_names).
        /// Returns the key's index. Caller owns Undo/dirty.</summary>
        public int AddOrMergeKey(int frame, Limb limb)
        {
            int at = IndexOfKeyAt(frame);
            if (at >= 0)
            {
                targets[at].limbs = LimbsOf(targets[at]) | limb;
                return at;
            }
            var t = new Target { frame = frame, limbs = limb, show = true };
            if (!AlignKeyToMotion(t)) SetIdlePose(t);
            targets.Add(t);
            return targets.Count - 1;
        }

        /// <summary>A one-frame clip view over a key's pose, so KimodoFK can evaluate it.</summary>
        public static KimodoClip PoseClip(Target t) => new KimodoClip
        {
            localQuats = t.localQuats,
            rootPositions = new[] { t.root.x, t.root.y, t.root.z },
        };

        /// <summary>Make sure the key has a usable pose, seeding it from the motion (or a rest pose)
        /// the first time. A legacy target-based key also gets its limb IK'd onto its old world
        /// position, so upgrading a scene keeps what was authored.</summary>
        public bool EnsurePose(Target t)
        {
            var g = ResolvedGenerator;
            var sk = g != null ? g.PoseSkeleton : null;
            if (t == null || sk == null) return false;
            int need = sk.jointCount * 4;
            if (t.hasPose && t.localQuats != null && t.localQuats.Length == need)
            { NormalizePose(t.localQuats); return true; }

            if (!AlignKeyToMotion(t)) SetIdlePose(t);
            if (!t.hasPose || t.localQuats == null || t.localQuats.Length != need) return false;
            NormalizePose(t.localQuats);

            // Migrate a legacy world target: bend the limb onto the point the user had placed.
            if (t.world != Vector3.zero)
            {
                var m = KimodoRootMap.Compute(g);
                if (m.valid) SolveLimbTo(t, LimbFromKind(t.kind), KimodoRootMap.WorldToKimodo(t.world, m));
                t.world = Vector3.zero;   // consumed; the pose is the source of truth from now on
            }
            return true;
        }

        /// <summary>Seed a key from the generated motion's own pose at its frame ("align to frame").
        /// False when there is no motion to read (caller can fall back to <see cref="SetIdlePose"/>).</summary>
        public bool AlignKeyToMotion(Target t)
        {
            var g = ResolvedGenerator;
            var m = g != null ? g.Motion : null;
            if (t == null || m == null || m.clips == null || m.clips.Count == 0) return false;
            var clip = m.clips[Mathf.Clamp(g.clipIndex, 0, m.clips.Count - 1)];
            int J = m.jointCount;
            int f = Mathf.Clamp(t.frame, 0, m.frameCount - 1);
            if (clip.localQuats == null || clip.localQuats.Length < (f + 1) * J * 4) return false;
            t.localQuats = new float[J * 4];
            Array.Copy(clip.localQuats, f * J * 4, t.localQuats, 0, J * 4);
            t.root = new Vector3(clip.rootPositions[f * 3], clip.rootPositions[f * 3 + 1],
                                 clip.rootPositions[f * 3 + 2]);
            t.hasPose = true;
            return true;
        }

        /// <summary>Reset a key to the rest pose, keeping its frame and root position.</summary>
        public void SetIdlePose(Target t)
        {
            var g = ResolvedGenerator;
            var sk = g != null ? g.PoseSkeleton : null;
            int J = sk != null ? sk.jointCount : (t.localQuats != null ? t.localQuats.Length / 4 : 0);
            if (J <= 0) return;
            var q = new float[J * 4];
            for (int j = 0; j < J; j++) q[j * 4] = 1f;   // identity (w,x,y,z) = (1,0,0,0)
            t.localQuats = q;
            t.hasPose = true;
        }

        /// <summary>Bend one limb of the key's pose so its effector reaches <paramref name="targetKimodo"/>
        /// (Kimodo coords). Only the upper/mid joints move; the effector keeps its orientation when
        /// <see cref="keepEffectorOrientation"/> is on, since that orientation is itself constrained.</summary>
        public bool SolveLimbTo(Target t, Limb limb, Vector3 targetKimodo)
        {
            var g = ResolvedGenerator;
            var sk = g != null ? g.PoseSkeleton : null;
            if (t == null || sk == null || t.localQuats == null || t.localQuats.Length != sk.jointCount * 4)
                return false;

            var (upN, midN, endN) = IKChain(limb);
            int iUp = KimodoFK.BoneIndex(sk, upN), iMid = KimodoFK.BoneIndex(sk, midN), iEnd = KimodoFK.BoneIndex(sk, endN);
            if (iUp < 0 || iMid < 0 || iEnd < 0) return false;
            if (!IsFinite(targetKimodo)) return false;

            NormalizePose(t.localQuats);   // never solve off a drifted pose (see WriteQuat)
            KimodoFK.GlobalPose(sk, PoseClip(t), 0, out var gpos, out var grot);
            Quaternion keepEnd = grot[iEnd];
            int iParent = sk.bones[iUp].parent;
            var ik = KimodoIK.SolveTwoBone(
                gpos[iUp], gpos[iMid], gpos[iEnd], targetKimodo,
                grot[iUp], grot[iMid], iParent >= 0 ? grot[iParent] : Quaternion.identity);

            WriteQuat(t.localQuats, iUp, ik.localUpper);
            WriteQuat(t.localQuats, iMid, ik.localMid);
            t.hasPose = true;

            if (keepEffectorOrientation)
            {
                // Re-FK with the new limb and re-derive the effector's local rotation so its global
                // orientation is unchanged (the foot stays flat / the hand keeps facing where it did).
                KimodoFK.GlobalPose(sk, PoseClip(t), 0, out _, out var grot2);
                int endParent = sk.bones[iEnd].parent;
                Quaternion parentG = endParent >= 0 ? grot2[endParent] : Quaternion.identity;
                WriteQuat(t.localQuats, iEnd, KimodoFK.Inv(parentG) * keepEnd);
            }
            return true;
        }

        private static bool IsFinite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
            !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

        /// <summary>Set one joint's global rotation (Kimodo coords) in the key's pose.</summary>
        public bool SetJointGlobalRotation(Target t, int joint, Quaternion globalKimodo)
        {
            var sk = ResolvedGenerator != null ? ResolvedGenerator.PoseSkeleton : null;
            if (t == null || sk == null || t.localQuats == null || joint < 0 || joint >= sk.jointCount) return false;
            KimodoFK.GlobalPose(sk, PoseClip(t), 0, out _, out var grot);
            int par = sk.bones[joint].parent;
            Quaternion parentG = par >= 0 ? grot[par] : Quaternion.identity;
            WriteQuat(t.localQuats, joint, KimodoFK.Inv(parentG) * globalKimodo);
            t.hasPose = true;
            return true;
        }

        /// <summary>How far the effector currently is from the limb's reach: &gt; 0 means the pose
        /// cannot get there and the pelvis has to move (metres). Used for the out-of-reach warning.</summary>
        public float ReachDeficit(Target t, Limb limb, Vector3 targetKimodo)
        {
            var sk = ResolvedGenerator != null ? ResolvedGenerator.PoseSkeleton : null;
            if (t == null || sk == null || t.localQuats == null) return 0f;
            var (upN, midN, endN) = IKChain(limb);
            int iUp = KimodoFK.BoneIndex(sk, upN), iMid = KimodoFK.BoneIndex(sk, midN), iEnd = KimodoFK.BoneIndex(sk, endN);
            if (iUp < 0 || iMid < 0 || iEnd < 0) return 0f;
            var gpos = KimodoFK.GlobalPositions(sk, PoseClip(t), 0);
            float reach = (gpos[iMid] - gpos[iUp]).magnitude + (gpos[iEnd] - gpos[iMid]).magnitude;
            return Mathf.Max(0f, (targetKimodo - gpos[iUp]).magnitude - reach);
        }

        /// <summary>World position of a key's primary effector (for "look at" / framing). False when
        /// there is no pose or no world mapping yet. Measures the mapping, so call it on demand.</summary>
        public bool TryGetKeyWorld(Target t, out Vector3 world)
        {
            world = default;
            var g = ResolvedGenerator;
            var sk = g != null ? g.PoseSkeleton : null;
            if (t == null || sk == null || !EnsurePose(t)) return false;
            var m = KimodoRootMap.Compute(g);
            if (!m.valid) return false;
            int j = KimodoFK.BoneIndex(sk, BaseBone(PrimaryLimb(t)));
            if (j < 0) j = sk.rootIndex;
            if (j < 0) return false;
            world = KimodoRootMap.KimodoToWorld(KimodoFK.GlobalPositions(sk, PoseClip(t), 0)[j], m);
            return true;
        }

        // Always store a UNIT rotation: FK scales a bone's offset by |q|², so an off-unit quaternion
        // stretches the skeleton, and re-solving off that result compounds it (KimodoFK.Unit).
        private static void WriteQuat(float[] q, int joint, Quaternion v)
        {
            v = KimodoFK.Unit(v);
            int i = joint * 4;
            q[i + 0] = v.w; q[i + 1] = v.x; q[i + 2] = v.y; q[i + 3] = v.z;
        }

        /// <summary>Renormalise every rotation in a pose, in place. Repairs poses saved by an earlier
        /// build that let the drift accumulate, and keeps what BuildConstraints sends the server
        /// unit-length.</summary>
        private static void NormalizePose(float[] q)
        {
            if (q == null) return;
            for (int i = 0; i + 3 < q.Length; i += 4)
            {
                float m2 = q[i] * q[i] + q[i + 1] * q[i + 1] + q[i + 2] * q[i + 2] + q[i + 3] * q[i + 3];
                if (!(m2 > 1e-12f) || float.IsInfinity(m2))         // degenerate or non-finite -> identity
                { q[i] = 1f; q[i + 1] = q[i + 2] = q[i + 3] = 0f; continue; }
                if (Mathf.Abs(m2 - 1f) < 1e-9f) continue;
                float inv = 1f / Mathf.Sqrt(m2);
                q[i] *= inv; q[i + 1] *= inv; q[i + 2] *= inv; q[i + 3] *= inv;
            }
        }
    }
}
