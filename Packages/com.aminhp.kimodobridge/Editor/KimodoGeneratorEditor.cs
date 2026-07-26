// SPDX-License-Identifier: Apache-2.0
// Inspector for KimodoGenerator: prompt + params, Generate, live preview
// (play/scrub/speed/root-motion-scale) and bake to an AnimationClip.

using System;
using UnityEditor;
using UnityEngine;

namespace AminHP.KimodoBridge.Editor
{
    [CustomEditor(typeof(KimodoGenerator))]
    public class KimodoGeneratorEditor : UnityEditor.Editor
    {
        private KimodoGenerator Gen => (KimodoGenerator)target;

        private float _previewSpeed = 1f;
        private double _lastEditorTime;

        private static readonly string[] _rootBakeLabels = { "In place", "Travel" };

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            _lastEditorTime = EditorApplication.timeSinceStartup;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            var g = target as KimodoGenerator;
            if (g == null || !g.Playing || !g.IsPreviewBound) return;
            double now = EditorApplication.timeSinceStartup;
            float dt = (float)(now - _lastEditorTime);
            _lastEditorTime = now;
            float t = g.PreviewTime + dt * _previewSpeed;
            if (!g.loop && t >= g.Duration) { t = g.Duration; g.Playing = false; }
            g.SampleTime(t);
            SceneView.RepaintAll();
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            var g = Gen;

            DrawSetup(g);
            EditorGUILayout.Space();
            DrawGenerate(g);
            EditorGUILayout.Space();
            DrawPreview(g);
            EditorGUILayout.Space();
            DrawBake(g);
        }

        // ---------------------------------------------------------------
        private void DrawSetup(KimodoGenerator g)
        {
            EditorGUI.BeginChangeCheck();
            var bridge = (KimodoBridge)EditorGUILayout.ObjectField(
                new GUIContent("Bridge", "Manager to generate through. Auto-found if empty."),
                g.bridge, typeof(KimodoBridge), true);
            var tgt = (Animator)EditorGUILayout.ObjectField(
                new GUIContent("Target (Humanoid)", "Character to preview on. Defaults to this Animator."),
                g.target, typeof(Animator), true);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(g, "Edit Kimodo Generator");
                g.bridge = bridge;
                if (tgt != g.target) { g.target = tgt; g.RebindPreview(); }
            }

            var rb = g.ResolvedBridge;
            if (rb == null)
                EditorGUILayout.HelpBox("No KimodoBridge in the scene. Add one via GameObject ▸ Kimodo ▸ Bridge Manager.", MessageType.Warning);
            else if (!rb.IsOnline)
                EditorGUILayout.HelpBox("Bridge is not connected. Select it and press Connect.", MessageType.Info);

