// SPDX-License-Identifier: Apache-2.0
// Bakes a KimodoMotion into a Humanoid AnimationClip asset by sampling the
// source rig's HumanPose (root + muscles) per frame. The resulting clip uses
// the standard Mecanim muscle curves, so it retargets onto any Humanoid avatar.

using System.IO;
using UnityEditor;
using UnityEngine;

namespace AminHP.KimodoBridge.Editor
{
    // KimodoRootBake moved to the runtime assembly (AminHP.KimodoBridge) so runtime
    // components can reference it; resolved here via the enclosing namespace.

    public static class KimodoBaker
    {
        /// <summary>Bake a clip in-memory (does not write an asset).</summary>
        /// <param name="rootMotionScale">Multiplier for root (body) translation; use the
        /// window's calibrated value to match the live preview.</param>
        /// <param name="rootBake">How the baked clip's root motion behaves (see KimodoRootBake).</param>
        public static AnimationClip Bake(KimodoMotion motion, int clipIndex, bool loopTime,
            float rootMotionScale = 1f, KimodoRootBake rootBake = KimodoRootBake.InPlace)
        {
            var rig = KimodoSomaHumanoid.BuildSourceRig(motion, "KimodoBakeSource");
            rig.root.hideFlags = HideFlags.HideAndDontSave;
            var handler = new HumanPoseHandler(rig.avatar, rig.root.transform);
            var pose = new HumanPose();

            var clipData = motion.clips[Mathf.Clamp(clipIndex, 0, motion.clips.Count - 1)];
            int T = motion.frameCount;
            float fps = motion.fps <= 0 ? 30f : motion.fps;

            // Root position (RootT) + root rotation (RootQ) + all muscles.
            var rootT = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
            var rootQ = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
            int muscleCount = HumanTrait.MuscleCount;
            var muscleCurves = new AnimationCurve[muscleCount];
            for (int m = 0; m < muscleCount; m++) muscleCurves[m] = new AnimationCurve();

            bool inPlace = rootBake == KimodoRootBake.InPlace;
            float bx0 = 0f, bz0 = 0f;  // frame-0 horizontal root (held constant for in-place)

            for (int f = 0; f < T; f++)
            {
                KimodoSomaHumanoid.ApplyFrame(rig, motion, clipData, f);
                handler.GetHumanPose(ref pose);
                if (!Mathf.Approximately(rootMotionScale, 1f))
                    pose.bodyPosition *= rootMotionScale;
                float time = f / fps;

                if (f == 0) { bx0 = pose.bodyPosition.x; bz0 = pose.bodyPosition.z; }
                // In place: hold horizontal root constant (strip travel) so the character walks on
                // the spot cleanly, keeping the vertical bob. No pose-warping loop settings needed.
                float rx = inPlace ? bx0 : pose.bodyPosition.x;
                float rz = inPlace ? bz0 : pose.bodyPosition.z;

                rootT[0].AddKey(time, rx);
                rootT[1].AddKey(time, pose.bodyPosition.y);
                rootT[2].AddKey(time, rz);
                rootQ[0].AddKey(time, pose.bodyRotation.x);
                rootQ[1].AddKey(time, pose.bodyRotation.y);
                rootQ[2].AddKey(time, pose.bodyRotation.z);
                rootQ[3].AddKey(time, pose.bodyRotation.w);

                for (int m = 0; m < muscleCount; m++)
                    muscleCurves[m].AddKey(time, pose.muscles[m]);
            }

            var clip = new AnimationClip { frameRate = fps };

            SetCurve(clip, "RootT.x", rootT[0]);
            SetCurve(clip, "RootT.y", rootT[1]);
            SetCurve(clip, "RootT.z", rootT[2]);
            SetCurve(clip, "RootQ.x", rootQ[0]);
            SetCurve(clip, "RootQ.y", rootQ[1]);
            SetCurve(clip, "RootQ.z", rootQ[2]);
            SetCurve(clip, "RootQ.w", rootQ[3]);
            for (int m = 0; m < muscleCount; m++)
                SetCurve(clip, HumanTrait.MuscleName[m], muscleCurves[m]);

            // Keep the motion faithful: no Loop-Pose blending (it warps the muscles) and no
            // Bake-Into-Pose (it strips/centres travel and can tilt the character). The "in place"
            // vs "travel" difference is authored directly in the RootT.x/z curves above.
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loopTime;
            settings.loopBlend = false;
            settings.loopBlendOrientation = false;
            settings.loopBlendPositionY = false;
            settings.loopBlendPositionXZ = false;
            settings.keepOriginalOrientation = true;
            settings.keepOriginalPositionY = true;
            settings.keepOriginalPositionXZ = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            clip.EnsureQuaternionContinuity();

            handler.Dispose();
            Object.DestroyImmediate(rig.root);
            return clip;
        }

        /// <summary>Bake and save an AnimationClip asset; returns the asset path.</summary>
        public static string BakeToAsset(KimodoMotion motion, int clipIndex, bool loopTime, string assetPath,
            float rootMotionScale = 1f, KimodoRootBake rootBake = KimodoRootBake.InPlace)
        {
            var clip = Bake(motion, clipIndex, loopTime, rootMotionScale, rootBake);
            string dir = Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
            AssetDatabase.CreateAsset(clip, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return assetPath;
        }

        private static void SetCurve(AnimationClip clip, string property, AnimationCurve curve)
        {
            var binding = EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), property);
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }
    }
}
