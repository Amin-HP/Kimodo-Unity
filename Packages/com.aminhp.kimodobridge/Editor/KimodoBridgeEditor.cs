// SPDX-License-Identifier: Apache-2.0
// Inspector for the KimodoBridge manager: server URL, Connect, model dropdown +
// preload, live connection/model status, and starting/stopping the server itself
// (see KimodoServerLauncher).

using System;
using UnityEditor;
using UnityEngine;

namespace AminHP.KimodoBridge.Editor
{
    [CustomEditor(typeof(KimodoBridge))]
    public class KimodoBridgeEditor : UnityEditor.Editor
    {
        private KimodoBridge Bridge => (KimodoBridge)target;

        public override void OnInspectorGUI()
        {
            var b = Bridge;

            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                string url = EditorGUILayout.TextField(b.serverUrl);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(b, "Edit Kimodo server URL");
                    b.serverUrl = url;
                }
                if (GUILayout.Button("Connect", GUILayout.Width(80)))
                { KimodoBridgeAutoConnect.Wanted = true; b.Connect(Repaint); }
                using (new EditorGUI.DisabledScope(b.Connection != KimodoBridge.ConnectionState.Online))
                    if (GUILayout.Button("Disconnect", GUILayout.Width(90)))
                    { KimodoBridgeAutoConnect.Wanted = false; b.Disconnect(); Repaint(); }
            }

            EditorGUILayout.LabelField(
                "Stays connected across Play-mode / recompiles (auto-reconnect). Disconnect to stop.",
                EditorStyles.wordWrappedMiniLabel);
            DrawStatusDot(ConnColor(b.Connection), $"● {b.Connection}");
            EditorGUILayout.LabelField(b.StatusMessage, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space();
            DrawServerProcess(b);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Model", EditorStyles.boldLabel);
            DrawModelPicker(b);

            if (b.Connection != KimodoBridge.ConnectionState.Online)
            {
                EditorGUILayout.HelpBox("Press Connect to load the model list and preload the model.", MessageType.None);
                return;
            }

            DrawStatusDot(ModelColor(b.ModelState),
                (b.ModelState == KimodoBridge.ModelLoadState.Loading ? "◌ " : "● ") + b.ModelStatus);
        }

        // Run the Python server from here instead of keeping a PowerShell window open. The path is an
        // EditorPref, not a scene value: it points at this machine's Kimodo checkout, which is not
        // something to commit into a scene.
        private void DrawServerProcess(KimodoBridge b)
        {
            _serverFoldout = EditorGUILayout.Foldout(_serverFoldout, "Server process", true);
            if (!_serverFoldout) return;

            using (new EditorGUI.IndentLevelScope())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    string path = EditorGUILayout.TextField(new GUIContent("run_bridge.ps1",
                        "The launcher script in your Kimodo checkout. Remembered per machine."),
                        KimodoServerLauncher.ScriptPath);
                    if (EditorGUI.EndChangeCheck()) KimodoServerLauncher.ScriptPath = path;

                    if (GUILayout.Button("…", GUILayout.Width(26)))
                    {
                        string picked = EditorUtility.OpenFilePanel("Select run_bridge.ps1", "", "ps1");
                        if (!string.IsNullOrEmpty(picked)) KimodoServerLauncher.ScriptPath = picked;
                    }
                }

