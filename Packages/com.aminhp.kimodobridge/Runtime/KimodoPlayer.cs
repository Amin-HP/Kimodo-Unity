// SPDX-License-Identifier: Apache-2.0
// Retargets a KimodoMotion onto a target Humanoid Animator using the standard
// Mecanim muscle space (HumanPoseHandler). Used by both the live editor preview
// and the runtime KimodoMotionPlayer component.

using System;
using UnityEngine;

namespace AminHP.KimodoBridge
{
    public class KimodoPlayer : IDisposable
    {
        private KimodoSourceRig _rig;
        private HumanPoseHandler _sourceHandler;
        private HumanPoseHandler _targetHandler;
        private HumanPose _pose;
        private Animator _target;
        private KimodoMotion _motion;

        public KimodoMotion Motion => _motion;
        public bool IsBound => _rig != null && _target != null;

        /// <summary>Multiplier applied to the retargeted root (body) translation.
        /// 1 = Unity's default proportional retarget. If the character travels far too much
        /// (e.g. it was imported at a large scale), reduce this; the humanScale readout in the
        /// window hints at the right magnitude.</summary>
        public float RootMotionScale = 1f;

        public float SourceHumanScale { get; private set; } = 1f;
        public float TargetHumanScale { get; private set; } = 1f;

        /// <summary>Build a source rig for <paramref name="motion"/> and bind it to a target humanoid.</summary>
        public bool Bind(Animator target, KimodoMotion motion, bool hideSourceRig = true)
        {
            Dispose();
            if (target == null) { Debug.LogError("[Kimodo] Target Animator is null."); return false; }
            if (target.avatar == null || !target.avatar.isHuman)
            {
                Debug.LogError("[Kimodo] Target Animator has no Humanoid avatar. Set the model's Rig to Humanoid.");
                return false;
            }

            _target = target;
            _motion = motion;
            _rig = KimodoSomaHumanoid.BuildSourceRig(motion);
            if (_rig.avatar == null || !_rig.avatar.isValid)
            {
                Debug.LogError("[Kimodo] Failed to build a valid source avatar.");
                Dispose();
                return false;
            }
            if (hideSourceRig)
                _rig.root.hideFlags = HideFlags.HideAndDontSave;

            _sourceHandler = new HumanPoseHandler(_rig.avatar, _rig.root.transform);
            _targetHandler = new HumanPoseHandler(_target.avatar, _target.transform);
            _pose = new HumanPose();

            SourceHumanScale = _rig.humanScale > 0f ? _rig.humanScale : 1f;
            TargetHumanScale = _target.humanScale > 0f ? _target.humanScale : 1f;
            return true;
        }

        public int ClipCount => _motion?.clips?.Count ?? 0;
        public int FrameCount => _motion?.frameCount ?? 0;
        public float Fps => _motion?.fps ?? 30f;
        public float Duration => FrameCount / Mathf.Max(1f, Fps);

        /// <summary>Pose the target at an integer frame of the given clip.</summary>
        public void SampleFrame(int clipIndex, int frame)
        {
            if (!IsBound) return;
            frame = Mathf.Clamp(frame, 0, FrameCount - 1);
            clipIndex = Mathf.Clamp(clipIndex, 0, ClipCount - 1);
            var clip = _motion.clips[clipIndex];

            KimodoSomaHumanoid.ApplyFrame(_rig, _motion, clip, frame);
            _sourceHandler.GetHumanPose(ref _pose);
            if (!Mathf.Approximately(RootMotionScale, 1f))
                _pose.bodyPosition *= RootMotionScale;
            _targetHandler.SetHumanPose(ref _pose);
        }

        /// <summary>
        /// Pose the target from an arbitrary Kimodo local-rotation pose (J*4 wxyz) + root
        /// (raw Kimodo coords), rather than a frame of the bound motion. Used to preview an
        /// authored pose-constraint key on a ghost copy of the character.
        /// </summary>
        public void PoseFromLocal(float[] localQuats, Vector3 rootKimodo)
        {
            if (!IsBound || localQuats == null) return;
            var view = new KimodoClip
            {
                localQuats = localQuats,
                rootPositions = new[] { rootKimodo.x, rootKimodo.y, rootKimodo.z },
            };
            KimodoSomaHumanoid.ApplyFrame(_rig, _motion, view, 0);
            _sourceHandler.GetHumanPose(ref _pose);
            if (!Mathf.Approximately(RootMotionScale, 1f))
                _pose.bodyPosition *= RootMotionScale;
            _targetHandler.SetHumanPose(ref _pose);
        }

        /// <summary>Pose the target at a time in seconds (nearest frame).</summary>
        public void SampleTime(int clipIndex, float seconds, bool loop = false)
        {
            if (!IsBound || FrameCount == 0) return;
            int frame = Mathf.RoundToInt(seconds * Fps);
            if (loop) frame = ((frame % FrameCount) + FrameCount) % FrameCount;
            SampleFrame(clipIndex, frame);
        }

        /// <summary>
        /// Measure the target's actual hip travel for the clip and solve for the signed
        /// RootMotionScale that makes it travel the real Kimodo distance. This absorbs any
        /// hidden unit/scale factor (e.g. Mixamo's cm↔m 100×) and fixes a reversed direction,
        /// so the character moves correctly without manual tuning. Returns the scale it set.
        /// Leaves scale unchanged for near-stationary motions (e.g. idle) where it can't measure.
        /// </summary>
        public float AutoCalibrateRootMotion(int clipIndex = 0)
        {
            if (!IsBound || FrameCount < 2) return RootMotionScale;
            clipIndex = Mathf.Clamp(clipIndex, 0, ClipCount - 1);
            var clip = _motion.clips[clipIndex];

            // Kimodo horizontal displacement: use the frame of maximum travel from frame 0.
            Vector3 r0 = KimodoSomaHumanoid.RootAt(clip, 0);
            int bestF = 0; float bestSq = 0f; Vector3 kimodoDisp = Vector3.zero;
            for (int f = 1; f < FrameCount; f++)
            {
                Vector3 d = KimodoSomaHumanoid.RootAt(clip, f) - r0; d.y = 0f;
                if (d.sqrMagnitude > bestSq) { bestSq = d.sqrMagnitude; bestF = f; kimodoDisp = d; }
            }
            if (bestSq < 1e-6f) return RootMotionScale; // essentially stationary

            var hips = _target.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null) return RootMotionScale;

            float saved = RootMotionScale;
            RootMotionScale = 1f;
            SampleFrame(clipIndex, 0); Vector3 h0 = hips.position;
            SampleFrame(clipIndex, bestF); Vector3 h1 = hips.position;
            Vector3 measured = h1 - h0; measured.y = 0f;

            float denom = Vector3.Dot(measured, measured);
            if (denom < 1e-9f) { RootMotionScale = saved; SampleFrame(clipIndex, 0); return RootMotionScale; }

            // Express the desired Kimodo displacement in world space (root may be rotated), then
            // take the signed least-squares scalar so measured*scale ≈ desired (real metres, sign).
            Vector3 desiredWorld = _target.transform.rotation * kimodoDisp;
            RootMotionScale = Vector3.Dot(desiredWorld, measured) / denom;
            SampleFrame(clipIndex, 0);
            return RootMotionScale;
        }

        public void Dispose()
        {
            _sourceHandler?.Dispose();
            _targetHandler?.Dispose();
            _sourceHandler = null;
            _targetHandler = null;
            if (_rig != null && _rig.root != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_rig.root);
                else UnityEngine.Object.DestroyImmediate(_rig.root);
            }
            _rig = null;
            _target = null;
        }
    }
}
