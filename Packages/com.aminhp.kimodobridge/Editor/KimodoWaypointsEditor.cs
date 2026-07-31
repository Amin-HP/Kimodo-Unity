// SPDX-License-Identifier: Apache-2.0
// Inspector + Scene gizmos for KimodoWaypoints. Draws the character's current root path
// (a polyline of the pelvis over the motion — a direct check that the Unity↔Kimodo mapping
// is right) and each waypoint as a movable ground handle routed to its frame on the path.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AminHP.KimodoBridge.Editor
{
    [CustomEditor(typeof(KimodoWaypoints))]
    public class KimodoWaypointsEditor : UnityEditor.Editor
    {
        private KimodoWaypoints Wp => (KimodoWaypoints)target;

        private Vector3[] _pathWorld;   // pelvis world position per frame (index = frame)
        private int _sig = int.MinValue;

        private static readonly Color PathColor = new Color(0.35f, 0.7f, 1f, 0.95f);
        private static readonly Color WpColor = new Color(1f, 0.6f, 0.2f, 1f);

        public override void OnInspectorGUI()
        {
            var w = Wp;
            var g = w.ResolvedGenerator;
            if (g == null) { EditorGUILayout.HelpBox("Needs a KimodoGenerator on the same GameObject.", MessageType.Warning); return; }
            var tgt = g.ResolvedTarget;
            if (tgt == null || tgt.avatar == null || !tgt.avatar.isHuman)
            { EditorGUILayout.HelpBox("Assign a Humanoid character on the Generator.", MessageType.Warning); return; }

            EditorGUILayout.LabelField("Root path (waypoints)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Add a waypoint, then drag its handle on the ground to route the pelvis through it at that " +
                "frame. The blue line is the character's current root path (after a generate).",
                EditorStyles.wordWrappedMiniLabel);
            if (g.Motion == null)
                EditorGUILayout.HelpBox("No motion yet — you can place waypoints now and they'll apply on the first " +
                    "Generate (placement is approximate until a motion refines the mapping).", MessageType.Info);

            // Display settings (editor-only visuals).
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                float gy = EditorGUILayout.FloatField(new GUIContent("Ground Y", "Height the route + markers are drawn at."), w.groundY);
                float mr = EditorGUILayout.FloatField(new GUIContent("Radius", "Waypoint disc radius (metres)."), w.markerRadius);
                if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(w, "Edit waypoint display"); w.groundY = gy; w.markerRadius = Mathf.Max(0f, mr); }
                using (new EditorGUI.DisabledScope(!g.IsPreviewBound))
                    if (GUILayout.Button("Snap to feet", GUILayout.Width(90)))
                    {
                        float fy = w.EstimateGroundY();
                        if (!float.IsNaN(fy)) { Undo.RecordObject(w, "Snap ground to feet"); w.groundY = fy; SceneView.RepaintAll(); }
                    }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Frame: {g.CurrentFrame} / {Mathf.Max(0, g.AuthoringFrameCount - 1)}", EditorStyles.miniLabel);
                if (GUILayout.Button($"＋ Add @ {g.CurrentFrame}", GUILayout.Width(120)))
                    AddAtCurrent(w, g);
                using (new EditorGUI.DisabledScope(w.waypoints.Count == 0))
                    if (GUILayout.Button("Clear", GUILayout.Width(60)))
                    {
                        Undo.RecordObject(w, "Clear waypoints");
                        w.waypoints.Clear();
                    }
            }

            int removeAt = -1;
            for (int i = 0; i < w.waypoints.Count; i++)
            {
                var wp = w.waypoints[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        int frame = Mathf.Clamp(EditorGUILayout.IntField("Frame", wp.frame), 0, Mathf.Max(0, g.AuthoringFrameCount - 1));
                        if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(w, "Edit waypoint frame"); wp.frame = frame; }

                        if (GUILayout.Button("Go", GUILayout.Width(34)))
                        {
                            g.Playing = false;
                            g.SampleTime(g.Fps > 0f ? wp.frame / g.Fps : 0f);
                            SceneView.RepaintAll();
                        }
                        if (GUILayout.Button("✕", GUILayout.Width(24))) removeAt = i;
                    }
                    EditorGUI.BeginChangeCheck();
                    Vector3 world = EditorGUILayout.Vector3Field("World", wp.world);
                    if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(w, "Edit waypoint position"); wp.world = world; }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        bool cf = EditorGUILayout.ToggleLeft(new GUIContent("Constrain facing", "Force the character to face the arrow direction here."), wp.constrainFacing, GUILayout.Width(130));
                        float hd;
                        using (new EditorGUI.DisabledScope(!cf))
                            hd = EditorGUILayout.FloatField(new GUIContent("Yaw°", "Facing angle in world degrees (0 = +Z)."), wp.headingDeg);
                        if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(w, "Edit waypoint facing"); wp.constrainFacing = cf; wp.headingDeg = hd; }
                    }
                }
            }
            if (removeAt >= 0)
            {
                Undo.RecordObject(w, "Remove waypoint");
                w.waypoints.RemoveAt(removeAt);
            }

            if (w.waypoints.Count > 0)
                EditorGUILayout.LabelField($"{w.waypoints.Count} waypoint(s) sent as a root2d path on next Generate.", EditorStyles.miniLabel);

            if (GUI.changed) SceneView.RepaintAll();
        }

        private void AddAtCurrent(KimodoWaypoints w, KimodoGenerator g)
        {
            var tgt = g.ResolvedTarget;
            var hips = tgt != null ? tgt.GetBoneTransform(HumanBodyBones.Hips) : null;
            Vector3 pos = hips != null ? hips.position : (tgt != null ? tgt.transform.position : Vector3.zero);
            pos = w.OnGround(pos);   // drop the new waypoint onto the display ground
            foreach (var wp in w.waypoints)
                if (wp.frame == g.CurrentFrame) return; // one per frame
            Undo.RecordObject(w, "Add waypoint");
            w.waypoints.Add(new KimodoWaypoints.Waypoint { frame = g.CurrentFrame, world = pos });
            KimodoEditState.Select(w, w.waypoints.Count - 1);
        }

        // ---------------------------------------------------------------
        private void OnSceneGUI()
        {
            var w = Wp;
            var g = w.ResolvedGenerator;
            if (g == null || g.ResolvedTarget == null) return;

            // The pelvis route is only available once a motion has been generated; the waypoint handles
            // themselves are world positions and can be placed before that.
            bool hasMotion = g.Motion != null && g.IsPreviewBound;
            if (hasMotion)
            {
                RefreshPathIfStale(w, g);
                if (_pathWorld != null && _pathWorld.Length > 1)
                {
                    var ground = new Vector3[_pathWorld.Length];
                    for (int f = 0; f < _pathWorld.Length; f++) ground[f] = w.OnGround(_pathWorld[f]);
                    Handles.color = PathColor;
                    Handles.DrawAAPolyLine(4f, ground);
                }
            }
            else _pathWorld = null;

            // Authored pathway: connect waypoints in frame order (the route the user is drawing).
            DrawAuthoredPathway(w);

            // Waypoints keep their own combined gizmo — ground dot to move, arrow tip to aim — always
            // live on every waypoint. No tool modes: this is the part that already worked well.
            KimodoEditState.ResolveFollow(g);
            int selIndex = KimodoEditState.IndexFor(w, w.waypoints.Count);

            for (int i = 0; i < w.waypoints.Count; i++)
            {
                var wp = w.waypoints[i];
                bool selected = i == selIndex;
                Vector3 groundPos = w.OnGround(wp.world);
                float size = HandleUtility.GetHandleSize(groundPos);

                // Demo-style marker: ground disc (radius) + heading arrow (angle) + label.
                Handles.color = WpColor;
                Handles.DrawWireDisc(groundPos, Vector3.up, w.markerRadius);
                Handles.color = new Color(WpColor.r, WpColor.g, WpColor.b, 0.12f);
                Handles.DrawSolidDisc(groundPos, Vector3.up, w.markerRadius);

                // Facing arrow: user-set heading when constrained, else the path tangent (auto).
                Vector3 dir = wp.constrainFacing
                    ? Quaternion.Euler(0f, wp.headingDeg, 0f) * Vector3.forward
                    : HeadingAt(wp.frame);
                dir.y = 0f;
                dir = dir.sqrMagnitude < 1e-6f ? Vector3.forward : dir.normalized;

                Handles.color = wp.constrainFacing ? WpColor : new Color(WpColor.r, WpColor.g, WpColor.b, 0.5f);
                Vector3 tip = groundPos + dir * (w.markerRadius * 1.8f);
                Handles.DrawAAPolyLine(3f, groundPos, tip);
                Handles.ConeHandleCap(0, tip, Quaternion.LookRotation(dir, Vector3.up), size * 0.08f, EventType.Repaint);

                // Drag the arrow tip to rotate the waypoint's facing (enables the facing constraint).
                EditorGUI.BeginChangeCheck();
                Vector3 newTip = Handles.FreeMoveHandle(tip, size * 0.09f, Vector3.zero, Handles.CircleHandleCap);
                if (EditorGUI.EndChangeCheck())
                {
                    Vector3 nd = newTip - groundPos; nd.y = 0f;
                    if (nd.sqrMagnitude > 1e-6f)
                    {
                        KimodoEditState.Select(w, i);
                        Undo.RecordObject(w, "Aim waypoint");
                        wp.headingDeg = Vector3.SignedAngle(Vector3.forward, nd.normalized, Vector3.up);
                        wp.constrainFacing = true;
                        EditorUtility.SetDirty(w);
                    }
                }

                // If the waypoint was lifted off the ground, show a drop line + its sphere.
                if (Mathf.Abs(wp.world.y - w.groundY) > 1e-3f)
                {
                    Handles.color = new Color(WpColor.r, WpColor.g, WpColor.b, 0.5f);
                    Handles.DrawDottedLine(wp.world, groundPos, 2f);
                    Handles.SphereHandleCap(0, wp.world, Quaternion.identity, size * 0.1f, EventType.Repaint);
                }

                // Line to where the pelvis currently is at this frame (the correction the route asks for).
                if (_pathWorld != null && wp.frame >= 0 && wp.frame < _pathWorld.Length)
                {
                    Handles.color = new Color(1f, 1f, 1f, 0.35f);
                    Handles.DrawDottedLine(groundPos, w.OnGround(_pathWorld[wp.frame]), 3f);
                }

                // Ground grab: a single dot that moves the waypoint on the X/Z plane only (no Y axis,
                // no 3-axis gizmo). Yaw is set by the arrow-tip handle above. Under the Face tool it
                // becomes a plain dot that selects the waypoint instead.
                Handles.color = selected ? Color.yellow : WpColor;
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.Slider2D(groundPos, Vector3.up, Vector3.right, Vector3.forward,
                    size * 0.16f, Handles.SphereHandleCap, Vector2.zero);
                if (EditorGUI.EndChangeCheck())
                {
                    KimodoEditState.Select(w, i);
                    Undo.RecordObject(w, "Move waypoint");
                    wp.world = new Vector3(moved.x, wp.world.y, moved.z);  // keep the waypoint's own Y
                    EditorUtility.SetDirty(w);
                }
            }
        }

        // Dotted route through the waypoints in frame order.
        private void DrawAuthoredPathway(KimodoWaypoints w)
        {
            if (w.waypoints.Count < 2) return;
            var ordered = new List<KimodoWaypoints.Waypoint>(w.waypoints);
            ordered.Sort((a, b) => a.frame.CompareTo(b.frame));
            Handles.color = new Color(WpColor.r, WpColor.g, WpColor.b, 0.7f);
            for (int i = 1; i < ordered.Count; i++)
                Handles.DrawDottedLine(w.OnGround(ordered[i - 1].world), w.OnGround(ordered[i].world), 4f);
        }

        // Ground heading (path tangent) at a frame, from the cached pelvis path.
        private Vector3 HeadingAt(int frame)
        {
            if (_pathWorld == null || _pathWorld.Length < 2) return Vector3.forward;
            int a = Mathf.Clamp(frame - 2, 0, _pathWorld.Length - 1);
            int b = Mathf.Clamp(frame + 2, 0, _pathWorld.Length - 1);
            Vector3 d = _pathWorld[b] - _pathWorld[a];
            d.y = 0f;
            return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
        }

        // Pose every frame once (on change) to capture the pelvis world path.
        private void RefreshPathIfStale(KimodoWaypoints w, KimodoGenerator g)
        {
            int sig = Signature(g);
            if (sig == _sig) return;
            _sig = sig;

            var tgt = g.ResolvedTarget;
            var hips = tgt != null ? tgt.GetBoneTransform(HumanBodyBones.Hips) : null;
            if (hips == null) { _pathWorld = null; return; }

            int T = g.FrameCount;
            _pathWorld = new Vector3[T];
            float saved = g.PreviewTime;
            for (int f = 0; f < T; f++)
            {
                g.Preview.SampleFrame(g.clipIndex, f);
                _pathWorld[f] = hips.position;
            }
            g.SampleTime(saved);
        }

        private static int Signature(KimodoGenerator g)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (g.Motion != null ? g.Motion.GetHashCode() : 0);
                h = h * 31 + g.clipIndex;
                h = h * 31 + g.rootMotionScale.GetHashCode();
                return h;
            }
        }
    }
}
