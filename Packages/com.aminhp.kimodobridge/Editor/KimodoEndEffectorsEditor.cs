// SPDX-License-Identifier: Apache-2.0
// Scene view + inspector for KimodoEndEffectors, built to look and behave like Kimodo's demo viewer.
//
// The demo draws an end-effector keyframe as a ghost SKELETON of the whole pose, with the joints and
// bones that are actually constrained (the limb chain + the root) in red and the rest in light grey,
// plus an axes gizmo on each effector to show its constrained rotation
// (kimodo/viz/constraint_ui.py, EEJointsKeyframeSet.create_scene_elements). This mirrors that: the
// skeleton is always drawn in full. The ghost MESH is what gets narrowed to the pinned limbs (see
// KimodoEndEffectors.GhostChain), so a hand key shows an arm instead of a whole extra body.
//
// Nothing here writes text into the Scene: the wording lives in the overlay and the inspector.
//
// THREE RULES this file follows, each fixing a bug that showed up in use:
//  1. The active tool and the selected key live in KimodoEditState (static), never on this editor —
//     the Inspector can rebuild the editor mid-edit, which used to wipe them, so the gizmo silently
//     stopped responding after a couple of drags.
//  2. While a handle is being dragged it stays anchored to the DRAG value, not to the pose it just
//     produced. Re-anchoring a handle to an IK result that clamps at the limb's reach feeds the drag
//     back into itself — that is what made moving a limb jitter and "sometimes not move".
//  3. Decoration (bones, dots, labels) is drawn in one pass after the handles and only on Repaint,
//     so the number and order of handle control IDs never changes between the events of a drag.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AminHP.KimodoBridge.Editor
{
    [CustomEditor(typeof(KimodoEndEffectors))]
    public class KimodoEndEffectorsEditor : UnityEditor.Editor
    {
        private KimodoEndEffectors Eff => (KimodoEndEffectors)target;

        // Demo palette: constrained joints/bones red, everything else light grey.
        private static readonly Color Constrained = new Color(1f, 0.15f, 0.15f);
        private static readonly Color Loose = new Color(0.86f, 0.86f, 0.86f);

        private static readonly string[] BodyJoints = KimodoPoseConstraints.BodyJointNames;

        private int[] _bodyIdx;                 // BodyJoints -> joint index
        private int _bodyMotionHash;
        private KimodoRootMap.Map _map;
        private int _mapSig = int.MinValue;
        private KimodoPoseGhosts _ghosts;

        // Live drag (rule 2). Anchored in world space; released as soon as no handle is hot.
        private bool _dragging;
        private int _dragJoint = -1;
        private Vector3 _dragPos;
        private Quaternion _dragRot;

        private static KimodoTool Tool => KimodoEditState.Tool(KimodoKeyKind.EndEffector);

        /// <summary>Where the handle is drawn from: the live drag value while this joint is being
        /// dragged, otherwise where the joint actually is. A non-finite drag value — a degenerate IK
        /// solve can produce one — is discarded instead of being fed back into the handle, where it
        /// would put the gizmo at an unreachable distance and blow up its screen size.</summary>
        private Vector3 DragAnchor(int joint, Vector3 fallback)
        {
            if (!_dragging || _dragJoint != joint) return fallback;
            if (!IsFinite(_dragPos)) { _dragging = false; return fallback; }
            // A limb reaches under a metre, so an anchor tens of metres from the joint is not a big
            // drag — it is the handle compounding its own output. Bound it rather than let it run off
            // toward the horizon, where the handle is drawn ever larger.
            if ((_dragPos - fallback).sqrMagnitude > 100f) { _dragging = false; return fallback; }
            return _dragPos;
        }

        private static bool IsFinite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
            !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

        private void OnDisable() { _ghosts?.Dispose(); _ghosts = null; }

        // raw-Kimodo Quaternion <-> Unity (single-axis flip); self-inverse.
        private static Quaternion FlipQ(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);

        private static Color LimbColor(KimodoEndEffectors.Limb l) => l switch
        {
            KimodoEndEffectors.Limb.LeftHand => new Color(0.35f, 0.7f, 1f),
            KimodoEndEffectors.Limb.RightHand => new Color(0.2f, 0.5f, 1f),
            KimodoEndEffectors.Limb.LeftFoot => new Color(1f, 0.75f, 0.3f),
            KimodoEndEffectors.Limb.RightFoot => new Color(1f, 0.55f, 0.15f),
            _ => Color.white,
        };

        // =======================================================================================
        // Inspector — the list of keys. The TOOLS live in the Scene view's Kimodo Constraints overlay.
        // =======================================================================================
        public override void OnInspectorGUI()
        {
            var e = Eff;
            var g = e.ResolvedGenerator;
            if (g == null) { EditorGUILayout.HelpBox("Needs a KimodoGenerator on the same GameObject.", MessageType.Warning); return; }
            if (!g.HasAuthoringSkeleton)
            {
                EditorGUILayout.HelpBox(g.ResolvedBridge != null && g.ResolvedBridge.IsOnline
                    ? "Loading the skeleton…" : "Connect the KimodoBridge (or Generate once) to author hand/foot keys.",
                    MessageType.Info);
                return;
            }
            KimodoEditState.ResolveFollow(g);

            EditorGUILayout.LabelField("End-Effectors (hand / foot keys)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Each key is a whole-body pose at its frame, of which only the ticked limbs — and the " +
                "root — are pinned, exactly like the demo. Limb and Aim live in the Scene view's Kimodo " +
                "Constraints overlay. There is one key per frame, so adding a second limb at a frame " +
                "joins the key already there instead of fighting it.",
                EditorStyles.wordWrappedMiniLabel);
            if (g.Motion == null)
                EditorGUILayout.HelpBox("No motion yet — keys apply on the first Generate. New keys start from " +
                    "a rest pose until there is a motion to align to (placement is approximate until then).",
                    MessageType.Info);

            EditorGUI.BeginChangeCheck();
            bool follow = EditorGUILayout.ToggleLeft(
                new GUIContent("Edit the key at the current frame (follow the playhead)",
                               "Scrub, or click a key in the Timeline, and it becomes the editable one."),
                KimodoEditState.FollowPlayhead);
            if (EditorGUI.EndChangeCheck()) KimodoEditState.FollowPlayhead = follow;

            EditorGUI.BeginChangeCheck();
            bool ghostLimbs = EditorGUILayout.ToggleLeft(
                new GUIContent("Ghost only the pinned limbs' part of the body",
                               "An arm for a hand, the lower body for a foot — the torso and head fade out."),
                e.ghostOnlyPinnedLimbs);
            bool only = EditorGUILayout.ToggleLeft(
                new GUIContent("Show only the key at the current frame",
                               "The demo's 'show only current constraint' — hides the other keys entirely."),
                e.showOnlyCurrentFrame);
            bool keepRot = EditorGUILayout.ToggleLeft(
                new GUIContent("Keep the effector's orientation while dragging",
                               "A foot stays flat / a hand keeps facing the same way as the limb bends."),
                e.keepEffectorOrientation);
            bool ghost = EditorGUILayout.ToggleLeft("Show ghost mesh (transparent model at each key's pose)", e.showGhostMesh);
            float op = e.ghostOpacity;
            if (ghost) op = EditorGUILayout.Slider("Ghost transparency", e.ghostOpacity, 0f, 1f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(e, "Edit end-effector settings");
                e.ghostOnlyPinnedLimbs = ghostLimbs;
                e.showOnlyCurrentFrame = only; e.keepEffectorOrientation = keepRot;
                e.showGhostMesh = ghost; e.ghostOpacity = op;
                if (!ghost) { _ghosts?.Dispose(); _ghosts = null; }
                SceneView.RepaintAll();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Frame: {g.CurrentFrame} / {Mathf.Max(0, g.AuthoringFrameCount - 1)}", EditorStyles.miniLabel);
                if (GUILayout.Button($"＋ Add @ {g.CurrentFrame}", GUILayout.Width(120)))
                {
                    Undo.RecordObject(e, "Add hand/foot key");
                    KimodoEditState.Select(e, e.AddOrMergeKey(g.CurrentFrame, KimodoEndEffectors.Limb.LeftHand));
                }
                using (new EditorGUI.DisabledScope(e.targets.Count == 0))
                    if (GUILayout.Button("Clear", GUILayout.Width(60)))
                    { Undo.RecordObject(e, "Clear hand/foot keys"); e.targets.Clear(); KimodoEditState.Select(e, -1); }
            }

            int selected = KimodoEditState.IndexFor(e, e.targets.Count);
            int removeAt = -1;
            for (int i = 0; i < e.targets.Count; i++)
            {
                var t = e.targets[i];
                bool sel = selected == i;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        bool show = GUILayout.Toggle(t.show, "Show", "Button", GUILayout.Width(52));
                        int frame = Mathf.Clamp(EditorGUILayout.IntField("Frame", t.frame), 0, Mathf.Max(0, g.AuthoringFrameCount - 1));
                        if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(e, "Edit hand/foot key"); t.show = show; t.frame = frame; }

                        if (GUILayout.Button(sel ? "◉" : "○", GUILayout.Width(26)))
                        { KimodoEditState.SelectExplicit(e, sel ? -1 : i); SceneView.RepaintAll(); }
                        if (GUILayout.Button("Go", GUILayout.Width(30)))
                        { g.Playing = false; g.SampleTime(g.Fps > 0f ? t.frame / g.Fps : 0f); SceneView.RepaintAll(); }
                        if (GUILayout.Button(new GUIContent("⋮", "More: align to frame, rest pose, snap a limb…"), GUILayout.Width(24)))
                            ShowKeyMenu(e, g, t);
                        if (GUILayout.Button("✕", GUILayout.Width(22))) removeAt = i;
                    }

                    // Which limbs this key pins. Several at one frame share the pose (as in the demo).
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Pin", GUILayout.Width(26));
                        var limbs = KimodoEndEffectors.LimbsOf(t);
                        foreach (var l in KimodoEndEffectors.AllLimbs)
                        {
                            bool on = (limbs & l) != 0;
                            var prev = GUI.backgroundColor;
                            if (on) GUI.backgroundColor = LimbColor(l);
                            bool now = GUILayout.Toggle(on, KimodoEndEffectors.ShortName(l), "Button", GUILayout.Width(36));
                            GUI.backgroundColor = prev;
                            if (now != on)
                            {
                                Undo.RecordObject(e, "Edit pinned limbs");
                                t.limbs = now ? (limbs | l) : (limbs & ~l);
                                SceneView.RepaintAll();
                            }
                        }
                        EditorGUI.BeginChangeCheck();
                        bool fr = GUILayout.Toggle(t.freeRoot, new GUIContent("Free root",
                            "Let the body rise/turn to reach: only the limb and the ground position are pinned, " +
                            "not the pose's height or facing. Use when a limb has to go somewhere the body " +
                            "should adapt to (e.g. stepping onto a box)."), "Button");
                        if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(e, "Edit free root"); t.freeRoot = fr; }
                    }
                }
            }
            if (removeAt >= 0)
            {
                Undo.RecordObject(e, "Remove hand/foot key");
                e.targets.RemoveAt(removeAt);
                KimodoEditState.Select(e, -1);
            }

            if (e.targets.Count > 0)
                EditorGUILayout.LabelField($"{e.targets.Count} key(s) → {CountConstraints(e)} constraint(s) on next Generate.",
                    EditorStyles.miniLabel);

            if (GUI.changed) SceneView.RepaintAll();
        }

        private static int CountConstraints(KimodoEndEffectors e)
        {
            int n = 0;
            foreach (var t in e.targets)
            {
                var limbs = KimodoEndEffectors.LimbsOf(t);
                foreach (var l in KimodoEndEffectors.AllLimbs) if ((limbs & l) != 0) n++;
            }
            return n;
        }

        private void ShowKeyMenu(KimodoEndEffectors e, KimodoGenerator g, KimodoEndEffectors.Target t)
        {
            var menu = new GenericMenu();
            if (g.Motion != null)
                menu.AddItem(new GUIContent("Align pose to frame"), false, () =>
                {
                    Undo.RecordObject(e, "Align hand/foot key");
                    e.AlignKeyToMotion(t);
                    EditorUtility.SetDirty(e); SceneView.RepaintAll(); Repaint();
                });
            else
                menu.AddDisabledItem(new GUIContent("Align pose to frame (needs a motion)"));
            menu.AddItem(new GUIContent("Rest pose"), false, () =>
            {
                Undo.RecordObject(e, "Reset hand/foot key pose");
                e.SetIdlePose(t);
                EditorUtility.SetDirty(e); SceneView.RepaintAll(); Repaint();
            });
            menu.AddSeparator("");
            foreach (var l in KimodoEndEffectors.AllLimbs)
            {
                var limb = l;
                menu.AddItem(new GUIContent($"Snap to motion/{KimodoEndEffectors.TypeString(limb)}"), false, () =>
                {
                    SnapLimbToMotion(e, g, t, limb);
                    EditorUtility.SetDirty(e); SceneView.RepaintAll(); Repaint();
                });
            }
            menu.ShowAsContext();
        }

        // Put one limb back where the generated motion has it at this frame, leaving the rest of the
        // authored pose alone.
        private static void SnapLimbToMotion(KimodoEndEffectors e, KimodoGenerator g,
                                             KimodoEndEffectors.Target t, KimodoEndEffectors.Limb limb)
        {
            var m = g.Motion;
            if (m == null || m.clips == null || m.clips.Count == 0) return;
            if (!e.EnsurePose(t)) return;
            var clip = m.clips[Mathf.Clamp(g.clipIndex, 0, m.clips.Count - 1)];
            int f = Mathf.Clamp(t.frame, 0, m.frameCount - 1);
            int j = KimodoFK.BoneIndex(m, KimodoEndEffectors.BaseBone(limb));
            if (j < 0) return;
            var gpos = KimodoFK.GlobalPositions(m, clip, f);
            Undo.RecordObject(e, "Snap limb to motion");
            e.SolveLimbTo(t, limb, gpos[j]);
        }

        // =======================================================================================
        // Scene view
        // =======================================================================================
        private void EnsureMapAndBodyIdx(KimodoGenerator g)
        {
            var sk = g.PoseSkeleton;
            if (_bodyIdx == null || _bodyMotionHash != sk.GetHashCode())
            {
                _bodyMotionHash = sk.GetHashCode();
                _bodyIdx = new int[BodyJoints.Length];
                for (int b = 0; b < BodyJoints.Length; b++) _bodyIdx[b] = KimodoFK.BoneIndex(sk, BodyJoints[b]);
            }
            int sig = sk.GetHashCode() * 31 + g.clipIndex;
            sig = sig * 31 + g.rootMotionScale.GetHashCode();
            if (sig != _mapSig) { _map = KimodoRootMap.Compute(g); _mapSig = sig; }
        }

        private static bool Shown(KimodoEndEffectors e, KimodoGenerator g, KimodoEndEffectors.Target t) =>
            t.show && (!e.showOnlyCurrentFrame || t.frame == g.CurrentFrame);

        private void OnSceneGUI()
        {
            var e = Eff; var g = e.ResolvedGenerator;
            if (g == null || !g.HasAuthoringSkeleton) return;
            var sk = g.PoseSkeleton;

            // A drag ends on mouse-up wherever it happens, so handles re-anchor to the real pose.
            // A drag is over as soon as no handle is hot. MouseUp alone is not enough: the handle
            // consumes it, and one delivered to another Scene view never arrives here — the anchor
            // would then survive into the NEXT drag and be used as its starting point, so each drag
            // added to the last, the handle marched away from the camera, and GetHandleSize scaled it
            // up with the distance. That is the gizmo that "grows".
            if (_dragging && (GUIUtility.hotControl == 0 || Event.current.type == EventType.MouseUp))
                _dragging = false;

            KimodoEditState.ResolveFollow(g);
            int selected = KimodoEditState.IndexFor(e, e.targets.Count);

            // Seed (and migrate) any shown key that has no pose yet, so there is something to draw.
            foreach (var t in e.targets)
            {
                if (!Shown(e, g, t) || t.hasPose) continue;
                if (e.EnsurePose(t)) EditorUtility.SetDirty(e);
            }

            // Every shown key gets a ghost (hide one with its own Show toggle). The mesh is faded down
            // to the part of the body the key's limbs belong to, so a hand key shows an arm rather than
            // a whole extra character standing in the shot.
            if (e.showGhostMesh)
            {
                var views = new List<KimodoPoseGhosts.PoseView>();
                foreach (var t in e.targets)
                    if (Shown(e, g, t) && t.localQuats != null)
                        views.Add(new KimodoPoseGhosts.PoseView
                        { owner = t, localQuats = t.localQuats, root = t.root, jointActive = e.GhostMask(t, sk) });
                (_ghosts ??= new KimodoPoseGhosts()).Sync(g, e.ghostOpacity, views);
            }
            else if (_ghosts != null) { _ghosts.Dispose(); _ghosts = null; }

            EnsureMapAndBodyIdx(g);
            if (!_map.valid) return;

            var prevZ = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;

            // Interactive passes first (stable control IDs), decoration last (rule 3).
            if (selected >= 0 && selected < e.targets.Count && Shown(e, g, e.targets[selected]))
                DrawHandles(e, sk, e.targets[selected]);
            DrawPickers(e, g, sk, selected);
            if (Event.current.type == EventType.Repaint)
                for (int i = 0; i < e.targets.Count; i++)
                    if (Shown(e, g, e.targets[i]))
                        DrawDecoration(e, sk, e.targets[i], i == selected);

            Handles.zTest = prevZ;
        }

        // ---- geometry shared by the passes -----------------------------------------------------
        private struct Layout
        {
            public Vector3[] gpos;
            public Quaternion[] grot;
            public HashSet<int> redJoints;
            public HashSet<int> redBones;                                      // keyed by the bone's child joint
            public List<KeyValuePair<KimodoEndEffectors.Limb, int>> effectors;  // limb -> its base joint
        }

        private static bool BuildLayout(KimodoMotion sk, KimodoEndEffectors.Target t, out Layout L)
        {
            L = default;
            if (t.localQuats == null || t.localQuats.Length != sk.jointCount * 4) return false;
            KimodoFK.GlobalPose(sk, KimodoEndEffectors.PoseClip(t), 0, out L.gpos, out L.grot);

            // The demo colours exactly this set red: constrained_idx = [root_idx] + ee_joint_indices.
            L.redJoints = new HashSet<int>();
            L.redBones = new HashSet<int>();
            L.effectors = new List<KeyValuePair<KimodoEndEffectors.Limb, int>>();
            if (sk.rootIndex >= 0) L.redJoints.Add(sk.rootIndex);

            var limbs = KimodoEndEffectors.LimbsOf(t);
            foreach (var l in KimodoEndEffectors.AllLimbs)   // fixed order => stable control IDs
            {
                if ((limbs & l) == 0) continue;
                int baseJoint = -1, n = 0;
                foreach (var name in KimodoEndEffectors.ConstrainedChain(l))
                {
                    int j = KimodoFK.BoneIndex(sk, name);
                    if (j < 0) continue;                     // e.g. ToeEnd is somaskel77-only
                    L.redJoints.Add(j);
                    if (n == 0) baseJoint = j; else L.redBones.Add(j);
                    n++;
                }
                if (baseJoint >= 0) L.effectors.Add(new KeyValuePair<KimodoEndEffectors.Limb, int>(l, baseJoint));
            }
            return true;
        }

        // ---- interactive pass: the gizmo for the active tool ------------------------------------
        private void DrawHandles(KimodoEndEffectors e, KimodoMotion sk, KimodoEndEffectors.Target t)
        {
            if (!BuildLayout(sk, t, out var L)) return;
            Vector3 W(int j) => KimodoRootMap.KimodoToWorld(L.gpos[j], _map);

            foreach (var kv in L.effectors)
            {
                int j = kv.Value;
                if (Tool == KimodoTool.MoveLimb)
                {
                    Vector3 anchor = DragAnchor(j, W(j));
                    EditorGUI.BeginChangeCheck();
                    Vector3 np = Handles.PositionHandle(anchor, Quaternion.identity);
                    if (EditorGUI.EndChangeCheck())
                    {
                        // Anchor to what the user asked for, not to the IK result (rule 2).
                        _dragging = true; _dragJoint = j; _dragPos = np;
                        Undo.RecordObject(e, "Move effector");
                        e.SolveLimbTo(t, kv.Key, KimodoRootMap.WorldToKimodo(np, _map));
                        EditorUtility.SetDirty(e);
                    }
                }
                else if (Tool == KimodoTool.AimLimb)
                {
                    Quaternion anchor = _dragging && _dragJoint == j ? _dragRot : _map.charRot * FlipQ(L.grot[j]);
                    EditorGUI.BeginChangeCheck();
                    Quaternion nr = Handles.RotationHandle(anchor, W(j));
                    if (EditorGUI.EndChangeCheck())
                    {
                        _dragging = true; _dragJoint = j; _dragRot = nr;
                        Undo.RecordObject(e, "Aim effector");
                        e.SetJointGlobalRotation(t, j, FlipQ(Quaternion.Inverse(_map.charRot) * nr));
                        EditorUtility.SetDirty(e);
                    }
                }
            }
        }

        // ---- interactive pass: pickers ---------------------------------------------------------
        // Click another key's pelvis dot to start editing that key, so switching keys never needs the
        // inspector. (The edited key has no picker: its own handles own that space.)
        private void DrawPickers(KimodoEndEffectors e, KimodoGenerator g, KimodoMotion sk, int selected)
        {
            if (sk.rootIndex < 0) return;
            for (int i = 0; i < e.targets.Count; i++)
            {
                if (i == selected) continue;
                var t = e.targets[i];
                if (!Shown(e, g, t) || t.localQuats == null || t.localQuats.Length != sk.jointCount * 4) continue;
                var gpos = KimodoFK.GlobalPositions(sk, KimodoEndEffectors.PoseClip(t), 0);
                Vector3 p = KimodoRootMap.KimodoToWorld(gpos[sk.rootIndex], _map);
                float s = HandleUtility.GetHandleSize(p);
                Handles.color = new Color(1f, 1f, 1f, 0.6f);
                if (Handles.Button(p, Quaternion.identity, s * 0.07f, s * 0.11f, Handles.SphereHandleCap))
                { KimodoEditState.SelectExplicit(e, i); Repaint(); }
            }
        }

        // ---- decoration pass (Repaint only) ----------------------------------------------------
        private void DrawDecoration(KimodoEndEffectors e, KimodoMotion sk, KimodoEndEffectors.Target t, bool selected)
        {
            if (!BuildLayout(sk, t, out var L)) return;
            Vector3 W(int j) => KimodoRootMap.KimodoToWorld(L.gpos[j], _map);

            float alpha = selected ? 1f : 0.45f;

            // The whole skeleton is always drawn; only the constrained joints/bones are red.
            var draw = new List<int>();
            foreach (int j in _bodyIdx) if (j >= 0) draw.Add(j);
            foreach (int j in L.redJoints) if (!draw.Contains(j)) draw.Add(j);

            foreach (int j in draw)
            {
                int par = sk.bones[j].parent;
                if (par < 0 || !draw.Contains(par)) continue;
                bool red = L.redBones.Contains(j);
                var c = red ? Constrained : Loose;
                Handles.color = new Color(c.r, c.g, c.b, alpha);
                Handles.DrawAAPolyLine(red ? (selected ? 7f : 4f) : (selected ? 4f : 2.5f), W(par), W(j));
            }

            foreach (int j in draw)
            {
                bool red = L.redJoints.Contains(j);
                var c = red ? Constrained : Loose;
                Vector3 p = W(j);
                Handles.color = new Color(c.r, c.g, c.b, alpha);
                Handles.SphereHandleCap(0, p, Quaternion.identity,
                                        HandleUtility.GetHandleSize(p) * (red ? 0.075f : 0.05f), EventType.Repaint);
            }

            // Axes on each effector — the demo's add_batched_axes, showing the rotation that is pinned.
            foreach (var kv in L.effectors)
            {
                Vector3 p = W(kv.Value);
                Quaternion rot = _map.charRot * FlipQ(L.grot[kv.Value]);
                float s = HandleUtility.GetHandleSize(p) * 0.35f;
                Handles.color = Color.red;   Handles.DrawAAPolyLine(3f, p, p + rot * Vector3.right * s);
                Handles.color = Color.green; Handles.DrawAAPolyLine(3f, p, p + rot * Vector3.up * s);
                Handles.color = Color.blue;  Handles.DrawAAPolyLine(3f, p, p + rot * Vector3.forward * s);
            }

            // Out of reach: a dotted line to the target the limb could not make. No text — the Scene
            // stays clean; the overlay carries the wording.
            if (!selected || Tool != KimodoTool.MoveLimb) return;
            foreach (var kv in L.effectors)
            {
                Vector3 want = DragAnchor(kv.Value, W(kv.Value));
                if (e.ReachDeficit(t, kv.Key, KimodoRootMap.WorldToKimodo(want, _map)) <= 0.005f) continue;
                Handles.color = Color.yellow;
                Handles.DrawDottedLine(W(kv.Value), want, 4f);
            }
        }
    }
}
