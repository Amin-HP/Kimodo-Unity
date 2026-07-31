// SPDX-License-Identifier: Apache-2.0
// Re-establishes the bridge connection after a domain reload (entering / exiting Play mode, script
// recompiles) so you don't have to press Connect every time. There is no real socket — "Connect" is
// just a health-check + model preload, and the server keeps the model loaded — so this is silent and
// instant. Gated on a SessionState flag set when you Connect (cleared by Disconnect / editor restart).

using UnityEditor;
using UnityEngine;

namespace AminHP.KimodoBridge.Editor
{
    [InitializeOnLoad]
    internal static class KimodoBridgeAutoConnect
    {
        private const string Key = "AminHP.KimodoBridge.AutoConnect";

        // True once the user has connected this editor session; persists across domain reloads.
        public static bool Wanted
        {
            get => SessionState.GetBool(Key, false);
            set => SessionState.SetBool(Key, value);
        }

        static KimodoBridgeAutoConnect()
        {
            // InitializeOnLoad's static ctor re-runs after every domain reload; defer so the scene is ready.
            EditorApplication.delayCall += Reconnect;

            // "Connected" is not a live socket — it only means a health check once succeeded — so when
            // the server we started goes away, nothing would otherwise contradict the green dot.
            KimodoServerLauncher.Stopped -= OnServerStopped;
            KimodoServerLauncher.Stopped += OnServerStopped;
        }

        private static void OnServerStopped()
        {
            Wanted = false;   // do not silently reconnect to a server that is not there
            foreach (var b in Object.FindObjectsByType<KimodoBridge>(FindObjectsSortMode.None))
            {
                b.Disconnect();
                b.StatusMessage = "The server was stopped.";
            }
            SceneView.RepaintAll();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        private static void Reconnect()
        {
            if (!Wanted) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += Reconnect;   // try again once the editor settles
                return;
            }
            foreach (var b in Object.FindObjectsByType<KimodoBridge>(FindObjectsSortMode.None))
                if (b.Connection != KimodoBridge.ConnectionState.Online &&
                    b.Connection != KimodoBridge.ConnectionState.Connecting)
                    b.Connect(() => SceneView.RepaintAll());
        }
    }
}
