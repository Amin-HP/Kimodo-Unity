// SPDX-License-Identifier: Apache-2.0
// THE editor-time playback clock for Kimodo previews.
//
// Both the Generator inspector and the Timeline window show transport controls, and both used to
// advance KimodoGenerator.PreviewTime from their own EditorApplication.update hook — so with the
// character selected AND the Timeline window open, the head advanced twice per tick and everything
// played at double speed. Playback now has exactly one owner: the UIs only toggle Playing (through
// SetPlaying/Toggle) and repaint themselves when Advanced fires.
//
// This is also where the loop wrap lives: PreviewTime must be wrapped, not left to run past the
// end, or the playhead sticks at the last frame while the retarget keeps looping.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AminHP.KimodoBridge.Editor
{
    [InitializeOnLoad]
    public static class KimodoPlayback
    {
        private static readonly List<KimodoGenerator> _playing = new List<KimodoGenerator>();
        private static double _last;

        /// <summary>Raised after the playhead moved, so open windows/inspectors can repaint.</summary>
        public static event Action Advanced;

        static KimodoPlayback()
        {
            EditorApplication.update += Tick;
            _last = EditorApplication.timeSinceStartup;
        }

        public static bool IsPlaying(KimodoGenerator g) => g != null && g.Playing;

        /// <summary>Start/stop playback for a generator. Playing requires a bound preview.</summary>
        public static void SetPlaying(KimodoGenerator g, bool play)
        {
            if (g == null) return;
            g.Playing = play && g.IsPreviewBound;
            if (g.Playing)
            {
                if (!_playing.Contains(g)) _playing.Add(g);
                _last = EditorApplication.timeSinceStartup;   // don't jump on the first tick
            }
            else _playing.Remove(g);
        }

        public static void Toggle(KimodoGenerator g) => SetPlaying(g, g != null && !g.Playing);

        private static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            float dt = (float)(now - _last);
            _last = now;
            if (_playing.Count == 0) return;

            bool moved = false;
            for (int i = _playing.Count - 1; i >= 0; i--)
            {
                var g = _playing[i];
                if (g == null || !g.Playing || !g.IsPreviewBound) { _playing.RemoveAt(i); continue; }

                float dur = g.Duration;
                float t = g.PreviewTime + dt * Mathf.Max(0.01f, g.PlaybackSpeed);
                if (t >= dur)
                {
                    if (g.loop) t = dur > 1e-4f ? Mathf.Repeat(t, dur) : 0f;
                    else { t = dur; g.Playing = false; _playing.RemoveAt(i); }
                }
                g.SampleTime(t);
                moved = true;
            }

            if (moved)
            {
                SceneView.RepaintAll();
                Advanced?.Invoke();
            }
        }
    }
}
