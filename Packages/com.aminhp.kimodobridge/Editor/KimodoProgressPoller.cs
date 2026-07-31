// SPDX-License-Identifier: Apache-2.0
// Keeps the generation progress bars fed.
//
// /generate is one long request — a minute is normal — and until it returns the client has nothing to
// show. The server tracks where its denoising loop has got to and answers /progress while still
// working, so this polls that twice a second for as long as a character is generating and writes the
// result onto the generator, which the inspector and the Timeline window then draw.
//
// Polling lives here rather than in the runtime component because it is editor-only chrome: nothing
// about generation depends on it, and a build should not carry it.

using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace AminHP.KimodoBridge.Editor
{
    [InitializeOnLoad]
    public static class KimodoProgressPoller
    {
        private const double IntervalSeconds = 0.5;

        private static double _next;
        private static bool _inFlight;

        static KimodoProgressPoller() { EditorApplication.update += Tick; }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup < _next) return;
            _next = EditorApplication.timeSinceStartup + IntervalSeconds;

#if UNITY_2022_2_OR_NEWER
            var gens = Object.FindObjectsByType<KimodoGenerator>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var gens = Object.FindObjectsOfType<KimodoGenerator>(true);
#endif
            KimodoGenerator busy = null;
            foreach (var g in gens)
            {
                if (g == null) continue;
                if (g.Busy) { busy = g; }
                else if (g.Progress01 >= 0f) { g.ClearProgress(); InternalEditorUtility.RepaintAllViews(); }
            }
            if (busy == null || _inFlight) return;

            var bridge = busy.ResolvedBridge;
            if (bridge == null) return;

            _inFlight = true;
            bridge.GetClient().GetProgress((ok, p, _) =>
            {
                _inFlight = false;
                if (busy == null || !busy.Busy) return;      // finished while we were asking

                // An older server has no /progress; leave the bar indeterminate rather than inventing
                // a number, and stop asking for the rest of this request.
                if (!ok || p == null) { busy.Progress01 = -1f; busy.ProgressLabel = ""; return; }
                if (!p.running) { busy.Progress01 = -1f; busy.ProgressLabel = "waiting for the server…"; }
                else
                {
                    busy.Progress01 = Mathf.Clamp01(p.fraction);
                    busy.ProgressLabel = Describe(p);
                }
                InternalEditorUtility.RepaintAllViews();
            });
        }

        private static string Describe(KimodoProgress p)
        {
            string where = p.segments > 1 ? $"segment {p.segment + 1}/{p.segments} · " : "";
            switch (p.phase)
            {
                case "encoding":
                    // No step count here, and on a CPU text encoder it is a real slice of the wait —
                    // saying so beats a bar that sits at 0 % for half a minute.
                    return $"{where}encoding the prompt · {p.elapsed:0}s";
                case "postprocess":
                    return $"{where}finishing · {p.elapsed:0}s";
                default:
                    return $"{where}step {p.step}/{p.steps} · {p.elapsed:0}s";
            }
        }
    }
}
