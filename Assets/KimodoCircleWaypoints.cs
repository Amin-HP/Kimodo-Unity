// Example EXTERNAL script (not part of the Kimodo Bridge package) showing how to drive the plugin
// from your own code: it builds a circular loop of waypoints and generates motion that follows it.
//
// Put this on the same GameObject as a Humanoid character that already has a KimodoGenerator
// (GameObject ▸ Kimodo ▸ Set Up Selected Character). It auto-adds a KimodoWaypoints component.
//
// Because the world↔Kimodo mapping is measured from an existing motion, waypoints only take effect
// on a SECOND generate. "Build + Generate" does that for you: it generates once to establish the
// mapping (if needed), then builds the circle and generates again with the constraint applied.

using System.Collections.Generic;
using UnityEngine;
using AminHP.KimodoBridge;
#if UNITY_EDITOR
using UnityEditor;
#endif

[RequireComponent(typeof(KimodoGenerator))]
public class KimodoCircleWaypoints : MonoBehaviour
{
    [Header("Circle")]
    [Tooltip("Pivot of the circle. If 'Start at character' is on, this is ignored and the loop is " +
             "placed so the character's current position is the first waypoint.")]
    public Transform centerTransform;
    public Vector3 center;                       // used when centerTransform is null
    [Min(0.1f)] public float radius = 2f;
    [Tooltip("Number of waypoints around the circle (>= 3).")]
    [Range(3, 64)] public int resolution = 8;
    [Tooltip("Angle (deg, 0 = +X) of the first waypoint around the circle.")]
    public float startAngleDeg = 0f;
    public bool clockwise = false;
    [Tooltip("Add a closing waypoint back at the start position on the last frame (full loop).")]
    public bool closeLoop = true;
    [Tooltip("Place the loop so the character's current position is the first waypoint (frame 0).")]
    public bool startAtCharacter = true;
    [Tooltip("Also constrain facing so the character looks along the direction of travel.")]
    public bool faceAlongPath = true;

    [Tooltip("Rotation added to each waypoint's facing (deg). 0 = along the path; ±90 = look toward / " +
             "away from the centre; 180 = face backward. Applied to every waypoint.")]
    public float facingOffsetDeg = 0f;

    [Header("Pose")]
    [Tooltip("Duplicate the FIRST pose key (on a sibling KimodoPoseConstraints) onto every waypoint " +
             "frame, with its root moved onto the circle so the pose travels along the loop. Author one " +
             "pose first (e.g. an upper-body partial pose), then build.")]
    public bool addPoseConstraints = false;

    [Tooltip("Show the duplicated pose ghosts in the Scene (off by default — one per waypoint is heavy).")]
    public bool showPoseGhosts = false;

    [Header("Refs (auto-found)")]
    public KimodoGenerator generator;
    public KimodoWaypoints waypoints;

    private KimodoGenerator Gen => generator != null ? generator : (generator = GetComponent<KimodoGenerator>());
    private KimodoWaypoints Wp =>
        waypoints != null ? waypoints : (waypoints = GetComponent<KimodoWaypoints>() ?? gameObject.AddComponent<KimodoWaypoints>());

    // ---- public API -------------------------------------------------------------------------