                bool running = KimodoServerLauncher.IsRunning;
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(running || KimodoServerLauncher.Starting))
                        if (GUILayout.Button(KimodoServerLauncher.Starting ? "Starting…" : "Start server"))
                        {
                            KimodoServerLauncher.ProbeUrl = b.serverUrl;
                            KimodoServerLauncher.Start(b.serverUrl, b.model, (ok, _) =>
                            {
                                // Connect on its own once it answers: starting it and then having to
                                // press Connect would be a pointless second step.
                                if (ok) { KimodoBridgeAutoConnect.Wanted = true; b.Connect(Repaint); }
                                Repaint();
                            });
                            Repaint();
                        }
                    using (new EditorGUI.DisabledScope(!running))
                        if (GUILayout.Button("Stop server")) { KimodoServerLauncher.Stop(); Repaint(); }
                }

                if (KimodoServerLauncher.Starting)
                    EditorGUILayout.LabelField("Starting — the first run loads the model, which takes a while.",
                        EditorStyles.wordWrappedMiniLabel);
                else if (running)
                    EditorGUILayout.LabelField(
                        $"● Running (PID {KimodoServerLauncher.RunningPid})" +
                        (KimodoServerLauncher.Detached
                            ? " — started before the last recompile, so its output is no longer captured."
                            : ""),
                        EditorStyles.miniLabel);
                else
                    EditorGUILayout.LabelField("Not running. You can also start it yourself in a terminal.",
                        EditorStyles.wordWrappedMiniLabel);

                if (!string.IsNullOrEmpty(KimodoServerLauncher.Problem))
                    EditorGUILayout.HelpBox(KimodoServerLauncher.Problem, MessageType.Error);

                var log = KimodoServerLauncher.Log;
                if (log.Count > 0)
                {
                    _logFoldout = EditorGUILayout.Foldout(_logFoldout, $"Server output ({log.Count} lines)", true);
                    if (_logFoldout)
                    {
                        _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(120f));
                        // Newest last, like a terminal.
                        for (int i = 0; i < log.Count; i++)
                            EditorGUILayout.SelectableLabel(log[i], EditorStyles.miniLabel,
                                GUILayout.Height(14f));
                        EditorGUILayout.EndScrollView();
                    }
                }
            }
        }

        private bool _serverFoldout = true, _logFoldout;
        private Vector2 _logScroll;

        private void DrawModelPicker(KimodoBridge b)
        {
            if (b.Models == null || b.Models.models.Count == 0)
            {
                // No list yet: fall back to a free-text model key so the user isn't blocked.
                EditorGUI.BeginChangeCheck();
                string key = EditorGUILayout.TextField("Model key", b.model);
                if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(b, "Edit Kimodo model"); b.model = key; }
                return;
            }

            var models = b.Models.models;
            var labels = new string[models.Count];
            int current = 0;
            for (int i = 0; i < models.Count; i++)
            {
                labels[i] = $"{models[i].displayName}{(models[i].loaded ? "  ✓ loaded" : "")}";
                if (models[i].shortKey == b.model) current = i;
            }

            using (new EditorGUI.DisabledScope(b.ModelState == KimodoBridge.ModelLoadState.Loading))
            {
                EditorGUI.BeginChangeCheck();
                int picked = EditorGUILayout.Popup("Model", current, labels);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(b, "Select Kimodo model");
                    b.model = models[picked].shortKey;
                    b.LoadModel(Repaint);   // switching preloads it
                }
            }
        }

        private static void DrawStatusDot(Color c, string text)
        {
            var prev = GUI.color;
            GUI.color = c;
            EditorGUILayout.LabelField(text, EditorStyles.wordWrappedMiniLabel);
            GUI.color = prev;
        }

        private static Color ConnColor(KimodoBridge.ConnectionState s) => s switch
        {
            KimodoBridge.ConnectionState.Online => new Color(0.4f, 0.9f, 0.4f),
            KimodoBridge.ConnectionState.Offline => new Color(0.95f, 0.5f, 0.5f),
            KimodoBridge.ConnectionState.Connecting => new Color(0.95f, 0.9f, 0.4f),
            _ => Color.gray,
        };

        private static Color ModelColor(KimodoBridge.ModelLoadState s) => s switch
        {
            KimodoBridge.ModelLoadState.Loaded => new Color(0.4f, 0.9f, 0.4f),
            KimodoBridge.ModelLoadState.Loading => new Color(0.95f, 0.9f, 0.4f),
            KimodoBridge.ModelLoadState.Failed => new Color(0.95f, 0.5f, 0.5f),
            _ => Color.gray,
        };
    }
}
