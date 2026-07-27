// SPDX-License-Identifier: Apache-2.0
// Inspector for the KimodoBridge manager: server URL, Connect, model dropdown +
// preload, and live connection/model status.

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