    /// <summary>Populate the KimodoWaypoints component with a circular loop. Call from any script.</summary>
    public void BuildCircle()
    {
        var g = Gen; var wp = Wp;
        if (g == null || wp == null) return;

        int fc = FrameCount(g);
        int segs = Mathf.Max(3, resolution);
        int pts = closeLoop ? segs + 1 : segs;
        int dir = clockwise ? -1 : 1;

        Vector3 c = ResolveCenter(g);
        float groundY = c.y;

        var list = new List<KimodoWaypoints.Waypoint>(pts);
        for (int i = 0; i < pts; i++)
        {
            float a = (startAngleDeg + dir * 360f * i / segs) * Mathf.Deg2Rad;
            var pos = c + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius;
            pos.y = groundY;

            float t = pts > 1 ? (float)i / (pts - 1) : 0f;   // 0..1 along the loop
            int frame = Mathf.Clamp(Mathf.RoundToInt(t * (fc - 1)), 0, fc - 1);

            var w = new KimodoWaypoints.Waypoint { frame = frame, world = pos };
            if (faceAlongPath)
            {
                // Tangent to the circle = derivative of the position w.r.t. angle; + the facing offset
                // (e.g. ±90 to look toward/away from the centre instead of along the path).
                Vector3 tan = dir * new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a));
                w.constrainFacing = true;
                w.headingDeg = Mathf.Atan2(tan.x, tan.z) * Mathf.Rad2Deg + facingOffsetDeg;
            }
            list.Add(w);
        }

        // Close the loop exactly: the last waypoint is the FIRST one again, on the final frame — same
        // position and facing, completely identical, so the path returns precisely to the start.
        if (closeLoop && list.Count > 1)
        {
            var first = list[0];
            var last = list[list.Count - 1];
            last.world = first.world;
            last.constrainFacing = first.constrainFacing;
            last.headingDeg = first.headingDeg;
        }

        RecordUndo(wp, "Build circle waypoints");
        wp.waypoints = list;
        wp.groundY = groundY;
        SetDirty(wp);
    }

    /// <summary>Build the circle and generate motion that follows it (two-pass on the first run so the
    /// world↔Kimodo mapping exists before the waypoints are applied).</summary>
    public void BuildAndGenerate()
    {
        var g = Gen;
        if (g == null) return;
        if (g.ResolvedBridge == null || !g.ResolvedBridge.IsOnline)
        {
            Debug.LogWarning("[Kimodo] Bridge is not connected — press Connect on the KimodoBridge first.");
            return;
        }

        if (!g.IsPreviewBound)
        {
            // No motion yet: generate once to establish the mapping + frame count, then loop it.
            g.Generate(() => { BuildCircle(); if (addPoseConstraints) BuildPoses(); g.Generate(); });
        }
        else
        {
            BuildCircle();
            if (addPoseConstraints) BuildPoses();
            g.Generate();
        }
    }

    /// <summary>Duplicate the first authored pose key onto every waypoint frame, moving each copy's root
    /// onto the circle so the pose travels along the loop (rather than freezing in one spot). The first
    /// and last waypoint share a position, so their pose keys match — a closed pose loop.</summary>
    public void BuildPoses()
    {
        var g = Gen; var wp = Wp;
        var pc = GetComponent<KimodoPoseConstraints>();
        if (pc == null) pc = gameObject.AddComponent<KimodoPoseConstraints>();

        if (g.Motion == null || !g.IsPreviewBound)
        { Debug.LogWarning("[Kimodo] Generate once first so the pose root can follow the path."); return; }
        if (wp.waypoints == null || wp.waypoints.Count == 0)
        { Debug.LogWarning("[Kimodo] Build the circle waypoints first."); return; }
        if (pc.keys == null || pc.keys.Count == 0)
        { Debug.LogWarning("[Kimodo] Author a pose on KimodoPoseConstraints first — that 'first one' is duplicated."); return; }

        int J = g.Motion.jointCount;
        var src = pc.keys[0];
        if (src.localQuats == null || src.localQuats.Length != J * 4)
        { Debug.LogWarning("[Kimodo] The first pose key has no authored pose (align/edit it first)."); return; }

        var m = wp.ComputeMapping();
        var keys = new List<KimodoPoseConstraints.Key>(wp.waypoints.Count);
        foreach (var w in wp.waypoints)
        {
            var key = new KimodoPoseConstraints.Key
            {
                frame = w.frame,
                hasPose = true,
                show = showPoseGhosts,
                localQuats = (float[])src.localQuats.Clone(),
                jointActive = src.jointActive != null ? (bool[])src.jointActive.Clone() : null,
            };
            // Move the pose's root onto the waypoint so it travels the circle (keep the authored height).
            if (m.valid)
            {
                var kxz = KimodoWaypoints.WorldToKimodoXZ(w.world, m);
                key.root = new Vector3(kxz.x, src.root.y, kxz.y);
            }
            else key.root = src.root;
            keys.Add(key);
        }

        RecordUndo(pc, "Duplicate pose to waypoints");
        pc.keys = keys;
        SetDirty(pc);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private Vector3 ResolveCenter(KimodoGenerator g)
    {
        if (startAtCharacter)
        {
            // Offset the center so the first waypoint (at startAngle) lands on the character.
            var charPos = g.ResolvedTarget != null ? g.ResolvedTarget.transform.position : transform.position;
            float a0 = startAngleDeg * Mathf.Deg2Rad;
            return charPos - new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)) * radius;
        }
        return centerTransform != null ? centerTransform.position : center;
    }

    private int FrameCount(KimodoGenerator g)
    {
        if (g.FrameCount > 1) return g.FrameCount;          // exact, from the current motion
        float fps = g.Fps > 0f ? g.Fps : 30f;              // else estimate from the generator's duration
        float secs = 0f;
        foreach (var p in (g.duration ?? "").Split(' '))
            if (float.TryParse(p, out var s)) secs += s;
        if (secs <= 0f) secs = 4f;
        return Mathf.Max(2, Mathf.RoundToInt(secs * fps));
    }

    private static void RecordUndo(Object o, string label)
    {
#if UNITY_EDITOR
        if (!Application.isPlaying) Undo.RecordObject(o, label);
#endif
    }

    private static void SetDirty(Object o)
    {
#if UNITY_EDITOR
        if (!Application.isPlaying) EditorUtility.SetDirty(o);
#endif
    }

    // Preview the planned circle in the Scene view before generating.
    private void OnDrawGizmosSelected()
    {
        if (Gen == null) return;
        Vector3 c = ResolveCenter(Gen);
        int segs = Mathf.Max(3, resolution);
        int dir = clockwise ? -1 : 1;
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.9f);
        Vector3 prev = default;
        for (int i = 0; i <= segs; i++)
        {
            float a = (startAngleDeg + dir * 360f * i / segs) * Mathf.Deg2Rad;
            var p = c + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius;
            if (i > 0) Gizmos.DrawLine(prev, p);
            if (i < segs) Gizmos.DrawSphere(p, radius * 0.06f);
            prev = p;
        }
    }

#if UNITY_EDITOR
    [ContextMenu("Build Circle")]
    private void CtxBuild() { BuildCircle(); SceneView.RepaintAll(); }

    [ContextMenu("Duplicate Pose To Waypoints")]
    private void CtxPoses() { BuildPoses(); SceneView.RepaintAll(); }

    [ContextMenu("Build Circle + Generate")]
    private void CtxBuildGenerate() => BuildAndGenerate();
#endif
}
