// SPDX-License-Identifier: Apache-2.0
// Scene-view overlay holding the constraint editing tools, the way Unity's own tool overlays work:
// dock it anywhere in the Scene view (or hide it) from the overlay menu (the ⋮⋮ button / ` key).
//
// It shows the tools for the kind of key you have selected and nothing else — a waypoint offers Move
// and Face, a hand/foot key Limb and Aim, a full-body key Joints, Pose and On/Off. The state lives in
// KimodoEditState, so this, the inspectors and the Timeline window stay in sync.

using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;

namespace AminHP.KimodoBridge.Editor
{
    [Overlay(typeof(SceneView), OverlayId, "Kimodo Constraints", true)]
    public class KimodoConstraintOverlay : Overlay
    {
        public const string OverlayId = "kimodo-constraints";

        private IMGUIContainer _gui;

        public override VisualElement CreatePanelContent()
        {
            _gui = new IMGUIContainer(DrawGUI);
            _gui.style.minWidth = 230f;
            // -= first: CreatePanelContent runs again whenever the overlay is re-created (docked,
            // floated, collapsed), and a duplicate subscription would outlive the old container.
            KimodoEditState.Changed -= Refresh;
            KimodoEditState.Changed += Refresh;
            Selection.selectionChanged -= Refresh;
            Selection.selectionChanged += Refresh;
            return _gui;
        }

        public override void OnWillBeDestroyed()
        {
            KimodoEditState.Changed -= Refresh;
            Selection.selectionChanged -= Refresh;
            base.OnWillBeDestroyed();
        }

        private void Refresh() => _gui?.MarkDirtyRepaint();

        private static KimodoGenerator Generator()
        {
            var go = Selection.activeGameObject;
            return go != null ? go.GetComponentInParent<KimodoGenerator>() : null;
        }

        private void DrawGUI()
        {
            var g = Generator();
            if (g == null)
            {
                EditorGUILayout.LabelField("Select a Kimodo character.", EditorStyles.wordWrappedMiniLabel);
                return;
            }

            KimodoEditState.ResolveFollow(g);

            var kind = KimodoEditState.SelectedKind;
            var owner = KimodoEditState.Owner;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(KimodoEditState.DisplayName(kind), EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                EditorGUI.BeginChangeCheck();
                bool follow = GUILayout.Toggle(KimodoEditState.FollowPlayhead,
                    new GUIContent("⏱", "Follow the playhead: edit the key at the current frame."),
                    EditorStyles.miniButton, GUILayout.Width(24f));
                if (EditorGUI.EndChangeCheck()) KimodoEditState.FollowPlayhead = follow;
            }

            if (kind == KimodoKeyKind.None || owner == null)
            {
                EditorGUILayout.LabelField(
                    "No key selected. Scrub to a key, click one in the Timeline, or click its gizmo in the Scene.",
                    EditorStyles.wordWrappedMiniLabel);
                return;
            }

            // A kind with no tools (waypoints) just shows what it is + the key bar: its gizmo already
            // does everything, so there is nothing to switch between.
            if (KimodoEditState.ToolsFor(kind).Length > 0) DrawTools(kind);
            else EditorGUILayout.LabelField("Drag the dot to move it, the arrow tip to aim it.",
                                            EditorStyles.wordWrappedMiniLabel);
            DrawKeyBar(g, owner, kind);
        }

        private static void DrawTools(KimodoKeyKind kind)
        {
            var tools = KimodoEditState.ToolsFor(kind);
            var active = KimodoEditState.Tool(kind);

            using (new EditorGUILayout.HorizontalScope())
                foreach (var t in tools)
                {
                    bool on = active == t;
                    var prev = GUI.backgroundColor;
                    if (on) GUI.backgroundColor = new Color(0.45f, 0.75f, 1f);
                    if (GUILayout.Toggle(on, Content(t), EditorStyles.miniButton, GUILayout.Height(22f)) != on)
                        KimodoEditState.SetTool(kind, t);
                    GUI.backgroundColor = prev;
                }
            EditorGUILayout.LabelField(Hint(active), EditorStyles.wordWrappedMiniLabel);
        }

        private static GUIContent Content(KimodoTool t) => t switch
        {
            KimodoTool.MoveLimb => new GUIContent(" Limb", "Drag a hand/foot; the limb bends to reach it."),
            KimodoTool.AimLimb => new GUIContent(" Aim", "Rotate a hand/foot — its rotation is constrained too."),
            KimodoTool.RotateJoints => new GUIContent(" Joints", "Click any joint and rotate it (the demo's editing mode)."),
            KimodoTool.MovePose => new GUIContent(" Pose", "Drag the pelvis to move the whole pose, height included."),
            KimodoTool.ToggleJoints => new GUIContent(" On/Off", "Click a joint to include or exclude it from the constraint."),
            _ => GUIContent.none,
        };

        private static string Hint(KimodoTool t) => t switch
        {
            KimodoTool.MoveLimb => "Drag a hand/foot handle. Out of reach? Use Free root or a waypoint.",
            KimodoTool.AimLimb => "Rotate the hand/foot handle.",
            KimodoTool.RotateJoints => "Click a joint dot, then rotate it.",
            KimodoTool.MovePose => "Drag the pelvis handle — green/up sets height.",
            KimodoTool.ToggleJoints => "Click joints: green = constrained, red = free.",
            _ => "",
        };

        // ---- key navigation -------------------------------------------------------------------
        private static void DrawKeyBar(KimodoGenerator g, Component owner, KimodoKeyKind kind)
        {
            int count = KimodoEditState.KeyCount(owner);
            int sel = KimodoEditState.IndexFor(owner, count);

            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(count == 0))
            {
                if (GUILayout.Button(new GUIContent("◀", "Previous key"), EditorStyles.miniButtonLeft, GUILayout.Width(24f)))
                    Step(g, owner, count, sel, -1);
                string label = count == 0 ? "no keys"
                             : sel < 0 ? $"{count} key(s) — none selected"
                             : $"key {sel + 1}/{count} @ frame {KimodoEditState.KeyFrame(owner, sel)}";
                GUILayout.Label(label, EditorStyles.miniLabel, GUILayout.MinWidth(110f));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("▶", "Next key"), EditorStyles.miniButtonRight, GUILayout.Width(24f)))
                    Step(g, owner, count, sel, 1);
            }
        }

        // Step to the next/previous key in FRAME order and put the playhead on it, so ◀ ▶ walks the
        // shot rather than the (arbitrary) order the keys were added in.
        private static void Step(KimodoGenerator g, Component owner, int count, int sel, int dir)
        {
            if (count == 0) return;
            var order = new System.Collections.Generic.List<int>();
            for (int i = 0; i < count; i++) order.Add(i);
            order.Sort((a, b) => KimodoEditState.KeyFrame(owner, a).CompareTo(KimodoEditState.KeyFrame(owner, b)));
            int at = order.IndexOf(sel);
            int next = at < 0 ? (dir > 0 ? 0 : count - 1) : Mathf.Clamp(at + dir, 0, count - 1);
            int idx = order[next];

            // Move the playhead first, then announce: RaiseChanged is what repaints the Timeline
            // window, and it has to see the new PreviewTime. It is raised unconditionally because
            // Select is a no-op when the key was already selected (stepping past the last one), and
            // the playhead would then look stuck.
            g.Playing = false;
            if (g.Fps > 0f) g.SampleTime(KimodoEditState.KeyFrame(owner, idx) / g.Fps);
            KimodoEditState.Select(owner, idx);
            KimodoEditState.RaiseChanged();
        }
    }
}
