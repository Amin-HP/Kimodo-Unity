// SPDX-License-Identifier: Apache-2.0
// Shared, persistent state for constraint editing: which key is being edited, which tool is active
// for that KIND of key, and whether the edited key follows the playhead. One place, so the Scene
// overlay, the custom inspectors and the Timeline window always agree.
//
// WHY THIS IS STATIC AND NOT A FIELD ON THE EDITOR: a UnityEditor.Editor instance is thrown away and
// rebuilt whenever the Inspector rebuilds — which `EditorUtility.SetDirty` / `Undo.RecordObject`
// during a handle drag can trigger. When the selection and the active tool lived on the editor
// instance, that reset them mid-edit: the gizmo silently vanished (or reverted to another tool) and
// the user saw "it rotates a couple of times and then it doesn't rotate at all". Static state
// survives the rebuild; only a domain reload clears it, which also re-selects sanely.
//
// The tool is remembered PER KIND (waypoint / end-effector / pose). Each kind only offers the tools
// that mean something for it, and selecting a key of another kind switches the toolset with it.

using System;
using UnityEditor;
using UnityEngine;

namespace AminHP.KimodoBridge.Editor
{
    /// <summary>Which sort of constraint key is being edited.</summary>
    public enum KimodoKeyKind { None, Waypoint, EndEffector, Pose }

    /// <summary>What a Scene-view drag does. Each tool belongs to exactly one key kind.</summary>
    public enum KimodoTool
    {
        MoveWaypoint,  // drag the waypoint on the ground
        FaceWaypoint,  // aim the waypoint's facing arrow
        MoveLimb,      // drag a hand/foot: the limb bends to reach it
        AimLimb,       // rotate a hand/foot (its rotation is constrained too)
        RotateJoints,  // the demo's editing mode: click any joint and rotate it
        MovePose,      // drag the pelvis: move the whole pose, height included
        ToggleJoints,  // click a joint to include/exclude it from the constraint
    }

    public static class KimodoEditState
    {
        // Waypoints deliberately have NO tools: their gizmo does both jobs at once (drag the ground dot
        // to move it, drag the arrow tip to aim it) and that already worked well — splitting it into
        // modes only got in the way.
        private static readonly KimodoTool[] EffectorTools = { KimodoTool.MoveLimb, KimodoTool.AimLimb };
        private static readonly KimodoTool[] PoseTools = { KimodoTool.RotateJoints, KimodoTool.MovePose, KimodoTool.ToggleJoints };

        private const string FollowPref = "Kimodo.FollowPlayhead";
        private static bool _follow = EditorPrefs.GetBool(FollowPref, true);

        /// <summary>Raised whenever the tool, the selection or the follow flag changes.</summary>
        public static event Action Changed;

        // ---- tools, per kind ------------------------------------------------------------------
        public static KimodoTool[] ToolsFor(KimodoKeyKind kind) => kind switch
        {
            KimodoKeyKind.EndEffector => EffectorTools,
            KimodoKeyKind.Pose => PoseTools,
            _ => Array.Empty<KimodoTool>(),
        };

        /// <summary>The active tool for a kind. Stored per kind, so switching to a key of another kind
        /// brings up that kind's tools instead of leaving a tool that means nothing for it.</summary>
        public static KimodoTool Tool(KimodoKeyKind kind)
        {
            var tools = ToolsFor(kind);
            if (tools.Length == 0) return KimodoTool.MoveWaypoint;
            var stored = (KimodoTool)EditorPrefs.GetInt(ToolPref(kind), (int)tools[0]);
            return Array.IndexOf(tools, stored) >= 0 ? stored : tools[0];
        }

        public static void SetTool(KimodoKeyKind kind, KimodoTool tool)
        {
            if (Array.IndexOf(ToolsFor(kind), tool) < 0 || Tool(kind) == tool) return;
            EditorPrefs.SetInt(ToolPref(kind), (int)tool);
            SelectedJoint = -1;
            RaiseChanged();
        }

        private static string ToolPref(KimodoKeyKind kind) => "Kimodo.EditTool." + kind;

        // ---- selection -------------------------------------------------------------------------
        /// <summary>Component whose key is being edited.</summary>
        public static Component Owner { get; private set; }

        /// <summary>Index of the key inside that component's list, or -1.</summary>
        public static int Index { get; private set; } = -1;

        /// <summary>Joint picked inside the key (RotateJoints / ToggleJoints), or -1.</summary>
        public static int SelectedJoint { get; set; } = -1;

        public static KimodoKeyKind KindOf(Component c) => c switch
        {
            KimodoWaypoints _ => KimodoKeyKind.Waypoint,
            KimodoEndEffectors _ => KimodoKeyKind.EndEffector,
            KimodoPoseConstraints _ => KimodoKeyKind.Pose,
            _ => KimodoKeyKind.None,
        };

