// SPDX-License-Identifier: Apache-2.0
// Runtime component: plays a generated KimodoMotion onto the humanoid Animator
// on the same GameObject. Lets you drive Kimodo motion from code at runtime.
//
// Example:
//   var player = character.GetComponent<KimodoMotionPlayer>();
//   var client = new KimodoClient("http://127.0.0.1:8765");
//   client.Generate(new KimodoGenerateRequest { prompt = "wave hello", duration = "3" },
//       (ok, motion, err) => { if (ok) player.Play(motion); });

using UnityEngine;

namespace AminHP.KimodoBridge
{
    [RequireComponent(typeof(Animator))]
    [AddComponentMenu("Kimodo/Kimodo Motion Player")]
    public class KimodoMotionPlayer : MonoBehaviour
    {
        [Tooltip("Which generated sample (clip) to play when a motion has multiple samples.")]
        public int clipIndex = 0;

        [Tooltip("Loop playback when reaching the end.")]
        public bool loop = true;

        [Tooltip("Playback speed multiplier.")]
        public float speed = 1f;

        [Tooltip("Play automatically as soon as a motion is set.")]
        public bool autoPlay = true;

        [Tooltip("Auto-measure the correct root travel scale/direction for this character " +
                 "(recommended; absorbs unit scale like Mixamo's cm↔m). When off, 'rootMotionScale' is used.")]
        public bool autoFitRootMotion = true;

        [Tooltip("Multiplies root travel. 1 = default. Only used when 'autoFitRootMotion' is off.")]
        public float rootMotionScale = 1f;

        private KimodoPlayer _player;
        private Animator _animator;
        private float _time;
        private bool _playing;

        public bool HasMotion => _player != null && _player.IsBound;
        public float Duration => _player != null ? _player.Duration : 0f;
        public float Time => _time;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
        }

        /// <summary>Bind a generated motion and (optionally) begin playing.</summary>
        public bool Play(KimodoMotion motion)
        {
            if (_animator == null) _animator = GetComponent<Animator>();
            _player ??= new KimodoPlayer();
            if (!_player.Bind(_animator, motion)) return false;
            if (autoFitRootMotion) _player.AutoCalibrateRootMotion(clipIndex);
            else _player.RootMotionScale = rootMotionScale;
            _time = 0f;
            _playing = autoPlay;
            _player.SampleTime(clipIndex, 0f, loop);
            return true;
        }

        public void Pause() => _playing = false;
        public void Resume() => _playing = HasMotion;
        public void Restart() { _time = 0f; if (HasMotion) _player.SampleTime(clipIndex, 0f, loop); }

        /// <summary>Manually scrub to a normalized [0,1] position.</summary>
        public void Seek01(float t01)
        {
            if (!HasMotion) return;
            _time = Mathf.Clamp01(t01) * _player.Duration;
            _player.SampleTime(clipIndex, _time, loop);
        }

        private void Update()
        {
            if (!_playing || !HasMotion) return;
            _time += UnityEngine.Time.deltaTime * speed;
            if (!loop && _time >= _player.Duration)
            {
                _time = _player.Duration;
                _playing = false;
            }
            _player.SampleTime(clipIndex, _time, loop);
        }

        private void OnDestroy()
        {
            _player?.Dispose();
            _player = null;
        }
    }
}
