// SPDX-License-Identifier: Apache-2.0
// In-scene full-body pose editor for KimodoPoseConstraints. Each key is drawn as its own "ghost"
// skeleton at its frame (via FK of the stored Kimodo pose, placed in the world with the root affine
// — no touching the character). Several can be shown at once to see every constraint. Select a key's
// joint and rotate it to author the pose. Default: shown.

using UnityEditor;
using UnityEngine;

namespace AminHP.KimodoBridge.Editor
{
    [CustomEditor(typeof(KimodoPoseConstraints))]
    public class KimodoPoseConstraintsEditor : UnityEditor.Editor
    {
        private KimodoPoseConstraints Pc => (KimodoPoseConstraints)target;

        // Body joints (SOMA names) exposed as draggable dots/lines — fingers etc. keep their pose.
        // Shared with the runtime constraint builder so the two never diverge.
        private static readonly string[] BodyJoints = KimodoPoseConstraints.BodyJointNames;

        private int _selKey = -1, _selJoint = -1;
        private bool _activateMode;           // clicking a joint toggles its active state (subtree)
        private int[] _bodyIdx;               // BodyJoints -> index into the 77 joints
        private KimodoRootMap.Map _map;
        private int _mapSig = int.MinValue, _bodyMotionHash;
        private KimodoPoseGhosts _ghosts;     // transparent mesh copies at each shown key's pose

        private void OnDisable() { _ghosts?.Dispose(); _ghosts = null; }

        // raw-Kimodo Quaternion <-> Unity (single-axis flip); self-inverse.
        private static Quaternion FlipQ(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);

        // ---------------------------------------------------------------
        public override void OnInspectorGUI()
        {
            var p = Pc;
            var g = p.ResolvedGenerator;
            if (g == null) { EditorGUILayout.HelpBox("Needs a KimodoGenerator on the same GameObject.", MessageType.Warning); return; }
            if (g.Motion == null) { EditorGUILayout.HelpBox("Generate a motion on the Generator first.", MessageType.None); return; }

            EditorGUILayout.LabelField("Whole-rig pose keyframes", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Each key shows its pose as a skeleton at its frame. Select a key, click a joint and rotate it; " +
                "drag the pelvis handle to move the whole pose — green/up sets HEIGHT (e.g. onto a box). " +
                "'Align to frame' resets a key to the motion's pose there.",
                EditorStyles.wordWrappedMiniLabel);
            if (!g.IsPreviewBound)
                EditorGUILayout.HelpBox("Assign a Humanoid Target and Generate to place the pose skeletons.", MessageType.Info);

            EditorGUI.BeginChangeCheck();
            bool ghost = EditorGUILayout.ToggleLeft(
                "Show ghost mesh (transparent model at each shown key's pose)", p.showGhostMesh);
            float op = p.ghostOpacity;
            if (ghost)
                op = EditorGUILayout.Slider("Ghost transparency", p.ghostOpacity, 0f, 1f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(p, "Edit ghost mesh");
                p.showGhostMesh = ghost; p.ghostOpacity = op;
                if (!ghost) { _ghosts?.Dispose(); _ghosts = null; }
                SceneView.RepaintAll();
            }

            EditorGUI.BeginChangeCheck();
            bool act = GUILayout.Toggle(_activateMode,
                _activateMode ? "● Activating joints — click a joint to toggle just that joint (green=on, red=off)"
                              : "Activate/deactivate joints (click toggles one joint; use All/None/Upper/Lower for bulk)", "Button");
            if (EditorGUI.EndChangeCheck()) { _activateMode = act; SceneView.RepaintAll(); }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Preview frame: {g.CurrentFrame} / {Mathf.Max(0, g.FrameCount - 1)}", EditorStyles.miniLabel);
                if (GUILayout.Button($"＋ Add @ {g.CurrentFrame}", GUILayout.Width(120)))
                {
                    Undo.RecordObject(p, "Add pose key");
                    var nk = new KimodoPoseConstraints.Key { frame = g.CurrentFrame, show = true };
                    InitKeyFromMotion(nk, g);
                    p.keys.Add(nk);
                    _selKey = p.keys.Count - 1; _selJoint = -1;
                }
                using (new EditorGUI.DisabledScope(p.keys.Count == 0))
                    if (GUILayout.Button("Clear", GUILayout.Width(60)))
                    { Undo.RecordObject(p, "Clear pose keys"); p.keys.Clear(); _selKey = -1; }
            }