        /// <summary>The kind of key currently selected (None when nothing is).</summary>
        public static KimodoKeyKind SelectedKind => Index >= 0 ? KindOf(Owner) : KimodoKeyKind.None;

        /// <summary>When on, the key at the preview frame is the one you edit — so scrubbing, or
        /// clicking a key in the Timeline, picks it instead of hunting for it in an inspector.</summary>
        public static bool FollowPlayhead
        {
            get => _follow;
            set
            {
                if (_follow == value) return;
                _follow = value;
                EditorPrefs.SetBool(FollowPref, value);
                RaiseChanged();
            }
        }

        /// <summary>Select a key for editing. Pass index -1 (or a null owner) to clear.</summary>
        public static void Select(Component owner, int index)
        {
            if (ReferenceEquals(Owner, owner) && Index == index) return;
            Owner = index >= 0 ? owner : null;
            Index = owner != null ? index : -1;
            SelectedJoint = -1;
            RaiseChanged();
        }

        /// <summary>Select a key because the user pointed at it (a Scene ghost, a Timeline key, the ◉
        /// button). An explicit pick wins over "follow the playhead", which is otherwise still on and
        /// would immediately hand the selection back to whatever key sits under the playhead.</summary>
        public static void SelectExplicit(Component owner, int index)
        {
            FollowPlayhead = false;
            Select(owner, index);
        }

        /// <summary>The selected index within <paramref name="owner"/>, or -1 if something else is selected.
        /// <paramref name="count"/> bounds it, so a key deleted elsewhere can never leave a stale index.</summary>
        public static int IndexFor(Component owner, int count)
        {
            if (owner == null || !ReferenceEquals(Owner, owner)) return -1;
            return Index >= 0 && Index < count ? Index : -1;
        }

        // ---- follow the playhead ---------------------------------------------------------------
        /// <summary>Point the selection at the key under the playhead. Resolved in ONE place (rather
        /// than each editor deciding for itself) so every view — overlay, inspectors, Timeline — agrees
        /// on which key, and of which kind, is being edited. The component already being edited wins a
        /// tie, so working on hand/foot keys doesn't jump to a pose key that shares the frame.</summary>
        public static void ResolveFollow(KimodoGenerator g)
        {
            if (!_follow || g == null) return;
            int frame = g.CurrentFrame;

            if (Owner != null && TryFindKeyAt(Owner, frame, out int keep)) { Select(Owner, keep); return; }
            foreach (var c in new Component[]
                     {
                         g.GetComponent<KimodoEndEffectors>(),
                         g.GetComponent<KimodoPoseConstraints>(),
                         g.GetComponent<KimodoWaypoints>(),
                     })
                if (c != null && TryFindKeyAt(c, frame, out int i)) { Select(c, i); return; }
            // No key on this frame: keep whatever was selected rather than blanking the tools.
        }

        /// <summary>Index of <paramref name="c"/>'s key at <paramref name="frame"/>, if any.</summary>
        public static bool TryFindKeyAt(Component c, int frame, out int index)
        {
            index = -1;
            switch (c)
            {
                case KimodoEndEffectors e:
                    index = e.IndexOfKeyAt(frame);
                    break;
                case KimodoPoseConstraints p:
                    for (int i = 0; i < p.keys.Count; i++) if (p.keys[i].frame == frame) { index = i; break; }
                    break;
                case KimodoWaypoints w:
                    for (int i = 0; i < w.waypoints.Count; i++) if (w.waypoints[i].frame == frame) { index = i; break; }
                    break;
            }
            return index >= 0;
        }

        public static int KeyCount(Component c) => c switch
        {
            KimodoEndEffectors e => e.targets.Count,
            KimodoPoseConstraints p => p.keys.Count,
            KimodoWaypoints w => w.waypoints.Count,
            _ => 0,
        };

        public static int KeyFrame(Component c, int i) => c switch
        {
            KimodoEndEffectors e => i >= 0 && i < e.targets.Count ? e.targets[i].frame : -1,
            KimodoPoseConstraints p => i >= 0 && i < p.keys.Count ? p.keys[i].frame : -1,
            KimodoWaypoints w => i >= 0 && i < w.waypoints.Count ? w.waypoints[i].frame : -1,
            _ => -1,
        };

        public static string DisplayName(KimodoKeyKind kind) => kind switch
        {
            KimodoKeyKind.Waypoint => "2D Root (waypoint)",
            KimodoKeyKind.EndEffector => "End-Effectors",
            KimodoKeyKind.Pose => "Full-Body",
            _ => "—",
        };

        public static void RaiseChanged()
        {
            Changed?.Invoke();
            SceneView.RepaintAll();
        }
    }
}
