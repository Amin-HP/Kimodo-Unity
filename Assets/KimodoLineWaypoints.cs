// Example EXTERNAL script (not part of the Kimodo Bridge package): a STRAIGHT path of waypoints with a
// matching straight hand rail running alongside it. Everything else — the rail curve, the upper-body
// arm constraints and the fit — lives in KimodoRailAuthor, shared with KimodoCircleWaypoints.
//
// Put this on the same GameObject as a Humanoid character that already has a KimodoGenerator
// (GameObject ▸ Kimodo ▸ Set Up Selected Character). It auto-adds a KimodoWaypoints component.
//
// Unlike the circle, this path does not come back on itself: the rail has two ends, so arc positions
// clamp there instead of wrapping. 'Rail overhang' extends it past both ends of the walk so the arms
// still find curve to reach for at the first and last waypoint.

using System.Collections.Generic;
using UnityEngine;
using AminHP.KimodoBridge;

[RequireComponent(typeof(KimodoGenerator))]
public class KimodoLineWaypoints : KimodoRailAuthor
{
    [Header("Line")]
    [Tooltip("Where the walk starts. If 'Start at character' is on, this is ignored and the line " +
             "begins at the character's current position.")]
    public Transform startTransform;
    public Vector3 start;                        // used when startTransform is null

    [Tooltip("Where the walk ends. If set, it overrides Direction + Length.")]
    public Transform endTransform;

    [Tooltip("Direction of travel (world, Y ignored). Used when no End transform is set.")]
    public Vector3 direction = Vector3.forward;

    [Tooltip("How far the character walks (metres). Used when no End transform is set.")]
    [Min(0.1f)] public float length = 4f;

    [Tooltip("Begin the line at the character's current position.")]
    public bool startAtCharacter = true;

    [Tooltip("Distance from the walking line to the rail: + = to the LEFT of the direction of travel, " +
             "- = to the right. Keep it within about an arm's length, or move the body to it with " +
             "'Fit shoulder to rail'.")]
    public float curveSideOffset = 0.55f;

    [Tooltip("Extends the rail this far past BOTH ends of the walk (metres). An open rail stops dead " +
             "at its ends, so without a little overhang the arms run out of curve at the first and " +
             "last waypoint and pile up on the end point.")]
    [Min(0f)] public float railOverhang = 1f;

    // ---- path ---------------------------------------------------------------------------------

    /// <summary>Populate the KimodoWaypoints component with an evenly spaced straight line.</summary>
    public override void BuildWaypoints()
    {
        var g = Gen; var wp = Wp;
        if (g == null || wp == null) return;

        int fc = FrameCount(g);
        int pts = Mathf.Max(2, resolution);
        Vector3 a = ResolveStart(g), b = ResolveEnd(g);
        float groundY = a.y;

        var list = new List<KimodoWaypoints.Waypoint>(pts);
        float heading = Mathf.Atan2(b.x - a.x, b.z - a.z) * Mathf.Rad2Deg + facingOffsetDeg;

        for (int i = 0; i < pts; i++)
        {
            float t = (float)i / (pts - 1);                  // 0..1 along the line
            var pos = Vector3.Lerp(a, b, t);
            pos.y = groundY;
            int frame = Mathf.Clamp(Mathf.RoundToInt(t * (fc - 1)), 0, fc - 1);

            var w = new KimodoWaypoints.Waypoint { frame = frame, world = pos };
            if (faceAlongPath)
            {
                // One heading for the whole line — it never turns, so every waypoint faces the same way
                // (plus the offset: ±90 makes it a side-step along the line).
                w.constrainFacing = true;
                w.headingDeg = heading;
            }
            list.Add(w);
        }

        RecordUndo(wp, "Build line waypoints");
        wp.waypoints = list;
        wp.groundY = groundY;
        SetDirty(wp);
    }

    /// <summary>Kept for symmetry with the circle script — same as BuildWaypoints().</summary>
    public void BuildLine() => BuildWaypoints();

    // ---- rail ---------------------------------------------------------------------------------

    // A straight rail parallel to the walk, offset sideways. It has ends, so arc positions clamp.
    protected override bool RailClosed => false;
    protected override float RailGroundY => ResolveStart(Gen).y;

    protected override Vector3 RailPoint(float t)
    {
        var g = Gen;
        Vector3 a = ResolveStart(g), b = ResolveEnd(g);
        Vector3 fwd = b - a; fwd.y = 0f;
        fwd = fwd.sqrMagnitude < 1e-8f ? Vector3.forward : fwd.normalized;

        // Left of the direction of travel. Unity is left-handed, so up × forward points to the left.
        Vector3 left = Vector3.Cross(Vector3.up, fwd);

        // Stretch the rail past both ends so the arms don't run out of curve at the last waypoint.
        Vector3 railA = a - fwd * railOverhang;
        Vector3 railB = b + fwd * railOverhang;

        var p = Vector3.Lerp(railA, railB, t) + left * curveSideOffset;
        p.y = a.y + curveHeight + CurveWave(t);
        return p;
    }

    // ---- gizmo --------------------------------------------------------------------------------

    protected override void DrawPathGizmo()
    {
        var g = Gen;
        Vector3 a = ResolveStart(g), b = ResolveEnd(g);
        int pts = Mathf.Max(2, resolution);

        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.9f);
        Gizmos.DrawLine(a, b);
        float dot = Mathf.Max(0.03f, Vector3.Distance(a, b) * 0.02f);
        for (int i = 0; i < pts; i++)
            Gizmos.DrawSphere(Vector3.Lerp(a, b, (float)i / (pts - 1)), dot);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private Vector3 ResolveStart(KimodoGenerator g)
    {
        if (startAtCharacter && g != null)
            return g.ResolvedTarget != null ? g.ResolvedTarget.transform.position : transform.position;
        return startTransform != null ? startTransform.position : start;
    }

    private Vector3 ResolveEnd(KimodoGenerator g)
    {
        if (endTransform != null) return endTransform.position;
        Vector3 d = direction; d.y = 0f;
        d = d.sqrMagnitude < 1e-8f ? Vector3.forward : d.normalized;
        return ResolveStart(g) + d * Mathf.Max(0.1f, length);
    }
}
