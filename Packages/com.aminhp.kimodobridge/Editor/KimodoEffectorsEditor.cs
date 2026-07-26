// SPDX-License-Identifier: Apache-2.0
// Inspector + Scene gizmos for KimodoEffectors. Each hand/foot target gets a position handle
// (and a rotation handle when its orientation is constrained), so you place it in the world where
// the effector should be at that frame — like posing a keyframe.

using UnityEditor;
using UnityEngine;

namespace AminHP.KimodoBridge.Editor
{
    [CustomEditor(typeof(KimodoEffectors))]
    public class KimodoEffectorsEditor : UnityEditor.Editor
    {
        private KimodoEffectors Eff => (KimodoEffectors)target;

        private static Color ColorFor(KimodoEffectors.Kind k) => k switch
        {
            KimodoEffectors.Kind.LeftHand => new Color(0.35f, 0.7f, 1f),
            KimodoEffectors.Kind.RightHand => new Color(0.2f, 0.5f, 1f),
            KimodoEffectors.Kind.LeftFoot => new Color(1f, 0.75f, 0.3f),
            KimodoEffectors.Kind.RightFoot => new Color(1f, 0.55f, 0.15f),
            _ => Color.white,
        };

        public override void OnInspectorGUI()
        {
            var e = Eff;
            var g = e.ResolvedGenerator;
            if (g == null) { EditorGUILayout.HelpBox("Needs a KimodoGenerator on the same GameObject.", MessageType.Warning); return; }
            if (g.Motion == null) { EditorGUILayout.HelpBox("Generate a motion on the Generator first.", MessageType.None); return; }

            EditorGUILayout.LabelField("Hand / foot targets", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Add a target, pick which hand/foot and the frame, then drag its handle to where it should " +
                "be. Enable rotation to also aim it. Targets are sent on the next Generate.",
                EditorStyles.wordWrappedMiniLabel);
            if (!g.IsPreviewBound)
                EditorGUILayout.HelpBox("Assign a Humanoid Target and Generate to place and move targets.", MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Preview frame: {g.CurrentFrame} / {Mathf.Max(0, g.FrameCount - 1)}", EditorStyles.miniLabel);
                if (GUILayout.Button($"＋ Add @ {g.CurrentFrame}", GUILayout.Width(120)))
                    AddTarget(e, g, g.CurrentFrame);
                using (new EditorGUI.DisabledScope(e.targets.Count == 0))
                    if (GUILayout.Button("Clear", GUILayout.Width(60)))
                    {
                        Undo.RecordObject(e, "Clear targets");
                        e.targets.Clear();
                    }
            }

            int removeAt = -1;
            for (int i = 0; i < e.targets.Count; i++)
            {
                var t = e.targets[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        var kind = (KimodoEffectors.Kind)EditorGUILayout.EnumPopup(t.kind, GUILayout.Width(90));
                        int frame = Mathf.Clamp(EditorGUILayout.IntField("Frame", t.frame), 0, Mathf.Max(0, g.FrameCount - 1));
                        if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(e, "Edit target"); t.kind = kind; t.frame = frame; }

                        if (GUILayout.Button("Go", GUILayout.Width(30)))
                        {
                            g.Playing = false;
                            g.SampleTime(g.Fps > 0f ? t.frame / g.Fps : 0f);
                            SceneView.RepaintAll();
                        }
                        if (GUILayout.Button("✕", GUILayout.Width(22))) removeAt = i;
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        Vector3 world = EditorGUILayout.Vector3Field(GUIContent.none, t.world);
                        if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(e, "Move target"); t.world = world; }
                        if (GUILayout.Button("Snap to bone", GUILayout.Width(100)))
                            SnapToBone(e, g, t);
                    }
                    EditorGUI.BeginChangeCheck();
                    bool cr = EditorGUILayout.ToggleLeft("Constrain rotation (aim it)", t.constrainRotation);
                    if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(e, "Edit target rotation flag"); t.constrainRotation = cr; }
                }
            }
            if (removeAt >= 0) { Undo.RecordObject(e, "Remove target"); e.targets.RemoveAt(removeAt); }

            if (e.targets.Count > 0)
                EditorGUILayout.LabelField($"{e.targets.Count} target(s) sent on next Generate.", EditorStyles.miniLabel);

            if (GUI.changed) SceneView.RepaintAll();
        }

        // Add a target at the current frame, snapped to the default foot bone as a starting point.
        private void AddTarget(KimodoEffectors e, KimodoGenerator g, int frame)
        {
            var t = new KimodoEffectors.Target { frame = frame, kind = KimodoEffectors.Kind.LeftFoot };
            Undo.RecordObject(e, "Add target");
            e.targets.Add(t);
            SnapToBone(e, g, t);
        }

        // Place the target at the chosen bone's current world pose at the target's frame.
        private void SnapToBone(KimodoEffectors e, KimodoGenerator g, KimodoEffectors.Target t)
        {
            var tgt = g.ResolvedTarget;
            if (tgt == null || !g.IsPreviewBound) return;
            float saved = g.PreviewTime;
            g.Preview.SampleFrame(g.clipIndex, Mathf.Clamp(t.frame, 0, g.FrameCount - 1));
            var bone = tgt.GetBoneTransform(KimodoEffectors.Bone(t.kind));
            if (bone != null)
            {
                Undo.RecordObject(e, "Snap target to bone");
                t.world = bone.position;
                t.rot = bone.rotation;
            }
            g.SampleTime(saved);
            SceneView.RepaintAll();
        }

        private void OnSceneGUI()
        {
            var e = Eff;
            var g = e.ResolvedGenerator;
            if (g == null || g.Motion == null) return;

            for (int i = 0; i < e.targets.Count; i++)
            {
                var t = e.targets[i];
                Color col = ColorFor(t.kind);
                float size = HandleUtility.GetHandleSize(t.world);

                Handles.color = col;
                Handles.SphereHandleCap(0, t.world, Quaternion.identity, size * 0.12f, EventType.Repaint);
                Handles.Label(t.world + Vector3.up * size * 0.3f, $"{t.kind} · f{t.frame}", EditorStyles.whiteMiniLabel);

                if (t.constrainRotation)
                {
                    // Show orientation axes + a rotation handle.
                    Handles.color = Color.red;   Handles.DrawAAPolyLine(2f, t.world, t.world + t.rot * Vector3.right * size * 0.3f);
                    Handles.color = Color.green; Handles.DrawAAPolyLine(2f, t.world, t.world + t.rot * Vector3.up * size * 0.3f);
                    Handles.color = Color.blue;  Handles.DrawAAPolyLine(2f, t.world, t.world + t.rot * Vector3.forward * size * 0.3f);

                    EditorGUI.BeginChangeCheck();
                    Quaternion nr = Handles.RotationHandle(t.rot, t.world);
                    if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(e, "Aim target"); t.rot = nr; EditorUtility.SetDirty(e); }
                }

                EditorGUI.BeginChangeCheck();
                Vector3 np = Handles.PositionHandle(t.world, t.constrainRotation ? t.rot : Quaternion.identity);
                if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(e, "Move target"); t.world = np; EditorUtility.SetDirty(e); }
            }
        }
    }
}
