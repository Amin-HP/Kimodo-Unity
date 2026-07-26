// SPDX-License-Identifier: Apache-2.0
// Builds a Unity Humanoid "source" rig from a Kimodo SOMA skeleton so that
// generated motion can be retargeted onto any Unity Humanoid character via
// HumanPoseHandler (the standard Mecanim muscle space).
//
// SOMA (somaskel77) uses Mixamo-style bone names, which map cleanly onto
// Unity's HumanBodyBones. We build a GameObject hierarchy in the SOMA rest
// (T-)pose, tag bones with their HumanBodyBones, and call
// AvatarBuilder.BuildHumanAvatar to get a valid humanoid Avatar for the source.

using System.Collections.Generic;
using UnityEngine;

namespace AminHP.KimodoBridge
{
    /// <summary>A built source rig: the transform hierarchy + its humanoid Avatar.</summary>
    public class KimodoSourceRig
    {
        public GameObject root;                 // top-level container (identity)
        public Avatar avatar;                   // humanoid avatar built from the SOMA rest pose
        public Transform[] boneTransforms;      // index-aligned with KimodoMotion.bones
        public int rootIndex;
        public float humanScale = 1f;           // Animator.humanScale of the source avatar
        public Transform HipsTransform => boneTransforms[rootIndex];
    }

    public static class KimodoSomaHumanoid
    {
        // SOMA (somaskel77 / somaskel30) bone name -> Unity HumanBodyBones.
        // Unmapped SOMA joints (e.g. Neck2, *End leaves) still exist in the source
        // hierarchy and influence their mapped descendants; they simply aren't
        // exposed as human bones.
        public static readonly Dictionary<string, HumanBodyBones> SomaToHuman = new Dictionary<string, HumanBodyBones>
        {
            { "Hips", HumanBodyBones.Hips },
            { "Spine1", HumanBodyBones.Spine },
            { "Spine2", HumanBodyBones.Chest },
            { "Chest", HumanBodyBones.UpperChest },
            { "Neck1", HumanBodyBones.Neck },
            { "Head", HumanBodyBones.Head },
            { "Jaw", HumanBodyBones.Jaw },
            { "LeftEye", HumanBodyBones.LeftEye },
            { "RightEye", HumanBodyBones.RightEye },

            { "LeftShoulder", HumanBodyBones.LeftShoulder },
            { "LeftArm", HumanBodyBones.LeftUpperArm },
            { "LeftForeArm", HumanBodyBones.LeftLowerArm },
            { "LeftHand", HumanBodyBones.LeftHand },
            { "RightShoulder", HumanBodyBones.RightShoulder },
            { "RightArm", HumanBodyBones.RightUpperArm },
            { "RightForeArm", HumanBodyBones.RightLowerArm },
            { "RightHand", HumanBodyBones.RightHand },

            { "LeftLeg", HumanBodyBones.LeftUpperLeg },
            { "LeftShin", HumanBodyBones.LeftLowerLeg },
            { "LeftFoot", HumanBodyBones.LeftFoot },
            { "LeftToeBase", HumanBodyBones.LeftToes },
            { "RightLeg", HumanBodyBones.RightUpperLeg },
            { "RightShin", HumanBodyBones.RightLowerLeg },
            { "RightFoot", HumanBodyBones.RightFoot },
            { "RightToeBase", HumanBodyBones.RightToes },

            // Left fingers (somaskel77). SOMA has 1..4/End per finger; Unity has 3.
            { "LeftHandThumb1", HumanBodyBones.LeftThumbProximal },
            { "LeftHandThumb2", HumanBodyBones.LeftThumbIntermediate },
            { "LeftHandThumb3", HumanBodyBones.LeftThumbDistal },
            { "LeftHandIndex1", HumanBodyBones.LeftIndexProximal },
            { "LeftHandIndex2", HumanBodyBones.LeftIndexIntermediate },
            { "LeftHandIndex3", HumanBodyBones.LeftIndexDistal },
            { "LeftHandMiddle1", HumanBodyBones.LeftMiddleProximal },
            { "LeftHandMiddle2", HumanBodyBones.LeftMiddleIntermediate },
            { "LeftHandMiddle3", HumanBodyBones.LeftMiddleDistal },
            { "LeftHandRing1", HumanBodyBones.LeftRingProximal },
            { "LeftHandRing2", HumanBodyBones.LeftRingIntermediate },
            { "LeftHandRing3", HumanBodyBones.LeftRingDistal },
            { "LeftHandPinky1", HumanBodyBones.LeftLittleProximal },
            { "LeftHandPinky2", HumanBodyBones.LeftLittleIntermediate },
            { "LeftHandPinky3", HumanBodyBones.LeftLittleDistal },

            // Right fingers
            { "RightHandThumb1", HumanBodyBones.RightThumbProximal },
            { "RightHandThumb2", HumanBodyBones.RightThumbIntermediate },
            { "RightHandThumb3", HumanBodyBones.RightThumbDistal },
            { "RightHandIndex1", HumanBodyBones.RightIndexProximal },
            { "RightHandIndex2", HumanBodyBones.RightIndexIntermediate },
            { "RightHandIndex3", HumanBodyBones.RightIndexDistal },
            { "RightHandMiddle1", HumanBodyBones.RightMiddleProximal },
            { "RightHandMiddle2", HumanBodyBones.RightMiddleIntermediate },
            { "RightHandMiddle3", HumanBodyBones.RightMiddleDistal },
            { "RightHandRing1", HumanBodyBones.RightRingProximal },
            { "RightHandRing2", HumanBodyBones.RightRingIntermediate },
            { "RightHandRing3", HumanBodyBones.RightRingDistal },
            { "RightHandPinky1", HumanBodyBones.RightLittleProximal },
            { "RightHandPinky2", HumanBodyBones.RightLittleIntermediate },
            { "RightHandPinky3", HumanBodyBones.RightLittleDistal },
        };