            var rt = g.ResolvedTarget;
            if (rt != null && (rt.avatar == null || !rt.avatar.isHuman))
                EditorGUILayout.HelpBox("Target's avatar is not Humanoid. Set the model's Rig → Animation Type → Humanoid.", MessageType.Warning);
        }

        private void DrawGenerate(KimodoGenerator g)
        {
            EditorGUILayout.LabelField("Generate", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            string prompt = EditorGUILayout.TextArea(g.prompt, GUILayout.MinHeight(44));
            string duration = EditorGUILayout.TextField(new GUIContent("Duration (s)", "Seconds; space-separated for one per segment."), g.duration);
            int numSamples = EditorGUILayout.IntSlider("Samples", g.numSamples, 1, 8);
            int steps = EditorGUILayout.IntSlider("Diffusion steps", g.diffusionSteps, 10, 200);
            bool useSeed, postprocess;
            int seed = g.seed;
            using (new EditorGUILayout.HorizontalScope())
            {
                useSeed = EditorGUILayout.ToggleLeft("Seed", g.useSeed, GUILayout.Width(60));
                using (new EditorGUI.DisabledScope(!useSeed))
                    seed = EditorGUILayout.IntField(g.seed);
            }
            postprocess = EditorGUILayout.ToggleLeft("Foot-skate cleanup (post-process)", g.postprocess);
            float cw = EditorGUILayout.Slider(new GUIContent("Constraint weight", "How strongly hand/foot targets + waypoints are enforced. Raise if a target is ignored (e.g. a foot won't lift)."), g.constraintWeight, 1f, 8f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(g, "Edit Kimodo prompt");
                g.prompt = prompt; g.duration = duration; g.numSamples = numSamples;
                g.diffusionSteps = steps; g.useSeed = useSeed; g.seed = seed; g.postprocess = postprocess;
                g.constraintWeight = cw;
            }

            var rb = g.ResolvedBridge;
            bool canGen = !g.Busy && rb != null && rb.IsOnline && rb.ModelState != KimodoBridge.ModelLoadState.Loading;
            using (new EditorGUI.DisabledScope(!canGen))
            {
                string label = g.Busy ? "Generating…" : "Generate";
                if (GUILayout.Button(label, GUILayout.Height(30)))
                    g.Generate(Repaint);
            }
            if (!string.IsNullOrEmpty(g.Info))
                EditorGUILayout.HelpBox(g.Info, g.Busy ? MessageType.Info : MessageType.None);
            if (g.Busy)
                EditorGUILayout.LabelField("On this machine generation can take ~1 min (CPU text encoder).", EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawPreview(KimodoGenerator g)
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            if (g.Motion == null)
            {
                EditorGUILayout.HelpBox("Generate a motion to preview.", MessageType.None);
                return;
            }

            if (g.Motion.clips.Count > 1)
            {
                EditorGUI.BeginChangeCheck();
                int clip = EditorGUILayout.IntSlider("Sample", g.clipIndex, 0, g.Motion.clips.Count - 1);
                if (EditorGUI.EndChangeCheck()) { g.clipIndex = clip; g.SampleCurrent(); }
            }

            using (new EditorGUI.DisabledScope(!g.IsPreviewBound))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(g.Playing ? "❚❚ Pause" : "▶ Play", GUILayout.Height(24)))
                    {
                        g.Playing = !g.Playing;
                        _lastEditorTime = EditorApplication.timeSinceStartup;
                    }
                    if (GUILayout.Button("⏮ Restart", GUILayout.Height(24), GUILayout.Width(90)))
                        g.SampleTime(0f);
                    EditorGUI.BeginChangeCheck();
                    bool loop = GUILayout.Toggle(g.loop, "Loop", "Button", GUILayout.Width(60), GUILayout.Height(24));
                    if (EditorGUI.EndChangeCheck()) g.loop = loop;
                }

                float dur = g.Duration;
                EditorGUI.BeginChangeCheck();
                float t = EditorGUILayout.Slider(g.PreviewTime, 0f, Mathf.Max(0.0001f, dur));
                if (EditorGUI.EndChangeCheck()) { g.Playing = false; g.SampleTime(t); }
                EditorGUILayout.LabelField($"t = {g.PreviewTime:0.00}s / {dur:0.00}s  ·  frame {g.CurrentFrame} / {g.FrameCount - 1}", EditorStyles.miniLabel);

                _previewSpeed = EditorGUILayout.Slider("Speed", _previewSpeed, 0.1f, 3f);

                EditorGUI.BeginChangeCheck();
                float scale = EditorGUILayout.FloatField(
                    new GUIContent("Root motion scale", "Multiplies root travel. Reduce if the character shoots across the scene; negative reverses direction."),
                    g.rootMotionScale);
                if (EditorGUI.EndChangeCheck()) { g.rootMotionScale = scale; g.ApplyRootScale(); }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Auto-fit")) g.AutoFitRootScale();
                    if (GUILayout.Button("×1")) { g.rootMotionScale = 1f; g.ApplyRootScale(); }
                    if (GUILayout.Button("Reverse (±)")) { g.rootMotionScale = -g.rootMotionScale; g.ApplyRootScale(); }
                }
                if (g.IsPreviewBound)
                    EditorGUILayout.LabelField(
                        $"humanScale src {g.Preview.SourceHumanScale:0.###}, tgt {g.Preview.TargetHumanScale:0.###}",
                        EditorStyles.miniLabel);
            }

            if (!g.IsPreviewBound)
                EditorGUILayout.HelpBox("Assign a Humanoid Target above to preview.", MessageType.Info);
        }

        private void DrawBake(KimodoGenerator g)
        {
            EditorGUILayout.LabelField("Bake", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var mode = (KimodoRootBake)EditorGUILayout.Popup(
                new GUIContent("Root motion", "How the baked clip's root behaves when looped."),
                (int)g.rootBakeMode, _rootBakeLabels);
            if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(g, "Edit Kimodo bake mode"); g.rootBakeMode = mode; }

            if (g.rootBakeMode == KimodoRootBake.InPlace)
                EditorGUILayout.HelpBox("Walks on the spot. Looks the same with or without Apply Root Motion.", MessageType.None);
            else
                EditorGUILayout.HelpBox("Enable the Animator's 'Apply Root Motion' to travel; leave it off to walk on the spot.", MessageType.None);

            using (new EditorGUI.DisabledScope(g.Motion == null))
            {
                if (GUILayout.Button("Bake to AnimationClip…", GUILayout.Height(26)))
                    Bake(g);
            }
        }

        private void Bake(KimodoGenerator g)
        {
            string suggested = SanitizeFileName(g.prompt);
            string path = EditorUtility.SaveFilePanelInProject(
                "Bake Kimodo motion", suggested, "anim", "Choose where to save the baked AnimationClip.");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string saved = KimodoBaker.BakeToAsset(g.Motion, g.clipIndex, g.loop, path, g.rootMotionScale, g.rootBakeMode);
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(saved);
                EditorGUIUtility.PingObject(clip);
                Selection.activeObject = clip;
                g.Info = "Baked → " + saved;
            }
            catch (Exception e)
            {
                g.Info = "Bake failed: " + e.Message;
                Debug.LogException(e);
            }
            Repaint();
        }

        private static string SanitizeFileName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "KimodoMotion";
            s = s.Trim();
            if (s.Length > 40) s = s.Substring(0, 40);
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_').Replace('.', '_');
        }
    }
}
