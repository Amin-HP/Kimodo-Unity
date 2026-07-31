// SPDX-License-Identifier: Apache-2.0
// The Kimodo sequencer: Window ▸ Kimodo ▸ Timeline.
//
// One dockable window over a character's whole shot. The horizontal axis is FRAMES (the same frame
// indices every Kimodo constraint is keyed on), and it carries four tracks:
//
//   Prompt      — the KimodoTimeline asset's segments (drag an edge to retime, click to edit the text)
//   Waypoints   — KimodoWaypoints.waypoints        (root path / root2d)
//   Effectors   — KimodoEndEffectors.targets          (hand/foot targets)
//   Poses       — KimodoPoseConstraints.keys       (whole-rig pose keys)
//
// The prompt segments live in the assigned .asset (the "file"); the constraint keys stay on the
// scene components, where their world positions and Scene gizmos belong — this window is a second
// view onto the very same data the inspectors edit, so both stay in sync. Take ▸ Save/Load copies
// the keys in and out of the asset when you want the shot in one file.
//
// Drawing is plain IMGUI: rects for blocks, rotated rects for keys, and manual Event handling for
// drag/select/context-menu (no GUILayout inside the timeline area, so geometry stays exact).

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AminHP.KimodoBridge.Editor
{
    public class KimodoTimelineWindow : EditorWindow
    {
        // ---- layout ----------------------------------------------------
        private const float HeaderW = 124f;   // left column holding the track names
        private const float RulerH = 22f;
        private const float TrackH = 30f;
        private const float KeyHalf = 6f;     // half-size of a key diamond
        private const int TrackCount = 4;

        // ---- palette ---------------------------------------------------
        private static readonly Color BgColor = new Color(0.16f, 0.16f, 0.17f);
        private static readonly Color RowColor = new Color(0.20f, 0.20f, 0.21f);
        private static readonly Color RowAltColor = new Color(0.22f, 0.22f, 0.23f);
        private static readonly Color RulerColor = new Color(0.13f, 0.13f, 0.14f);
        private static readonly Color GridColor = new Color(1f, 1f, 1f, 0.07f);
        private static readonly Color PlayheadColor = new Color(1f, 0.35f, 0.25f);
        private static readonly Color SelColor = Color.white;
        private static readonly Color WpColor = new Color(1f, 0.6f, 0.2f);
        private static readonly Color PoseColor = new Color(0.55f, 0.85f, 0.45f);

        private static readonly Color[] SegColors =
        {
            new Color(0.27f, 0.45f, 0.65f), new Color(0.42f, 0.36f, 0.62f),
            new Color(0.25f, 0.52f, 0.50f), new Color(0.58f, 0.42f, 0.30f),
            new Color(0.50f, 0.32f, 0.46f), new Color(0.33f, 0.50f, 0.34f),
        };

        // ---- state -----------------------------------------------------
        private KimodoGenerator _gen;
        private bool _lockTarget;

        private float _zoom = 1f;      // 1 = the whole timeline fits the view
        private float _scrollX;        // pixels scrolled into the content

        private enum Sel { None, Segment, Waypoint, Effector, Pose }
        private Sel _selKind = Sel.None;
        private int _selIndex = -1;

        private enum Drag { None, Playhead, SegmentEdge, Key }
        private Drag _drag = Drag.None;
        private Sel _dragTrack = Sel.None;
        private int _dragIndex = -1;

        private Vector2 _paneScroll;
        private float _paneW = 260f;   // width of the left selection panel
        private bool _draggingSplitter;
        private Rect _content;         // the drawable track area (right of the header column)
        private float _pxPerFrame = 1f;

        // Anything that changes what the window DRAWS (selection, adding/removing a key, assigning a
        // different character, a modal panel) must not happen half-way through an event pass — IMGUI
        // would then see a different control sequence than the layout pass built. Such actions are
        // queued here and run at the start of the next layout pass instead. A list, not a single slot:
        // two actions can be queued in one pass (e.g. a context menu firing after a click selected).
        private readonly List<Action> _deferred = new List<Action>();

        private void Defer(Action a) { if (a != null) _deferred.Add(a); Repaint(); }

        /// <summary>Select without deferring — for code already running outside a Layout pass (the ＋
        /// buttons). Publishing to KimodoEditState is what makes the Scene overlay show the new key's
        /// tools; setting only the window's own fields left the previous key's tools up.</summary>
        private void SelectNow(Sel kind, int index, Component owner)
        {
            _selKind = kind; _selIndex = index;
            if (owner != null && index >= 0) KimodoEditState.Select(owner, index);
        }

        private void Select(Sel kind, int index) => Defer(() =>
        {
            _selKind = kind; _selIndex = index;
            // Clicking a key here also makes it the one you edit in the Scene view, so you never have
            // to find it again in the inspector. An explicit pick overrides "follow the playhead".
            Component owner = kind switch
            {
                Sel.Effector => Eff,
                Sel.Pose => Pc,
                Sel.Waypoint => Wp,
                _ => null,
            };
            if (owner != null && index >= 0) KimodoEditState.SelectExplicit(owner, index);
        });

        /// <summary>Drop a selection whose target is gone (a key deleted from the inspector, the last one
        /// removed, the character swapped). Runs ONLY at the start of a layout pass: a pane that cleared
        /// the selection while drawing would make the layout pass register no controls and the repaint
        /// pass draw the empty panel — "control 0 in a group with 0 controls".</summary>
        private void ValidateSelection()
        {
            bool ok;
            switch (_selKind)
            {
                case Sel.Segment: ok = Tl != null && _selIndex >= 0 && _selIndex < Tl.SegmentCount; break;
                case Sel.Waypoint: ok = Wp != null && _selIndex >= 0 && _selIndex < Wp.waypoints.Count; break;
                case Sel.Effector: ok = Eff != null && _selIndex >= 0 && _selIndex < Eff.targets.Count; break;
                case Sel.Pose: ok = Pc != null && _selIndex >= 0 && _selIndex < Pc.keys.Count; break;
                default: ok = true; break;
            }
            if (!ok) { _selKind = Sel.None; _selIndex = -1; }
        }

        // ---------------------------------------------------------------
        [MenuItem("Window/Kimodo/Timeline")]
        public static KimodoTimelineWindow Open()
        {
            var w = GetWindow<KimodoTimelineWindow>();
            w.titleContent = new GUIContent("Kimodo Timeline");
            w.minSize = new Vector2(620f, 380f);
            w.Show();
            return w;
        }

        /// <summary>Open the window already pointed at a character (used by the Generator inspector).</summary>
        public static void OpenFor(KimodoGenerator g)
        {
            var w = Open();
            if (g != null) { w._gen = g; w._lockTarget = true; }
            w.Repaint();
        }

        private void OnEnable()
        {
            // Playback is driven by KimodoPlayback (a single clock shared with the inspector); we
            // only repaint when it advances.
            KimodoPlayback.Advanced += Repaint;
            Undo.undoRedoPerformed += Repaint;
            KimodoEditState.Changed += OnEditStateChanged;
            if (_gen == null) AdoptSelection();
        }

        private void OnDisable()
        {
            KimodoPlayback.Advanced -= Repaint;
            Undo.undoRedoPerformed -= Repaint;
            KimodoEditState.Changed -= OnEditStateChanged;
        }

        /// <summary>Selecting a key elsewhere (the Scene overlay's ◀ ▶, a gizmo click) must show up
        /// here too — highlighted on its track, and with the playhead where that code put it. Without
        /// the repaint the window keeps drawing the old playhead position even though PreviewTime
        /// already moved, which reads as "the arrows don't move the playhead".</summary>
        private void OnEditStateChanged()
        {
            // Structural changes only at the start of a Layout pass (see §7c) — hence Defer.
            Defer(SyncSelectionFromEditState);
            Repaint();
        }

        private void SyncSelectionFromEditState()
        {
            var owner = KimodoEditState.Owner;
            int index = KimodoEditState.Index;
            if (owner == null || index < 0) return;
            if (ReferenceEquals(owner, Wp)) { _selKind = Sel.Waypoint; _selIndex = index; }
            else if (ReferenceEquals(owner, Eff)) { _selKind = Sel.Effector; _selIndex = index; }
            else if (ReferenceEquals(owner, Pc)) { _selKind = Sel.Pose; _selIndex = index; }
        }

        private void OnSelectionChange()
        {
            if (!_lockTarget) AdoptSelection();
            Repaint();
        }

        private void AdoptSelection()
        {
            var go = Selection.activeGameObject;
            var g = go != null ? go.GetComponentInParent<KimodoGenerator>() : null;
            if (g != null) _gen = g;
#if UNITY_2022_2_OR_NEWER
            else if (_gen == null) _gen = FindFirstObjectByType<KimodoGenerator>();
#else
            else if (_gen == null) _gen = FindObjectOfType<KimodoGenerator>();
#endif
        }

        // ---- shorthands -------------------------------------------------
        private float Fps => _gen != null ? Mathf.Max(1f, _gen.Fps) : 30f;

        /// <summary>Length of the frame axis: the longer of what the timeline currently describes and
        /// what the last generate produced — so retiming a segment grows the ruler straight away
        /// instead of waiting for the next Generate.</summary>
        private int TotalFrames
        {
            get
            {
                int fromMotion = _gen != null ? _gen.AuthoringFrameCount : 0;
                int fromTimeline = Tl != null ? Tl.TotalFrames(Fps) : 0;
                return Mathf.Max(2, Mathf.Max(fromMotion, fromTimeline));
            }
        }
        private int PlayFrame => _gen != null ? Mathf.Clamp(Mathf.RoundToInt(_gen.PreviewTime * Fps), 0, TotalFrames - 1) : 0;
        private KimodoTimeline Tl => _gen != null ? _gen.timeline : null;
        private KimodoWaypoints Wp => _gen != null ? _gen.GetComponent<KimodoWaypoints>() : null;
        private KimodoEndEffectors Eff => _gen != null ? _gen.GetComponent<KimodoEndEffectors>() : null;
        private KimodoPoseConstraints Pc => _gen != null ? _gen.GetComponent<KimodoPoseConstraints>() : null;

        private float FrameToX(float f) => _content.x - _scrollX + f * _pxPerFrame;
        private int XToFrame(float x) =>
            Mathf.Clamp(Mathf.RoundToInt((x - _content.x + _scrollX) / _pxPerFrame), 0, TotalFrames - 1);

        private void GoToFrame(int f)
        {
            if (_gen == null) return;
            KimodoPlayback.SetPlaying(_gen, false);
            _gen.SampleTime(Mathf.Clamp(f, 0, TotalFrames - 1) / Fps);
            SceneView.RepaintAll();
        }

        /// <summary>Fill a rect, clipped to the track area — without this, blocks scrolled off the left
        /// (or zoomed wider than the view) spill over the header column and the panel next to it.</summary>
        private void FillClipped(Rect r, Color c)
        {
            float x0 = Mathf.Max(r.x, _content.x);
            float x1 = Mathf.Min(r.xMax, _content.xMax);
            if (x1 <= x0) return;
            EditorGUI.DrawRect(new Rect(x0, r.y, x1 - x0, r.height), c);
        }

        /// <summary>Same idea for text: keep the label rect inside the track area.</summary>
        private bool ClipLabel(ref Rect r)
        {
            float x0 = Mathf.Max(r.x, _content.x + 2f);
            float x1 = Mathf.Min(r.xMax, _content.xMax - 2f);
            if (x1 - x0 < 10f) return false;
            r = new Rect(x0, r.y, x1 - x0, r.height);
            return true;
        }

        // ---------------------------------------------------------------
        private void OnGUI()
        {
            // Everything that changes the window's structure happens here, before a single control is
            // drawn, so the layout pass and the event/repaint pass always draw the same thing.
            if (Event.current.type == EventType.Layout)
            {
                if (_deferred.Count > 0)
                {
                    var queued = _deferred.ToArray();
                    _deferred.Clear();
                    foreach (var a in queued) a();
                }
                ValidateSelection();
            }

            DrawToolbar();
            DrawTargetRow();

            if (_gen == null)
            {
                EditorGUILayout.HelpBox(
                    "Select a character with a KimodoGenerator, or assign one above.\n" +
                    "(GameObject ▸ Kimodo ▸ Set Up Selected Character)", MessageType.Info);
                return;
            }

            HandleShortcuts();

            // Selection panel on the left, timeline on the right, with a draggable splitter between.
            using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandHeight(true)))
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(_paneW), GUILayout.ExpandHeight(true)))
                    DrawPane();

                DrawSplitter();

                using (new EditorGUILayout.VerticalScope())
                {
                    DrawTransport();
                    float h = RulerH + TrackH * TrackCount;
                    Rect area = GUILayoutUtility.GetRect(10f, 100000f, h, h);
                    DrawTimeline(area);
                    DrawScrollbar();
                    GUILayout.FlexibleSpace();
                }
            }
        }

        private void DrawSplitter()
        {
            var r = GUILayoutUtility.GetRect(4f, 4f, 1f, 100000f, GUILayout.Width(4f), GUILayout.ExpandHeight(true));
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.35f));
            EditorGUIUtility.AddCursorRect(r, MouseCursor.ResizeHorizontal);

            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
            { _draggingSplitter = true; e.Use(); }
            else if (_draggingSplitter)
            {
                if (e.type == EventType.MouseDrag)
                {
                    _paneW = Mathf.Clamp(e.mousePosition.x - 2f, 200f, Mathf.Max(220f, position.width - 280f));
                    e.Use(); Repaint();
                }
                else if (e.type == EventType.MouseUp) { _draggingSplitter = false; e.Use(); }
            }
        }

        // ---- keyboard ----------------------------------------------------
        // Delete removes what's selected, Space plays/pauses, arrows step the playhead. Typing in a
        // text field takes precedence, so editing a prompt never triggers any of this.
        private void HandleShortcuts()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown || EditorGUIUtility.editingTextField) return;

            switch (e.keyCode)
            {
                case KeyCode.Delete:
                case KeyCode.Backspace:
                    if (_selKind != Sel.None) { Defer(DeleteSelection); e.Use(); }
                    break;
                case KeyCode.Space:
                    TogglePlay(); e.Use();
                    break;
                case KeyCode.LeftArrow:
                    GoToFrame(PlayFrame - (e.shift ? 10 : 1)); e.Use(); Repaint();
                    break;
                case KeyCode.RightArrow:
                    GoToFrame(PlayFrame + (e.shift ? 10 : 1)); e.Use(); Repaint();
                    break;
                case KeyCode.Home:
                    GoToFrame(0); e.Use(); Repaint();
                    break;
                case KeyCode.End:
                    GoToFrame(TotalFrames - 1); e.Use(); Repaint();
                    break;
                case KeyCode.F:
                    FocusSelectionInScene(); e.Use();
                    break;
                case KeyCode.Escape:
                    Defer(() => { _selKind = Sel.None; _selIndex = -1; }); e.Use();
                    break;
            }
        }

        private void TogglePlay()
        {
            if (_gen == null || !_gen.IsPreviewBound) return;
            KimodoPlayback.Toggle(_gen);
            Repaint();
        }

        /// <summary>Delete whatever is selected (Delete key, or the ✕ in the panel).</summary>
        private void DeleteSelection()
        {
            switch (_selKind)
            {
                case Sel.Segment:
                    var tl = Tl;
                    if (tl != null && _selIndex >= 0 && _selIndex < tl.SegmentCount) { DeleteSegment(tl, _selIndex); return; }
                    break;
                case Sel.Waypoint:
                    var w = Wp;
                    if (w != null && _selIndex >= 0 && _selIndex < w.waypoints.Count)
                    { Undo.RecordObject(w, "Delete waypoint"); w.waypoints.RemoveAt(_selIndex); EditorUtility.SetDirty(w); }
                    break;
                case Sel.Effector:
                    var ef = Eff;
                    if (ef != null && _selIndex >= 0 && _selIndex < ef.targets.Count)
                    { Undo.RecordObject(ef, "Delete target"); ef.targets.RemoveAt(_selIndex); EditorUtility.SetDirty(ef); }
                    break;
                case Sel.Pose:
                    var p = Pc;
                    if (p != null && _selIndex >= 0 && _selIndex < p.keys.Count)
                    { Undo.RecordObject(p, "Delete pose key"); p.keys.RemoveAt(_selIndex); EditorUtility.SetDirty(p); }
                    break;
                default: return;
            }
            _selKind = Sel.None; _selIndex = -1;
            SceneView.RepaintAll();
        }

        /// <summary>Point the Scene view at the selected key (F).</summary>
        private void FocusSelectionInScene()
        {
            Vector3? at = null;
            if (_selKind == Sel.Waypoint && Wp != null && _selIndex < Wp.waypoints.Count) at = Wp.waypoints[_selIndex].world;
            else if (_selKind == Sel.Effector && Eff != null && _selIndex < Eff.targets.Count &&
                     Eff.TryGetKeyWorld(Eff.targets[_selIndex], out var ew)) at = ew;
            if (at != null && SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.LookAt(at.Value);
        }

        // ---- chrome ----------------------------------------------------
        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var b = _gen != null ? _gen.ResolvedBridge : null;
                bool online = b != null && b.IsOnline;
                bool busy = _gen != null && _gen.Busy;

                using (new EditorGUI.DisabledScope(_gen == null || busy || !online))
                    if (GUILayout.Button(busy ? "Generating…" : "▶ Generate", EditorStyles.toolbarButton, GUILayout.Width(90)))
                        Defer(() => _gen.Generate(Repaint));

                // While it runs, the button's usual neighbours are useless — put the progress where the
                // eye already is instead of in a corner of the window.
                if (busy)
                {
                    var bar = GUILayoutUtility.GetRect(160f, 16f, GUILayout.Width(160f));
                    bar.y += 1f; bar.height -= 2f;
                    if (_gen.Progress01 >= 0f)
                        EditorGUI.ProgressBar(bar, _gen.Progress01,
                            $"{Mathf.RoundToInt(_gen.Progress01 * 100f)}%  {_gen.ProgressLabel}");
                    else
                        EditorGUI.ProgressBar(bar, 0f,
                            string.IsNullOrEmpty(_gen.ProgressLabel) ? "Working…" : _gen.ProgressLabel);
                }

                using (new EditorGUI.DisabledScope(_gen == null || _gen.Motion == null))
                    if (GUILayout.Button("Bake…", EditorStyles.toolbarButton, GUILayout.Width(52)))
                        Defer(Bake);

                if (GUILayout.Button("Take ▾", EditorStyles.toolbarDropDown, GUILayout.Width(58)))
                    ShowTakeMenu();

                GUILayout.Space(8f);
                string status = _gen == null ? "" :
                    (b == null ? "no bridge" : (online ? (b.ModelReady ? "online" : "loading model…") : "offline"));
                GUILayout.Label(status, EditorStyles.miniLabel, GUILayout.Width(90));

                GUILayout.FlexibleSpace();

                GUILayout.Label("Zoom", EditorStyles.miniLabel, GUILayout.Width(36));
                _zoom = GUILayout.HorizontalSlider(_zoom, 1f, 12f, GUILayout.Width(90));
                if (GUILayout.Button("Fit", EditorStyles.toolbarButton, GUILayout.Width(34)))
                { _zoom = 1f; _scrollX = 0f; }
            }

            if (_gen != null && !string.IsNullOrEmpty(_gen.Info))
                EditorGUILayout.LabelField(_gen.Info, EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawTargetRow()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var g = (KimodoGenerator)EditorGUILayout.ObjectField(
                    new GUIContent("Character", "The KimodoGenerator this timeline drives."),
                    _gen, typeof(KimodoGenerator), true);
                if (EditorGUI.EndChangeCheck())
                    Defer(() => { _gen = g; _selKind = Sel.None; _selIndex = -1; });

                _lockTarget = GUILayout.Toggle(_lockTarget,
                    new GUIContent(_lockTarget ? "🔒" : "🔓", "Lock to this character (don't follow the Scene selection)."),
                    "Button", GUILayout.Width(28));
            }

            if (_gen == null) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var tl = (KimodoTimeline)EditorGUILayout.ObjectField(
                    new GUIContent("Timeline file", "Asset holding this shot's prompt segments (and an optional saved take)."),
                    _gen.timeline, typeof(KimodoTimeline), false);
                if (EditorGUI.EndChangeCheck())
                    Defer(() =>
                    {
                        Undo.RecordObject(_gen, "Assign Kimodo timeline");
                        _gen.timeline = tl;
                        _selKind = Sel.None; _selIndex = -1;
                        EditorUtility.SetDirty(_gen);
                    });
                if (GUILayout.Button(new GUIContent("New…", "Create a timeline asset and assign it."), GUILayout.Width(52)))
                    Defer(CreateTimelineAsset);
            }

            if (Tl == null)
                EditorGUILayout.HelpBox(
                    "No timeline file assigned — the Generator's own prompt + duration are used. " +
                    "Create one to sequence several prompts (constraint tracks still work below).",
                    MessageType.Info);
        }

        private void DrawTransport()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!_gen.IsPreviewBound))
                {
                    if (GUILayout.Button(new GUIContent(_gen.Playing ? "❚❚" : "▶", "Play / pause (Space)"), GUILayout.Width(34)))
                        KimodoPlayback.Toggle(_gen);
                    if (GUILayout.Button(new GUIContent("⏮", "Back to the start (Home)"), GUILayout.Width(34))) GoToFrame(0);
                }
                EditorGUI.BeginChangeCheck();
                bool loop = GUILayout.Toggle(_gen.loop, "Loop", "Button", GUILayout.Width(50));
                if (EditorGUI.EndChangeCheck()) _gen.loop = loop;

                GUILayout.Label("Speed", EditorStyles.miniLabel, GUILayout.Width(40));
                _gen.PlaybackSpeed = GUILayout.HorizontalSlider(_gen.PlaybackSpeed, 0.1f, 3f, GUILayout.Width(70));
                GUILayout.Label($"{_gen.PlaybackSpeed:0.0}×", EditorStyles.miniLabel, GUILayout.Width(30));

                GUILayout.FlexibleSpace();
                string src = _gen.Motion != null ? "motion" : "estimated";
                GUILayout.Label($"frame {PlayFrame} / {TotalFrames - 1}  ·  {PlayFrame / Fps:0.00}s  ·  {src}",
                    EditorStyles.miniLabel);
            }

            if (!_gen.IsPreviewBound)
                EditorGUILayout.LabelField(
                    "No preview yet — you can still author the timeline; it applies on the next Generate.",
                    EditorStyles.miniLabel);
        }

        // Always drawn (just disabled when everything fits) so the layout pass sees the same controls.
        private void DrawScrollbar()
        {
            float contentW = TotalFrames * _pxPerFrame;
            bool need = contentW > _content.width + 1f;
            using (new EditorGUI.DisabledScope(!need))
            {
                float v = GUILayout.HorizontalScrollbar(_scrollX, Mathf.Max(1f, _content.width), 0f, Mathf.Max(2f, contentW));
                _scrollX = need ? v : 0f;
            }
        }

        // ---- the timeline itself ---------------------------------------
        private void DrawTimeline(Rect area)
        {
            var e = Event.current;

            // GUILayoutUtility.GetRect returns a dummy rect during the layout pass — keep the last known
            // geometry then, or the scroll offset would be clamped away every frame.
            if (area.width > 2f)
            {
                _content = new Rect(area.x + HeaderW, area.y, Mathf.Max(40f, area.width - HeaderW), area.height);
                _pxPerFrame = Mathf.Max(0.02f, (_content.width / TotalFrames) * _zoom);
                _scrollX = Mathf.Clamp(_scrollX, 0f, Mathf.Max(0f, TotalFrames * _pxPerFrame - _content.width));
            }
            if (e.type == EventType.Layout) return;   // nothing here participates in layout

            if (e.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(area, BgColor);
                EditorGUI.DrawRect(new Rect(area.x, area.y, area.width, RulerH), RulerColor);
                for (int i = 0; i < TrackCount; i++)
                    EditorGUI.DrawRect(TrackRect(area, i), (i & 1) == 0 ? RowColor : RowAltColor);
                DrawRulerTicks(area);
            }

            DrawSegmentTrack(TrackRect(area, 0));
            DrawWaypointTrack(TrackRect(area, 1));
            DrawEffectorTrack(TrackRect(area, 2));
            DrawPoseTrack(TrackRect(area, 3));

            DrawPlayhead(area);
            DrawTrackHeaders(area);

            // Scrub by clicking/dragging the ruler; wheel zooms around the cursor.
            var ruler = new Rect(_content.x, area.y, _content.width, RulerH);
            if (e.type == EventType.MouseDown && e.button == 0 && ruler.Contains(e.mousePosition))
            { _drag = Drag.Playhead; GoToFrame(XToFrame(e.mousePosition.x)); e.Use(); Repaint(); }
            else if (_drag == Drag.Playhead)
            {
                if (e.type == EventType.MouseDrag) { GoToFrame(XToFrame(e.mousePosition.x)); e.Use(); Repaint(); }
                else if (e.type == EventType.MouseUp) { _drag = Drag.None; e.Use(); }
            }
            if (e.type == EventType.ScrollWheel && _content.Contains(e.mousePosition))
            {
                int anchor = XToFrame(e.mousePosition.x);
                _zoom = Mathf.Clamp(_zoom * (1f - e.delta.y * 0.05f), 1f, 12f);
                _pxPerFrame = Mathf.Max(0.02f, (_content.width / TotalFrames) * _zoom);
                _scrollX = Mathf.Clamp(anchor * _pxPerFrame - (e.mousePosition.x - _content.x),
                                       0f, Mathf.Max(0f, TotalFrames * _pxPerFrame - _content.width));
                e.Use(); Repaint();
            }
        }

        private static Rect TrackRect(Rect area, int i) =>
            new Rect(area.x, area.y + RulerH + i * TrackH, area.width, TrackH);

        private void DrawRulerTicks(Rect area)
        {
            // Pick a tick spacing that keeps labels ~70 px apart, in "nice" second counts.
            float[] steps = { 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 30f, 60f };
            float secStep = steps[steps.Length - 1];
            foreach (float s in steps)
                if (s * Fps * _pxPerFrame >= 70f) { secStep = s; break; }

            var style = EditorStyles.miniLabel;
            for (float t = 0f; t <= TotalFrames / Fps + 1e-3f; t += secStep)
            {
                float x = FrameToX(t * Fps);
                if (x < _content.x - 1f || x > _content.xMax) continue;
                EditorGUI.DrawRect(new Rect(x, area.y + 12f, 1f, area.height - 12f), GridColor);
                var lab = new Rect(x + 2f, area.y + 1f, 60f, 14f);
                if (ClipLabel(ref lab)) GUI.Label(lab, $"{t:0.##}s", style);
            }
        }

        private void DrawPlayhead(Rect area)
        {
            float x = FrameToX(PlayFrame);
            if (x < _content.x || x > _content.xMax) return;
            if (Event.current.type != EventType.Repaint) return;
            EditorGUI.DrawRect(new Rect(x, area.y, 1f, area.height), PlayheadColor);
            EditorGUI.DrawRect(new Rect(x - 4f, area.y, 9f, 5f), PlayheadColor);
        }

        // Left column: track name, key count and a "+" that adds at the playhead.
        private void DrawTrackHeaders(Rect area)
        {
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(area.x, area.y, HeaderW, area.height), RulerColor);

            for (int i = 0; i < TrackCount; i++)
            {
                Rect r = TrackRect(area, i);
                var label = new Rect(r.x + 6f, r.y + 2f, HeaderW - 34f, 15f);
                var sub = new Rect(r.x + 6f, r.y + 15f, HeaderW - 34f, 13f);
                var plus = new Rect(r.x + HeaderW - 26f, r.y + 5f, 20f, 18f);

                switch (i)
                {
                    case 0:
                        GUI.Label(label, "Prompt", EditorStyles.boldLabel);
                        GUI.Label(sub, Tl != null ? $"{Tl.SegmentCount} segment(s)" : "no file", EditorStyles.miniLabel);
                        using (new EditorGUI.DisabledScope(Tl == null))
                            if (GUI.Button(plus, new GUIContent("＋", "Add a segment at the end.")))
                                Defer(AddSegment);
                        break;
                    case 1:
                        GUI.Label(label, "Waypoints", EditorStyles.boldLabel);
                        GUI.Label(sub, Wp != null ? $"{Wp.waypoints.Count} key(s)" : "add component", EditorStyles.miniLabel);
                        if (GUI.Button(plus, new GUIContent("＋", "Add a waypoint at the playhead."))) Defer(AddWaypoint);
                        break;
                    case 2:
                        GUI.Label(label, "End-Effectors", EditorStyles.boldLabel);
                        GUI.Label(sub, Eff != null ? $"{Eff.targets.Count} key(s)" : "add component", EditorStyles.miniLabel);
                        if (GUI.Button(plus, new GUIContent("＋", "Add a hand/foot target at the playhead."))) Defer(AddEffector);
                        break;
                    case 3:
                        GUI.Label(label, "Poses", EditorStyles.boldLabel);
                        GUI.Label(sub, Pc != null ? $"{Pc.keys.Count} key(s)" : "add component", EditorStyles.miniLabel);
                        if (GUI.Button(plus, new GUIContent("＋", "Add a whole-rig pose key at the playhead."))) Defer(AddPose);
                        break;
                }
            }
        }

        // ---- prompt track ----------------------------------------------
        private void DrawSegmentTrack(Rect track)
        {
            var tl = Tl;
            var e = Event.current;
            if (tl == null)
            {
                if (e.type == EventType.Repaint)
                    GUI.Label(new Rect(_content.x + 8f, track.y + 6f, 320f, 18f),
                        "No timeline file — using the Generator's prompt.", EditorStyles.miniLabel);
                return;
            }

            // Blocks are laid out on the AUTHORED durations, so retiming (dragging an edge or typing a
            // new duration) is visible immediately. They match the motion too: Kimodo's multi-prompt
            // output is exactly sum(int(seconds × fps)) frames — the transition frames are absorbed.
            int cursor = 0;

            for (int i = 0; i < tl.SegmentCount; i++)
            {
                if (!tl.IsActive(i)) continue;
                var seg = tl.segments[i];
                int start = cursor;
                int len = Mathf.Max(1, tl.FrameCountOf(i, Fps));
                cursor += len;

                float x0 = FrameToX(start), x1 = FrameToX(start + len);
                var block = new Rect(x0 + 1f, track.y + 3f, Mathf.Max(3f, x1 - x0 - 2f), track.height - 6f);
                bool sel = _selKind == Sel.Segment && _selIndex == i;

                if (e.type == EventType.Repaint && block.xMax > _content.x && block.x < _content.xMax)
                {
                    Color col = SegColors[i % SegColors.Length];
                    if (Frozen(seg)) col = Color.Lerp(col, new Color(0.55f, 0.75f, 0.95f), 0.45f);
                    FillClipped(block, col);
                    if (sel)
                    {
                        FillClipped(new Rect(block.x, block.y, block.width, 2f), SelColor);
                        FillClipped(new Rect(block.x, block.yMax - 2f, block.width, 2f), SelColor);
                    }
                    var lab = new Rect(block.x + 4f, block.y + 2f, Mathf.Max(0f, block.width - 8f), block.height - 4f);
                    if (ClipLabel(ref lab))
                        GUI.Label(lab, (Frozen(seg) ? "❄ " : "") + seg.prompt, EditorStyles.whiteMiniLabel);
                    // Badge the segments that pin with their own constraint strength.
                    if (seg.overrideConstraintWeight && block.width > 46f)
                    {
                        var badge = new Rect(block.xMax - 34f, block.yMax - 15f, 32f, 14f);
                        if (ClipLabel(ref badge))
                            GUI.Label(badge, $"w{seg.constraintWeight:0.#}", EditorStyles.whiteMiniLabel);
                    }
                }

                // Right edge = retime handle (changes this segment's seconds). A frozen segment's
                // length is fixed by the frames it kept, so it has no handle.
                var edge = new Rect(block.xMax - 3f, block.y, 6f, block.height);
                if (!Frozen(seg)) EditorGUIUtility.AddCursorRect(edge, MouseCursor.ResizeHorizontal);

                if (e.type == EventType.MouseDown && e.button == 0 && !Frozen(seg) && edge.Contains(e.mousePosition))
                {
                    Select(Sel.Segment, i);
                    _drag = Drag.SegmentEdge; _dragIndex = i;
                    Undo.RecordObject(tl, "Retime segment");
                    e.Use(); Repaint();
                }
                else if (e.type == EventType.MouseDown && block.Contains(e.mousePosition))
                {
                    Select(Sel.Segment, i);
                    if (e.button == 0 && e.clickCount == 2) GoToFrame(start);
                    if (e.button == 1) ShowSegmentMenu(tl, i, start);
                    e.Use(); Repaint();
                }

                if (_drag == Drag.SegmentEdge && _dragIndex == i)
                {
                    if (e.type == EventType.MouseDrag)
                    {
                        float secs = Mathf.Max(0.2f, (XToFrame(e.mousePosition.x) - start) / Fps);
                        tl.segments[i].seconds = Mathf.Round(secs * 20f) / 20f;   // snap to 0.05 s
                        EditorUtility.SetDirty(tl);
                        e.Use(); Repaint();
                    }
                    else if (e.type == EventType.MouseUp) { _drag = Drag.None; _dragIndex = -1; e.Use(); }
                }
            }
        }

        // ---- key tracks -------------------------------------------------
        private void DrawWaypointTrack(Rect track)
        {
            var w = Wp;
            if (w == null) { DrawMissingComponent(track, "KimodoWaypoints"); return; }
            for (int i = 0; i < w.waypoints.Count; i++)
            {
                var k = w.waypoints[i];
                int idx = i;
                Color c = k.constrainFacing ? Color.Lerp(WpColor, Color.white, 0.35f) : WpColor;
                DrawKey(track, k.frame, c, _selKind == Sel.Waypoint && _selIndex == i, null);
                KeyInteraction(track, k.frame, Sel.Waypoint, idx, w, "Move waypoint",
                    f => w.waypoints[idx].frame = f,
                    () => { if (idx < w.waypoints.Count) w.waypoints.RemoveAt(idx); });
            }
        }

        private void DrawEffectorTrack(Rect track)
        {
            var e = Eff;
            if (e == null) { DrawMissingComponent(track, "KimodoEndEffectors"); return; }
            for (int i = 0; i < e.targets.Count; i++)
            {
                var t = e.targets[i];
                int idx = i;
                DrawKey(track, t.frame, EffectorColor(KimodoEndEffectors.PrimaryLimb(t)),
                        _selKind == Sel.Effector && _selIndex == i, LimbsShort(t));
                KeyInteraction(track, t.frame, Sel.Effector, idx, e, "Move target",
                    f => e.targets[idx].frame = f,
                    () => { if (idx < e.targets.Count) e.targets.RemoveAt(idx); });
            }
        }

        private void DrawPoseTrack(Rect track)
        {
            var p = Pc;
            if (p == null) { DrawMissingComponent(track, "KimodoPoseConstraints"); return; }
            for (int i = 0; i < p.keys.Count; i++)
            {
                var k = p.keys[i];
                int idx = i;
                Color c = KimodoPoseConstraints.HasInactive(k) ? Color.Lerp(PoseColor, Color.gray, 0.4f) : PoseColor;
                DrawKey(track, k.frame, c, _selKind == Sel.Pose && _selIndex == i,
                        string.IsNullOrEmpty(k.prompt) ? null : Truncate(k.prompt, 18));
                KeyInteraction(track, k.frame, Sel.Pose, idx, p, "Move pose key",
                    f => p.keys[idx].frame = f,
                    () => { if (idx < p.keys.Count) p.keys.RemoveAt(idx); });
            }
        }

        private void DrawMissingComponent(Rect track, string comp)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.Label(new Rect(_content.x + 8f, track.y + 6f, 420f, 18f),
                $"No {comp} on this character — press ＋ to add one.", EditorStyles.miniLabel);
        }

        // A key = a diamond at its frame, with an optional caption to its right.
        private void DrawKey(Rect track, int frame, Color color, bool selected, string caption)
        {
            if (Event.current.type != EventType.Repaint) return;
            float x = FrameToX(frame);
            if (x < _content.x - KeyHalf || x > _content.xMax + KeyHalf) return;
            float y = track.center.y;

            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(45f, new Vector2(x, y));
            if (selected)
                EditorGUI.DrawRect(new Rect(x - KeyHalf - 2f, y - KeyHalf - 2f, (KeyHalf + 2f) * 2f, (KeyHalf + 2f) * 2f), SelColor);
            EditorGUI.DrawRect(new Rect(x - KeyHalf, y - KeyHalf, KeyHalf * 2f, KeyHalf * 2f), color);
            GUI.matrix = m;

            if (!string.IsNullOrEmpty(caption) && x + 10f < _content.xMax)
                GUI.Label(new Rect(x + 9f, y - 8f, Mathf.Min(120f, _content.xMax - x - 10f), 16f),
                          caption, EditorStyles.miniLabel);
        }

        // Select / drag-to-retime / right-click on a key. setFrame + delete act on the owning component.
        private void KeyInteraction(Rect track, int frame, Sel kind, int index, Component owner,
                                    string undoName, Action<int> setFrame, Action delete)
        {
            var e = Event.current;
            float x = FrameToX(frame);
            var hit = new Rect(x - KeyHalf - 2f, track.y + 2f, (KeyHalf + 2f) * 2f, track.height - 4f);
            bool over = hit.Contains(e.mousePosition) && _content.Contains(e.mousePosition);

            if (e.type == EventType.MouseDown && over)
            {
                Select(kind, index);
                if (e.button == 1)
                {
                    var menu = new GenericMenu();
                    int f = frame;
                    menu.AddItem(new GUIContent("Go to frame"), false, () => GoToFrame(f));
                    menu.AddItem(new GUIContent("Delete"), false, () => Defer(() =>
                    {
                        Undo.RecordObject(owner, "Delete key");
                        delete();
                        _selKind = Sel.None; _selIndex = -1;
                        EditorUtility.SetDirty(owner); SceneView.RepaintAll();
                    }));
                    menu.ShowAsContext();
                }
                else
                {
                    _drag = Drag.Key; _dragTrack = kind; _dragIndex = index;
                    Undo.RecordObject(owner, undoName);
                    if (e.clickCount == 2) GoToFrame(frame);
                }
                e.Use(); Repaint();
                return;
            }

            if (_drag == Drag.Key && _dragTrack == kind && _dragIndex == index)
            {
                if (e.type == EventType.MouseDrag)
                {
                    setFrame(XToFrame(e.mousePosition.x));
                    EditorUtility.SetDirty(owner);
                    e.Use(); Repaint(); SceneView.RepaintAll();
                }
                else if (e.type == EventType.MouseUp)
                { _drag = Drag.None; _dragTrack = Sel.None; _dragIndex = -1; e.Use(); }
            }
        }

        // ---- selection panel (left column) --------------------------------
        private void DrawPane()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true)))
            {
                _paneScroll = EditorGUILayout.BeginScrollView(_paneScroll);
                float lw = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 62f;   // the column is narrow

                switch (_selKind)
                {
                    case Sel.Segment: DrawSegmentPane(); break;
                    case Sel.Waypoint: DrawWaypointPane(); break;
                    case Sel.Effector: DrawEffectorPane(); break;
                    case Sel.Pose: DrawPosePane(); break;
                    default: DrawEmptyPane(); break;
                }

                EditorGUIUtility.labelWidth = lw;
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawEmptyPane()
        {
            EditorGUILayout.LabelField("Nothing selected", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Click a segment or a key in the timeline to edit it here. Drag keys sideways to retime; " +
                "drag a segment's right edge to change its duration.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Shortcuts", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Space — play / pause\n" +
                "Delete — remove the selection\n" +
                "← → — step a frame (Shift = 10)\n" +
                "Home / End — jump to the ends\n" +
                "F — look at the selected key\n" +
                "Esc — deselect",
                EditorStyles.wordWrappedMiniLabel);

            if (Tl != null)
            {
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("Sent as", EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(Tl.BuildPrompt(), EditorStyles.wordWrappedMiniLabel, GUILayout.Height(46f));
                EditorGUILayout.LabelField($"duration = \"{Tl.BuildDuration()}\"", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField($"{Tl.TotalSeconds:0.##}s  ·  ~{Tl.TotalFrames(Fps)} frames", EditorStyles.miniLabel);
            }
        }

        // A compact tool button. The package styles its buttons with glyphs (◉ ⋮ ✕ ＋) rather than
        // editor icon names, which vary between Unity versions — same convention here.
        private static bool Tool(string glyph, string tooltip, bool enabled = true)
        {
            using (new EditorGUI.DisabledScope(!enabled))
                return GUILayout.Button(new GUIContent(glyph, tooltip), GUILayout.Width(28f), GUILayout.Height(20f));
        }

        private void DrawSegmentPane()
        {
            var tl = Tl;
            if (tl == null || _selIndex < 0 || _selIndex >= tl.SegmentCount) { DrawEmptyPane(); return; }
            var s = tl.segments[_selIndex];
            int start = tl.StartFrame(_selIndex, Fps);
            int len = Mathf.Max(1, tl.FrameCountOf(_selIndex, Fps));
            int i = _selIndex;

            EditorGUILayout.LabelField($"Segment {i + 1} of {tl.SegmentCount}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"frames {start}–{start + len - 1}", EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (Tool("✂", "Split at the playhead", !Frozen(s) && PlayFrame > start && PlayFrame < start + len))
                    Defer(() => SplitSegment(tl, i));
                if (Tool("⧉", "Duplicate this segment")) Defer(() => DuplicateSegment(tl, i));
                if (Tool("▲", "Move earlier", i > 0)) Defer(() => MoveSegment(tl, i, -1));
                if (Tool("▼", "Move later", i < tl.SegmentCount - 1)) Defer(() => MoveSegment(tl, i, +1));
                if (Tool("⇥", "Move the playhead to this segment")) GoToFrame(start);
                if (KimodoTimeline.FreezeEnabled &&
                    Tool("❄", s.frozen ? "Unfreeze — let this segment be generated again"
                                       : "Freeze — keep these frames and stop regenerating them",
                         s.frozen || _gen.Motion != null))
                    Defer(() => ToggleFreeze(tl, i));
                GUILayout.FlexibleSpace();
                if (Tool("✕", "Delete this segment")) Defer(DeleteSelection);
            }

            EditorGUI.BeginChangeCheck();
            string prompt = EditorGUILayout.TextArea(s.prompt, GUILayout.MinHeight(52f));
            float secs = s.seconds;
            using (new EditorGUI.DisabledScope(Frozen(s)))
                secs = EditorGUILayout.FloatField(new GUIContent("Seconds",
                    Frozen(s) ? "Fixed while frozen — it is the length of the frames being kept."
                              : "How long this segment lasts."), s.seconds);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(tl, "Edit segment");
                s.prompt = prompt;
                if (!Frozen(s)) s.seconds = Mathf.Max(0.1f, secs);
                EditorUtility.SetDirty(tl);
            }

            if (Frozen(s))
            {
                var fc = s.frozenClip;
                EditorGUILayout.HelpBox(fc != null && fc.IsValid
                    ? $"❄ Frozen — {fc.frameCount} frames kept ({fc.capturedAt}). Generate does NOT request " +
                      "this segment at all: it comes back identical every time, and the run after it is told " +
                      "where and how it ended so it carries on from there. Unfreezing drops both. " +
                      "While anything is frozen, Generate returns a single sample."
                    : "❄ Frozen, but nothing was captured — unfreeze and freeze again after a Generate.",
                    MessageType.Info);
                if (GUILayout.Button(new GUIContent("Re-capture from current motion",
                        "Replace the kept frames with what the character is playing now."), GUILayout.Height(20f)))
                    Defer(() => RecaptureFreeze(tl, i));
            }

            if (KimodoTimeline.HasInteriorPeriod(s.prompt))
                EditorGUILayout.HelpBox("Periods split segments for Kimodo, so this one will be sent as a comma. " +
                    "Split it into two segments if you meant two beats.", MessageType.Warning);

            // Per-segment constraint strength: Kimodo generates each segment separately, so the
            // constraint guidance weight can differ per segment (server: segment_cfg_weights).
            EditorGUILayout.Space(4f);
            EditorGUI.BeginChangeCheck();
            bool ow = EditorGUILayout.ToggleLeft(new GUIContent("Own constraint weight",
                "Pin harder (or looser) during this segment only. Off = the Generator's global weight."),
                s.overrideConstraintWeight);
            float cw = s.constraintWeight;
            using (new EditorGUI.DisabledScope(!ow))
                cw = EditorGUILayout.Slider(new GUIContent("Weight", "Kimodo cfg_weight[1] for this segment."), cw, 1f, 8f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(tl, "Edit segment constraint weight");
                s.overrideConstraintWeight = ow; s.constraintWeight = cw;
                EditorUtility.SetDirty(tl);
            }
            if (!ow)
                EditorGUILayout.LabelField($"using global {_gen.constraintWeight:0.#}", EditorStyles.miniLabel);
        }

        private void DrawWaypointPane()
        {
            var w = Wp;
            if (w == null || _selIndex < 0 || _selIndex >= w.waypoints.Count) { DrawEmptyPane(); return; }
            var k = w.waypoints[_selIndex];

            EditorGUILayout.LabelField($"Waypoint {_selIndex + 1}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("The pelvis passes here (root2d).", EditorStyles.miniLabel);
            DrawKeyTools(k.frame, k.world);

            EditorGUI.BeginChangeCheck();
            int frame = Mathf.Clamp(EditorGUILayout.IntField("Frame", k.frame), 0, TotalFrames - 1);
            Vector3 world = EditorGUILayout.Vector3Field("World", k.world);
            bool cf = EditorGUILayout.ToggleLeft(new GUIContent("Constrain facing",
                "Force the character to face a direction here."), k.constrainFacing);
            float hd = k.headingDeg;
            using (new EditorGUI.DisabledScope(!cf))
                hd = EditorGUILayout.FloatField(new GUIContent("Yaw°", "World facing angle (0 = +Z)."), hd);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(w, "Edit waypoint");
                k.frame = frame; k.world = world; k.constrainFacing = cf; k.headingDeg = hd;
                EditorUtility.SetDirty(w); SceneView.RepaintAll();
            }
        }

        private void DrawEffectorPane()
        {
            var e = Eff;
            if (e == null || _selIndex < 0 || _selIndex >= e.targets.Count) { DrawEmptyPane(); return; }
            var t = e.targets[_selIndex];

            EditorGUILayout.LabelField($"Hand/foot key {_selIndex + 1}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("A pose whose ticked limbs are pinned.", EditorStyles.miniLabel);
            // The world position is derived from the pose, so only resolve it if ⌖ is actually pressed.
            DrawKeyToolsLazy(t.frame, () => e.TryGetKeyWorld(t, out var w) ? (Vector3?)w : null);

            EditorGUI.BeginChangeCheck();
            int frame = Mathf.Clamp(EditorGUILayout.IntField("Frame", t.frame), 0, TotalFrames - 1);
            bool show = EditorGUILayout.ToggleLeft("Show ghost in the Scene", t.show);
            bool fr = EditorGUILayout.ToggleLeft(new GUIContent("Free root",
                "Pin only the limb + ground position, leaving the pose's height and facing free."), t.freeRoot);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(e, "Edit hand/foot key");
                t.frame = frame; t.show = show; t.freeRoot = fr;
                EditorUtility.SetDirty(e); SceneView.RepaintAll();
            }

            EditorGUILayout.LabelField("Pinned limbs", EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                var limbs = KimodoEndEffectors.LimbsOf(t);
                foreach (var l in KimodoEndEffectors.AllLimbs)
                {
                    bool on = (limbs & l) != 0;
                    var prev = GUI.backgroundColor;
                    if (on) GUI.backgroundColor = EffectorColor(l);
                    bool now = GUILayout.Toggle(on, KimodoEndEffectors.ShortName(l), "Button");
                    GUI.backgroundColor = prev;
                    if (now != on)
                    {
                        Undo.RecordObject(e, "Edit pinned limbs");
                        t.limbs = now ? (limbs | l) : (limbs & ~l);
                        EditorUtility.SetDirty(e); SceneView.RepaintAll();
                    }
                }
            }

            using (new EditorGUI.DisabledScope(_gen == null || _gen.Motion == null))
                if (GUILayout.Button(new GUIContent("Align pose to frame",
                    "Reseed this key from the generated motion's own pose at its frame.")))
                {
                    Undo.RecordObject(e, "Align hand/foot key");
                    e.AlignKeyToMotion(t);
                    EditorUtility.SetDirty(e); SceneView.RepaintAll();
                }
            EditorGUILayout.LabelField("Select the character to edit the pose in the Scene view.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawPosePane()
        {
            var p = Pc;
            if (p == null || _selIndex < 0 || _selIndex >= p.keys.Count) { DrawEmptyPane(); return; }
            var k = p.keys[_selIndex];
            var g = _gen;

            EditorGUILayout.LabelField($"Pose key {_selIndex + 1}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Whole-rig pose (fullbody).", EditorStyles.miniLabel);
            DrawKeyTools(k.frame, null);

            EditorGUI.BeginChangeCheck();
            int frame = Mathf.Clamp(EditorGUILayout.IntField("Frame", k.frame), 0, TotalFrames - 1);
            bool show = EditorGUILayout.ToggleLeft("Show ghost in the Scene", k.show);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(p, "Edit pose key");
                k.frame = frame; k.show = show;
                EditorUtility.SetDirty(p); SceneView.RepaintAll();
            }

            EditorGUI.BeginChangeCheck();
            string prompt = EditorGUILayout.TextField(new GUIContent("Prompt", "Describe the pose, e.g. 'raise both hands'."), k.prompt);
            if (EditorGUI.EndChangeCheck())
            { Undo.RecordObject(p, "Edit pose prompt"); k.prompt = prompt; EditorUtility.SetDirty(p); }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                bool uwp = GUILayout.Toggle(k.useWaypoints, new GUIContent("WP",
                    "Generate this pose in context along the waypoint path (slower)."), "Button", GUILayout.Width(34f));
                if (EditorGUI.EndChangeCheck())
                { Undo.RecordObject(p, "Edit pose mode"); k.useWaypoints = uwp; EditorUtility.SetDirty(p); }

                var b = g.ResolvedBridge;
                using (new EditorGUI.DisabledScope(p.GeneratingPose || b == null || !b.IsOnline || string.IsNullOrWhiteSpace(k.prompt)))
                    if (GUILayout.Button(p.GeneratingPose ? "…" : "Generate pose"))
                    {
                        Undo.RecordObject(p, "Generate pose from prompt");
                        p.GeneratePoseForKey(k, (ok, err) =>
                        {
                            if (!ok) Debug.LogWarning("[Kimodo] " + err);
                            else EditorUtility.SetDirty(p);
                            Repaint(); SceneView.RepaintAll();
                        });
                    }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(g.Motion == null))
                    if (GUILayout.Button(new GUIContent("Align", "Seed the pose from the generated motion at this frame.")))
                    {
                        Undo.RecordObject(p, "Align pose to frame");
                        if (!p.AlignKeyToMotion(k)) p.SetIdlePose(k);
                        EditorUtility.SetDirty(p); SceneView.RepaintAll();
                    }
                if (GUILayout.Button(new GUIContent("Idle", "Reset to the neutral rest pose.")))
                {
                    Undo.RecordObject(p, "Reset pose to idle");
                    p.SetIdlePose(k);
                    EditorUtility.SetDirty(p); SceneView.RepaintAll();
                }
                if (GUILayout.Button(new GUIContent("Joints…", "Select the component so you can rotate joints in the Scene.")))
                { Selection.activeGameObject = p.gameObject; EditorGUIUtility.PingObject(p); }
            }

            EditorGUILayout.LabelField(
                KimodoPoseConstraints.HasInactive(k) ? "Partial pose (some joints off)" : "Whole body constrained",
                EditorStyles.miniLabel);
        }

        // Shared tool strip for a key: jump the playhead to it, look at it in the Scene, delete it.
        private void DrawKeyTools(int frame, Vector3? focus) =>
            DrawKeyToolsLazy(frame, focus != null ? () => focus : (System.Func<Vector3?>)null);

        // The focus point is a callback so a key whose position has to be computed (a pose's world
        // placement) only pays for it when ⌖ is pressed, not on every repaint.
        private void DrawKeyToolsLazy(int frame, System.Func<Vector3?> focus)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (Tool("⇥", "Move the playhead to this key")) GoToFrame(frame);
                if (Tool("⌖", "Look at it in the Scene view (F)", focus != null) && focus != null)
                {
                    var at = focus();
                    if (at != null && SceneView.lastActiveSceneView != null)
                        SceneView.lastActiveSceneView.LookAt(at.Value);
                }
                GUILayout.FlexibleSpace();
                if (Tool("✕", "Delete this key (Delete)")) Defer(DeleteSelection);
            }
        }

        // ---- adding things ----------------------------------------------
        private void AddSegment()
        {
            var tl = Tl;
            if (tl == null) return;
            Undo.RecordObject(tl, "Add segment");
            var last = tl.SegmentCount > 0 ? tl.segments[tl.SegmentCount - 1] : null;
            tl.segments.Add(new KimodoTimeline.Segment
            {
                prompt = "A person stands still",
                seconds = last != null ? last.seconds : 2f,
            });
            _selKind = Sel.Segment; _selIndex = tl.SegmentCount - 1;
            EditorUtility.SetDirty(tl);
        }

        private void AddWaypoint()
        {
            var w = Wp ?? Undo.AddComponent<KimodoWaypoints>(_gen.gameObject);
            var tgt = _gen.ResolvedTarget;
            var hips = tgt != null ? tgt.GetBoneTransform(HumanBodyBones.Hips) : null;
            Vector3 pos = hips != null ? hips.position : (tgt != null ? tgt.transform.position : Vector3.zero);
            Undo.RecordObject(w, "Add waypoint");
            w.waypoints.Add(new KimodoWaypoints.Waypoint
            {
                frame = PlayFrame,
                world = new Vector3(pos.x, w.groundY, pos.z),
            });
            SelectNow(Sel.Waypoint, w.waypoints.Count - 1, w);
            EditorUtility.SetDirty(w); SceneView.RepaintAll();
        }

        private void AddEffector()
        {
            var e = Eff ?? Undo.AddComponent<KimodoEndEffectors>(_gen.gameObject);
            Undo.RecordObject(e, "Add hand/foot key");
            var t = new KimodoEndEffectors.Target
            {
                frame = PlayFrame,
                limbs = KimodoEndEffectors.Limb.LeftFoot,
                show = true,
            };
            if (!e.AlignKeyToMotion(t)) e.SetIdlePose(t);
            e.targets.Add(t);
            SelectNow(Sel.Effector, e.targets.Count - 1, e);
            EditorUtility.SetDirty(e); SceneView.RepaintAll();
        }

        private void AddPose()
        {
            var p = Pc ?? Undo.AddComponent<KimodoPoseConstraints>(_gen.gameObject);
            Undo.RecordObject(p, "Add pose key");
            var k = new KimodoPoseConstraints.Key { frame = PlayFrame, show = true };
            p.keys.Add(k);
            if (!p.AlignKeyToMotion(k)) p.SetIdlePose(k);
            SelectNow(Sel.Pose, p.keys.Count - 1, p);
            EditorUtility.SetDirty(p); SceneView.RepaintAll();
        }

        // ---- segment operations -----------------------------------------
        private void MoveSegment(KimodoTimeline tl, int i, int dir)
        {
            int j = i + dir;
            if (j < 0 || j >= tl.SegmentCount) return;
            Undo.RecordObject(tl, "Reorder segments");
            var s = tl.segments[i];
            tl.segments[i] = tl.segments[j];
            tl.segments[j] = s;
            _selIndex = j;
            EditorUtility.SetDirty(tl);
        }

        private void SplitSegment(KimodoTimeline tl, int i)
        {
            int start = tl.StartFrame(i, Fps);
            int len = Mathf.Max(1, tl.FrameCountOf(i, Fps));
            int cut = Mathf.Clamp(PlayFrame - start, 1, len - 1);
            if (len < 2) return;
            Undo.RecordObject(tl, "Split segment");
            float first = cut / Fps;
            float second = Mathf.Max(0.1f, tl.segments[i].seconds - first);
            tl.segments[i].seconds = Mathf.Max(0.1f, first);
            tl.segments.Insert(i + 1, new KimodoTimeline.Segment
            {
                prompt = tl.segments[i].prompt,
                seconds = second,
            });
            _selIndex = i + 1;
            EditorUtility.SetDirty(tl);
        }

        private void DuplicateSegment(KimodoTimeline tl, int i)
        {
            Undo.RecordObject(tl, "Duplicate segment");
            tl.segments.Insert(i + 1, new KimodoTimeline.Segment
            {
                prompt = tl.segments[i].prompt,
                seconds = tl.segments[i].seconds,
            });
            _selIndex = i + 1;
            EditorUtility.SetDirty(tl);
        }

        private void DeleteSegment(KimodoTimeline tl, int i)
        {
            Undo.RecordObject(tl, "Delete segment");
            tl.segments.RemoveAt(i);
            _selKind = Sel.None; _selIndex = -1;
            EditorUtility.SetDirty(tl);
        }

        private static bool Frozen(KimodoTimeline.Segment s) => KimodoTimeline.FreezeEnabled && s != null && s.frozen;

        // Freeze = keep this segment's frames exactly as they came out and stop regenerating them.
        // Kimodo can't skip a segment (its multi-prompt loop feeds each segment into the next), so
        // Generate still runs over the whole sequence — the kept frames go back in afterwards and are
        // sent as a constraint meanwhile, so what IS generated flows in and out of them.
        private void ToggleFreeze(KimodoTimeline tl, int i)
        {
            if (tl == null || i < 0 || i >= tl.SegmentCount) return;
            var s = tl.segments[i];
            Undo.RecordObject(tl, s.frozen ? "Unfreeze segment" : "Freeze segment");
            // Unfreezing drops the kept frames, and with them the boundary constraints they imply — the
            // "hidden keys" that made the next run start where this segment ended.
            if (s.frozen) tl.Unfreeze(i);
            else if (!tl.FreezeSegment(i, _gen.Motion, _gen.clipIndex, Fps))
                Debug.LogWarning("[Kimodo] Nothing to freeze — Generate first so this segment has frames.");
            EditorUtility.SetDirty(tl);
            Repaint();
        }

        private void RecaptureFreeze(KimodoTimeline tl, int i)
        {
            if (tl == null || i < 0 || i >= tl.SegmentCount) return;
            Undo.RecordObject(tl, "Re-capture frozen segment");
            if (!tl.FreezeSegment(i, _gen.Motion, _gen.clipIndex, Fps))
                Debug.LogWarning("[Kimodo] Nothing to capture — Generate first.");
            EditorUtility.SetDirty(tl);
            Repaint();
        }

        private void ShowSegmentMenu(KimodoTimeline tl, int i, int start)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Go to start"), false, () => GoToFrame(start));
            if (KimodoTimeline.FreezeEnabled)
                menu.AddItem(new GUIContent(tl.segments[i].frozen ? "Unfreeze" : "Freeze"), tl.segments[i].frozen,
                    () => Defer(() => ToggleFreeze(tl, i)));
            menu.AddItem(new GUIContent("Split at playhead"), false, () => Defer(() => SplitSegment(tl, i)));
            menu.AddItem(new GUIContent("Duplicate"), false, () => Defer(() => DuplicateSegment(tl, i)));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Delete"), false, () => Defer(() => DeleteSegment(tl, i)));
            menu.ShowAsContext();
        }

        // ---- take (save/load the constraint keys into the asset) ---------
        private void ShowTakeMenu()
        {
            var menu = new GenericMenu();
            var tl = Tl;
            if (tl == null || _gen == null)
            {
                menu.AddDisabledItem(new GUIContent("Assign a timeline file first"));
                menu.ShowAsContext();
                return;
            }

            menu.AddItem(new GUIContent("Save take to file"), false, () =>
            {
                Undo.RecordObject(tl, "Save Kimodo take");
                tl.CaptureTake(_gen);
                EditorUtility.SetDirty(tl);
                AssetDatabase.SaveAssets();
                Repaint();
            });

            if (tl.take != null && tl.take.saved)
                menu.AddItem(new GUIContent($"Load take from file ({tl.take.Count} keys, {tl.take.savedAt})"), false, () => Defer(() =>
                {
                    if (!EditorUtility.DisplayDialog("Load take",
                        "This REPLACES the character's waypoints, effector targets and pose keys with the ones " +
                        "saved in the timeline file. Continue?", "Load", "Cancel")) return;
                    var w = Wp ?? Undo.AddComponent<KimodoWaypoints>(_gen.gameObject);
                    var e = Eff ?? Undo.AddComponent<KimodoEndEffectors>(_gen.gameObject);
                    var p = Pc ?? Undo.AddComponent<KimodoPoseConstraints>(_gen.gameObject);
                    Undo.RecordObject(w, "Load Kimodo take");
                    Undo.RecordObject(e, "Load Kimodo take");
                    Undo.RecordObject(p, "Load Kimodo take");
                    tl.ApplyTake(_gen);
                    EditorUtility.SetDirty(w); EditorUtility.SetDirty(e); EditorUtility.SetDirty(p);
                    _selKind = Sel.None; _selIndex = -1;
                    SceneView.RepaintAll();
                }));
            else
                menu.AddDisabledItem(new GUIContent("Load take from file (nothing saved yet)"));

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Ping the timeline file"), false, () => EditorGUIUtility.PingObject(tl));
            menu.ShowAsContext();
        }

        private void CreateTimelineAsset()
        {
            string suggested = _gen != null ? _gen.name + " Timeline" : "Kimodo Timeline";
            string path = EditorUtility.SaveFilePanelInProject(
                "New Kimodo timeline", suggested, "asset", "Where should the timeline file live?");
            if (string.IsNullOrEmpty(path)) return;

            var tl = CreateInstance<KimodoTimeline>();
            // Seed it from whatever the Generator's prompt already is, so nothing is lost.
            tl.segments = new List<KimodoTimeline.Segment>();
            var texts = (_gen.prompt ?? "").Split('.');
            var durs = (_gen.duration ?? "").Split(' ');
            int di = 0;
            foreach (var raw in texts)
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                float secs = 2f;
                if (di < durs.Length && float.TryParse(durs[di], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var s)) secs = s;
                else if (durs.Length == 1 && float.TryParse(durs[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var s1)) secs = s1;
                tl.segments.Add(new KimodoTimeline.Segment { prompt = t, seconds = Mathf.Max(0.1f, secs) });
                di++;
            }
            if (tl.segments.Count == 0) tl.segments.Add(new KimodoTimeline.Segment());

            AssetDatabase.CreateAsset(tl, path);
            AssetDatabase.SaveAssets();

            Undo.RecordObject(_gen, "Assign Kimodo timeline");
            _gen.timeline = tl;
            EditorUtility.SetDirty(_gen);
            _selKind = Sel.Segment; _selIndex = 0;
            EditorGUIUtility.PingObject(tl);
        }

        private void Bake()
        {
            string suggested = SanitizeFileName(_gen.EffectivePrompt);
            string path = EditorUtility.SaveFilePanelInProject(
                "Bake Kimodo motion", suggested, "anim", "Choose where to save the baked AnimationClip.");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string saved = KimodoBaker.BakeToAsset(_gen.Motion, _gen.clipIndex, _gen.loop, path,
                                                       _gen.rootMotionScale, _gen.rootBakeMode);
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(saved);
                EditorGUIUtility.PingObject(clip);
                _gen.Info = "Baked → " + saved;
            }
            catch (Exception ex)
            {
                _gen.Info = "Bake failed: " + ex.Message;
                Debug.LogException(ex);
            }
            Repaint();
        }

        // ---- small helpers ----------------------------------------------
        private static Color EffectorColor(KimodoEndEffectors.Limb k) => k switch
        {
            KimodoEndEffectors.Limb.LeftHand => new Color(0.35f, 0.7f, 1f),
            KimodoEndEffectors.Limb.RightHand => new Color(0.2f, 0.5f, 1f),
            KimodoEndEffectors.Limb.LeftFoot => new Color(1f, 0.75f, 0.3f),
            KimodoEndEffectors.Limb.RightFoot => new Color(1f, 0.55f, 0.15f),
            _ => Color.white,
        };

        // "LF", or "LF+RH" when one key pins several limbs.
        private static string LimbsShort(KimodoEndEffectors.Target t)
        {
            var limbs = KimodoEndEffectors.LimbsOf(t);
            string s = "";
            foreach (var l in KimodoEndEffectors.AllLimbs)
                if ((limbs & l) != 0) s += (s.Length > 0 ? "+" : "") + KimodoEndEffectors.ShortName(l);
            return s;
        }

        private static string Truncate(string s, int n) =>
            string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n - 1) + "…";

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
