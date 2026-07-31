// SPDX-License-Identifier: Apache-2.0
// Brings each character's generated motion back after Unity throws it away.
//
// A KimodoMotion lives in a [NonSerialized] field (it is generated data, not authored data), so every
// script compile, every entry into play mode and every restart wiped it — leaving a character with
// nothing to preview or bake after a generation that takes about a minute. KimodoMotionCache keeps a
// copy in Library/; this puts it back.
//
// Same shape as KimodoBridgeAutoConnect: [InitializeOnLoad] runs again after every domain reload, and
// delayCall waits until the scene objects are actually there to be found.

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AminHP.KimodoBridge.Editor
{
    [InitializeOnLoad]
    public static class KimodoMotionRestore
    {
        static KimodoMotionRestore()
        {
            EditorApplication.delayCall += RestoreAll;
            EditorSceneManager.sceneOpened += OnSceneOpened;
        }

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode) => EditorApplication.delayCall += RestoreAll;

        private static void RestoreAll()
        {
            // Play mode has its own object graph; restoring into it would fight whatever the game does.
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

#if UNITY_2022_2_OR_NEWER
            var gens = Object.FindObjectsByType<KimodoGenerator>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var gens = Object.FindObjectsOfType<KimodoGenerator>(true);
#endif
            int restored = 0;
            foreach (var g in gens)
            {
                if (g == null || g.Motion != null) continue;
                if (g.RestoreCachedMotion()) restored++;
            }
            if (restored > 0)
            {
                // Not a warning: this is the expected path after a compile. It is logged because the
                // preview reappearing on its own is otherwise a little uncanny.
                Debug.Log($"[Kimodo] Restored {restored} generated motion(s) from the cache — no regenerate needed.");
                SceneView.RepaintAll();
            }
        }
    }
}