            int removeAt = -1;
            for (int i = 0; i < p.keys.Count; i++)
            {
                var k = p.keys[i];
                bool sel = _selKey == i;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        bool show = GUILayout.Toggle(k.show, "Show", "Button", GUILayout.Width(52));
                        int frame = Mathf.Clamp(EditorGUILayout.IntField("Frame", k.frame), 0, Mathf.Max(0, g.FrameCount - 1));
                        if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(p, "Edit pose key"); k.show = show; k.frame = frame; }

                        if (GUILayout.Button(sel ? "◉" : "○", GUILayout.Width(26))) { _selKey = sel ? -1 : i; _selJoint = -1; SceneView.RepaintAll(); }
                        if (GUILayout.Button("Go", GUILayout.Width(30)))
                        { g.Playing = false; g.SampleTime(g.Fps > 0f ? k.frame / g.Fps : 0f); SceneView.RepaintAll(); }
                        if (GUILayout.Button("✕", GUILayout.Width(22))) removeAt = i;
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Align to frame")) { InitKeyFromMotion(k, g); SceneView.RepaintAll(); }
                        EditorGUILayout.LabelField(sel && _selJoint >= 0 ? $"Selected joint: {g.Motion.bones[_selJoint].name}" : (sel ? "Click a joint in the Scene." : ""), EditorStyles.miniLabel);
                    }
                    if (sel)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField("Active:", GUILayout.Width(46));
                            if (GUILayout.Button("All")) { SetRegion(k, g, Region.All); }
                            if (GUILayout.Button("None")) { SetRegion(k, g, Region.None); }
                            if (GUILayout.Button("Upper")) { SetRegion(k, g, Region.Upper); }
                            if (GUILayout.Button("Lower")) { SetRegion(k, g, Region.Lower); }
                            int active = CountActiveBody(k, g.Motion);
                            EditorGUILayout.LabelField(KimodoPoseConstraints.HasInactive(k)
                                ? $"{active}/{BodyJoints.Length} joints constrained" : "whole body",
                                EditorStyles.miniLabel);
                        }
                    }
                }
            }
            if (removeAt >= 0) { Undo.RecordObject(p, "Remove pose key"); p.keys.RemoveAt(removeAt); if (_selKey >= p.keys.Count) _selKey = -1; }

            if (p.keys.Count > 0)
                EditorGUILayout.LabelField($"{p.keys.Count} whole-rig pose(s) sent on next Generate.", EditorStyles.miniLabel);
        }

        private void InitKeyFromMotion(KimodoPoseConstraints.Key k, KimodoGenerator g)
        {
            var motion = g.Motion; int J = motion.jointCount;
            var clip = motion.clips[Mathf.Clamp(g.clipIndex, 0, motion.clips.Count - 1)];
            int f = Mathf.Clamp(k.frame, 0, motion.frameCount - 1);
            Undo.RecordObject(Pc, "Align pose to frame");
            k.localQuats = new float[J * 4];
            System.Array.Copy(clip.localQuats, f * J * 4, k.localQuats, 0, J * 4);
            k.root = new Vector3(clip.rootPositions[f * 3], clip.rootPositions[f * 3 + 1], clip.rootPositions[f * 3 + 2]);
            k.hasPose = true;
        }

        // ---- joint activation --------------------------------------------------------------
        private enum Region { All, None, Upper, Lower }

        private void SetRegion(KimodoPoseConstraints.Key k, KimodoGenerator g, Region r)
        {
            var motion = g.Motion; int J = motion.jointCount;
            Undo.RecordObject(Pc, "Set active region");
            if (r == Region.All) { k.jointActive = null; EditorUtility.SetDirty(Pc); SceneView.RepaintAll(); return; }
            var active = new bool[J];
            for (int i = 0; i < J; i++) active[i] = r != Region.None;   // None = all off, else start all on
            if (r == Region.Upper) // keep the upper body: drop both leg chains.
            {
                SetSubtree(active, motion, KimodoFK.BoneIndex(motion, "LeftLeg"), false);
                SetSubtree(active, motion, KimodoFK.BoneIndex(motion, "RightLeg"), false);
            }
            else if (r == Region.Lower) // keep the lower body: drop everything above the pelvis.
            {
                SetSubtree(active, motion, KimodoFK.BoneIndex(motion, "Spine1"), false);
            }
            // Region.None: leave everything off.
            k.jointActive = active;
            EditorUtility.SetDirty(Pc);
            SceneView.RepaintAll();
        }

        // Toggle EXACTLY one joint (independent, not a chain), so you can e.g. deactivate everything
        // except a hand. Use the region buttons for bulk changes.
        private void ToggleJoint(KimodoPoseConstraints.Key k, KimodoMotion motion, int j)
        {
            Undo.RecordObject(Pc, "Toggle joint");
            EnsureJointActive(k, motion.jointCount);
            k.jointActive[j] = !k.jointActive[j];
            if (!KimodoPoseConstraints.HasInactive(k)) k.jointActive = null; // all on -> whole body
            EditorUtility.SetDirty(Pc);
        }

        private static void EnsureJointActive(KimodoPoseConstraints.Key k, int J)
        {
            if (k.jointActive != null && k.jointActive.Length == J) return;
            var a = new bool[J];
            for (int i = 0; i < J; i++) a[i] = k.jointActive == null || i >= k.jointActive.Length || k.jointActive[i];
            k.jointActive = a;
        }

        private static void SetSubtree(bool[] active, KimodoMotion motion, int root, bool value)
        {
            if (root < 0) return;
            for (int j = 0; j < motion.bones.Count && j < active.Length; j++)
                if (IsDescendantOf(motion, j, root)) active[j] = value;
        }

        private static bool IsDescendantOf(KimodoMotion motion, int j, int root)
        {
            int t = j, guard = 0;
            while (t >= 0 && guard++ < 200) { if (t == root) return true; t = motion.bones[t].parent; }
            return false;
        }

        private static bool IsActive(KimodoPoseConstraints.Key k, int j) =>
            k.jointActive == null || j < 0 || j >= k.jointActive.Length || k.jointActive[j];

        private int CountActiveBody(KimodoPoseConstraints.Key k, KimodoMotion motion)
        {
            if (k.jointActive == null) return BodyJoints.Length;
            int c = 0;
            foreach (var name in BodyJoints)
            {
                int j = KimodoFK.BoneIndex(motion, name);
                if (j >= 0 && j < k.jointActive.Length && k.jointActive[j]) c++;
            }
            return c;
        }

        // ---------------------------------------------------------------
        private void EnsureMapAndBodyIdx(KimodoGenerator g)
        {
            var motion = g.Motion;
            if (_bodyIdx == null || _bodyMotionHash != motion.GetHashCode())
            {
                _bodyMotionHash = motion.GetHashCode();
                _bodyIdx = new int[BodyJoints.Length];
                for (int b = 0; b < BodyJoints.Length; b++) _bodyIdx[b] = KimodoFK.BoneIndex(motion, BodyJoints[b]);
            }
            int sig = motion.GetHashCode() * 31 + g.clipIndex;
            sig = sig * 31 + g.rootMotionScale.GetHashCode();
            if (sig != _mapSig) { _map = KimodoRootMap.Compute(g); _mapSig = sig; }
        }

        private void OnSceneGUI()
        {
            var p = Pc; var g = p.ResolvedGenerator;
            if (g == null || g.Motion == null || !g.IsPreviewBound) return;

            // Make sure every shown key has an authored pose (so both the skeleton and the ghost
            // mesh have something to draw), then update the transparent ghost copies.
            foreach (var k in p.keys)
                if (k.show && (!k.hasPose || k.localQuats == null || k.localQuats.Length != g.Motion.jointCount * 4))
                    InitKeyFromMotion(k, g);
            if (p.showGhostMesh) { (_ghosts ??= new KimodoPoseGhosts()).Sync(p, g); }
            else if (_ghosts != null) { _ghosts.Dispose(); _ghosts = null; }

            EnsureMapAndBodyIdx(g);
            if (!_map.valid) return;
            var motion = g.Motion;

            var prevZ = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;

            for (int ki = 0; ki < p.keys.Count; ki++)
            {
                var k = p.keys[ki];
                if (!k.show) continue;
                if (!k.hasPose || k.localQuats == null || k.localQuats.Length != motion.jointCount * 4) InitKeyFromMotion(k, g);

                DrawKey(p, g, motion, ki, k, _selKey == ki);
            }

            Handles.zTest = prevZ;
        }

        private void DrawKey(KimodoPoseConstraints p, KimodoGenerator g, KimodoMotion motion, int ki, KimodoPoseConstraints.Key k, bool selected)
        {
            // FK the stored pose -> Kimodo global positions/rotations -> world.
            var kd = MakeClipView(motion, k);
            KimodoFK.GlobalPose(motion, kd, 0, out var gpos, out var grot);
            Vector3 W(int j) => KimodoRootMap.KimodoToWorld(gpos[j], _map);

            // Bones between body joints (deactivated joints drawn dimmed).
            for (int b = 0; b < _bodyIdx.Length; b++)
            {
                int j = _bodyIdx[b]; if (j < 0) continue;
                int par = motion.bones[j].parent;
                if (par < 0) continue;
                Handles.color = IsActive(k, j)
                    ? (selected ? new Color(0.4f, 0.9f, 1f, 1f) : new Color(0.5f, 0.7f, 0.9f, 0.6f))
                    : new Color(0.5f, 0.5f, 0.5f, selected ? 0.35f : 0.2f);
                Handles.DrawAAPolyLine(selected ? 6f : 3f, W(par), W(j));
            }

            // Joint dots. In activate mode a click toggles the joint (+ its chain); otherwise it
            // selects the joint for rotation. Green = active, red = deactivated.
            for (int b = 0; b < _bodyIdx.Length; b++)
            {
                int j = _bodyIdx[b]; if (j < 0) continue;
                Vector3 pos = W(j);
                float s = HandleUtility.GetHandleSize(pos);
                bool on = IsActive(k, j);
                bool js = selected && _selJoint == j;
                if (_activateMode)
                    Handles.color = on ? new Color(0.3f, 1f, 0.4f, selected ? 1f : 0.6f)
                                       : new Color(1f, 0.3f, 0.3f, selected ? 1f : 0.5f);
                else
                    Handles.color = js ? Color.yellow
                                  : on ? (selected ? new Color(1f, 0.6f, 0.2f, 1f) : new Color(1f, 0.6f, 0.2f, 0.5f))
                                       : new Color(0.5f, 0.5f, 0.5f, selected ? 0.5f : 0.3f);
                if (selected)
                {
                    if (Handles.Button(pos, Quaternion.identity, s * 0.09f, s * 0.13f, Handles.SphereHandleCap))
                    {
                        if (_activateMode) { ToggleJoint(k, motion, j); SceneView.RepaintAll(); }
                        else _selJoint = j;
                        Repaint();
                    }
                }
                else Handles.SphereHandleCap(0, pos, Quaternion.identity, s * 0.06f, EventType.Repaint);
            }

            // Move the whole pose (root position, incl. HEIGHT) — this is how you lift a pose in Y,
            // e.g. onto a box. World-axis handle: green = up. Waypoints only move on the ground (X/Z).
            if (selected && !_activateMode)
            {
                Vector3 rootW = W(motion.rootIndex);
                EditorGUI.BeginChangeCheck();
                Vector3 newRootW = Handles.PositionHandle(rootW, Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(p, "Move pose root");
                    k.root = KimodoRootMap.WorldToKimodo(newRootW, _map);
                    k.hasPose = true;
                    EditorUtility.SetDirty(p);
                }
            }

            // Rotate the selected joint (world gizmo -> Kimodo local rotation). The root uses the
            // move handle above instead, so skip it here to avoid two overlapping gizmos at the pelvis.
            if (selected && !_activateMode && _selJoint >= 0 && _selJoint != motion.rootIndex)
            {
                int j = _selJoint;
                Quaternion worldRot = _map.charRot * FlipQ(grot[j]);
                Vector3 pos = W(j);
                EditorGUI.BeginChangeCheck();
                Quaternion nr = Handles.RotationHandle(worldRot, pos);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(p, "Rotate joint");
                    Quaternion newKimodoGlobal = FlipQ(Quaternion.Inverse(_map.charRot) * nr);
                    int par = motion.bones[j].parent;
                    Quaternion parentGlobal = par >= 0 ? grot[par] : Quaternion.identity;
                    Quaternion localRaw = Quaternion.Inverse(parentGlobal) * newKimodoGlobal;
                    k.localQuats[j * 4] = localRaw.w; k.localQuats[j * 4 + 1] = localRaw.x;
                    k.localQuats[j * 4 + 2] = localRaw.y; k.localQuats[j * 4 + 3] = localRaw.z;
                    k.hasPose = true;
                    EditorUtility.SetDirty(p);
                }
            }
        }

        // A one-frame KimodoClip view over the key's stored pose, so KimodoFK can FK it.
        private static KimodoClip MakeClipView(KimodoMotion motion, KimodoPoseConstraints.Key k)
        {
            return new KimodoClip
            {
                localQuats = k.localQuats,
                rootPositions = new[] { k.root.x, k.root.y, k.root.z },
            };
        }
    }
}
