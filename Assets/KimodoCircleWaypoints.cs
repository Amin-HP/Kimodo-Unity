// Example EXTERNAL script (not part of the Kimodo Bridge package): a CIRCULAR path of waypoints with a
// matching ring-shaped hand rail. Everything else — the rail curve, the upper-body arm constraints and
// the fit — lives in KimodoRailAuthor, shared with KimodoLineWaypoints.
//
// Put this on the same GameObject as a Humanoid character that already has a KimodoGenerator
// (GameObject ▸ Kimodo ▸ Set Up Selected Character). It auto-adds a KimodoWaypoints component.

using System.Collections.Generic;
using UnityEngine;
using AminHP.KimodoBridge;

[RequireComponent(typeof(KimodoGenerator))]
public class KimodoCircleWaypoints : KimodoRailAuthor
{
    [Header("Circle")]
    [Tooltip("Pivot of the circle. If 'Start at character' is on, this is ignored and the loop is " +
             "placed so the character's current position is the first waypoint.")]
    public Transform centerTransform;
    public Vector3 center;                       // used when centerTransform is null
    [Min(0.1f)] public float radius = 2f;
    [Tooltip("Angle (deg, 0 = +X) of the first waypoint around the circle.")]
    public float startAngleDeg = 0f;
    public bool clockwise = false;
    [Tooltip("Add a closing waypoint back at the start position on the last frame (full loop).")]
    public bool closeLoop = true;
    [Tooltip("Place the loop so the character's current position is the first waypoint (frame 0).")]
    public bool startAtCharacter = true;

    [Tooltip("Distance from the walking circle to the rail: + = outside the circle, - = inside. Keep it " +
             "within about an arm's length, or move the body to it with 'Fit shoulder to rail'.")]
    public float curveRadialOffset = 0.55f;

    // ---- path ---------------------------------------------------------------------------------

    /// <summary>Populate the KimodoWaypoints component with a circular loop.</summary>
    public override void BuildWaypoints()
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

    /// <summary>Kept for callers of the old API — same as BuildWaypoints().</summary>
    public void BuildCircle() => BuildWaypoints();

    // ---- rail ---------------------------------------------------------------------------------

    // A ring concentric with the walking circle: the loop closes, so arc positions wrap around it.
    protected override bool RailClosed => true;
    protected override float RailGroundY => ResolveCenter(Gen).y;

    protected override Vector3 RailPoint(float t)
    {
        Vector3 c = ResolveCenter(Gen);
        float a = startAngleDeg * Mathf.Deg2Rad + (clockwise ? -1f : 1f) * 2f * Mathf.PI * t;
        float r = Mathf.Max(0.01f, radius + curveRadialOffset);
        var p = c + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
        p.y = c.y + curveHeight + CurveWave(t);
        return p;
    }

    /// <summary>World point on the rail at an angle around the circle (radians, 0 = +X).</summary>
    public Vector3 CurvePointAtAngle(float aRad)
    {
        float turn = (clockwise ? -1f : 1f) * 2f * Mathf.PI;
        return RailPoint(Mathf.Repeat((aRad - startAngleDeg * Mathf.Deg2Rad) / turn, 1f));
    }

    // ---- gizmo --------------------------------------------------------------------------------

    protected override void DrawPathGizmo()
    {
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

    // ---- helpers ------------------------------------------------------------------------------

    private Vector3 ResolveCenter(KimodoGenerator g)
    {
        if (startAtCharacter && g != null)
        {
            // Offset the center so the first waypoint (at startAngle) lands on the character.
            var charPos = g.ResolvedTarget != null ? g.ResolvedTarget.transform.position : transform.position;
            float a0 = startAngleDeg * Mathf.Deg2Rad;
            return charPos - new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)) * radius;
        }
        return centerTransform != null ? centerTransform.position : center;
    }
}