        /// <summary>
        /// Build the source rig (hierarchy + humanoid Avatar) from a motion's skeleton.
        /// The rig is created in the SOMA rest/T-pose (bone offsets, identity rotations).
        /// </summary>
        public static KimodoSourceRig BuildSourceRig(KimodoMotion motion, string rootName = "KimodoSource")
        {
            int n = motion.bones.Count;
            var rig = new KimodoSourceRig
            {
                root = new GameObject(rootName),
                boneTransforms = new Transform[n],
                rootIndex = motion.rootIndex,
            };
            rig.root.transform.localPosition = Vector3.zero;
            rig.root.transform.localRotation = Quaternion.identity;

            // Create a transform per bone.
            for (int i = 0; i < n; i++)
            {
                var b = motion.bones[i];
                var go = new GameObject(b.name);
                rig.boneTransforms[i] = go.transform;
            }
            // Parent and place at rest pose (local offsets, converted to Unity coords).
            for (int i = 0; i < n; i++)
            {
                var b = motion.bones[i];
                Transform t = rig.boneTransforms[i];
                Transform parent = b.parent >= 0 ? rig.boneTransforms[b.parent] : rig.root.transform;
                t.SetParent(parent, false);
                t.localPosition = KimodoCoords.Position(b.ox, b.oy, b.oz);
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
            }

            rig.avatar = BuildAvatar(rig.root, motion);

            // Read the source avatar's humanScale (used to normalize root translation
            // to absolute metres when retargeting). Needs an Animator with the avatar.
            if (rig.avatar != null && rig.avatar.isHuman)
            {
                var anim = rig.root.AddComponent<Animator>();
                anim.avatar = rig.avatar;
                float hs = anim.humanScale;
                rig.humanScale = hs > 0f ? hs : 1f;
                anim.enabled = false; // no controller; keep it from touching transforms
            }
            return rig;
        }

        private static Avatar BuildAvatar(GameObject root, KimodoMotion motion)
        {
            var human = new List<HumanBone>();
            string[] boneNames = HumanTrait.BoneName; // human-name strings indexed by HumanBodyBones

            foreach (var b in motion.bones)
            {
                if (SomaToHuman.TryGetValue(b.name, out var hbb))
                {
                    var hb = new HumanBone
                    {
                        boneName = b.name,
                        humanName = boneNames[(int)hbb],
                    };
                    hb.limit.useDefaultValues = true;
                    human.Add(hb);
                }
            }

            // Skeleton must include the container root plus every bone transform.
            var skeleton = new List<SkeletonBone>();
            skeleton.Add(new SkeletonBone
            {
                name = root.name,
                position = Vector3.zero,
                rotation = Quaternion.identity,
                scale = Vector3.one,
            });
            for (int i = 0; i < motion.bones.Count; i++)
            {
                Transform t = null;
                // boneTransforms not returned here; re-find under root by name order.
                // (Names are unique in SOMA skeletons.)
                t = FindDeep(root.transform, motion.bones[i].name);
                skeleton.Add(new SkeletonBone
                {
                    name = motion.bones[i].name,
                    position = t.localPosition,
                    rotation = t.localRotation,
                    scale = t.localScale,
                });
            }

            var desc = new HumanDescription
            {
                human = human.ToArray(),
                skeleton = skeleton.ToArray(),
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0.0f,
                hasTranslationDoF = false,
            };

            Avatar avatar = AvatarBuilder.BuildHumanAvatar(root, desc);
            if (!avatar.isValid || !avatar.isHuman)
                Debug.LogError("[Kimodo] Built avatar is not a valid humanoid. Check SOMA->Human bone mapping / rest pose.");
            avatar.name = "KimodoSourceAvatar";
            return avatar;
        }

        private static Transform FindDeep(Transform parent, string name)
        {
            if (parent.name == name) return parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                var r = FindDeep(parent.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>
        /// Pose the source rig at a given frame (local rotations + root position),
        /// converting Kimodo coords to Unity coords. Returns the converted root position.
        /// </summary>
        public static Vector3 ApplyFrame(KimodoSourceRig rig, KimodoMotion motion, KimodoClip clip, int frame)
        {
            int J = motion.jointCount;
            int qBase = frame * J * 4;
            for (int j = 0; j < J; j++)
            {
                rig.boneTransforms[j].localRotation = KimodoCoords.Quat(clip.localQuats, qBase + j * 4);
            }
            Vector3 root = KimodoCoords.Position(clip.rootPositions, frame * 3);
            rig.HipsTransform.localPosition = root;
            return root;
        }

        /// <summary>Converted (Unity-space) root position, metres, for a given frame.</summary>
        public static Vector3 RootAt(KimodoClip clip, int frame)
        {
            return KimodoCoords.Position(clip.rootPositions, frame * 3);
        }
    }
}
