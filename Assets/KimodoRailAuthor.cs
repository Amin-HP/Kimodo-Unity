// Example EXTERNAL script (not part of the Kimodo Bridge package) showing how to drive the plugin from
// your own code. This is the shared half: a path of waypoints for the feet + a "hand rail" curve for
// the arms. The path shape itself is left to the subclasses — see KimodoCircleWaypoints (a loop) and
// KimodoLineWaypoints (a straight run).
//
// Put a subclass on the same GameObject as a Humanoid character that already has a KimodoGenerator
// (GameObject ▸ Kimodo ▸ Set Up Selected Character). It auto-adds a KimodoWaypoints component.
//
// Because the world↔Kimodo mapping is measured from an existing motion, waypoints only take effect on
// a SECOND generate. "Build + Generate" does that for you: it generates once to establish the mapping
// (if needed), then builds the path and generates again with the constraint applied.
//
// The rail carries UPPER-BODY-ONLY pose constraints (shoulder → arm → elbow → hand, left and/or right)
// that lay the arm ALONG the curve. All three joints are put ON it: the BODY is moved (root height,
// optionally sideways) so the shoulder lands on the rail — a shoulder is carried by the torso, no
// rotation can place it — and then the upper arm and forearm are rotated to the next two points along
// the curve. Those points are found by intersecting the curve with a sphere of the bone's own length,
// so each bone reaches its target exactly and the arm traces the shape of the line.

using System.Collections.Generic;
using UnityEngine;
using AminHP.KimodoBridge;
#if UNITY_EDITOR
using UnityEditor;
#endif

[RequireComponent(typeof(KimodoGenerator))]
public abstract class KimodoRailAuthor : MonoBehaviour
{
    public enum CurveShape { Sine, Noise, Flat }
    public enum ArmSide { Left, Right, Both }
    public enum ShoulderFit { None, Height, HeightAndSideways }

    [Header("Path")]
    [Tooltip("How many waypoints to lay along the path.")]
    [Range(2, 64)] public int resolution = 8;

    [Tooltip("Also constrain facing so the character looks along the direction of travel.")]
    public bool faceAlongPath = true;

    [Tooltip("Rotation added to each waypoint's facing (deg). 0 = along the path; ±90 = face sideways " +
             "(a side-step / crab walk, which also puts the rail across the body); 180 = face backward.")]
    public float facingOffsetDeg = 0f;

    [Header("Pose")]
    [Tooltip("Duplicate the FIRST pose key (on a sibling KimodoPoseConstraints) onto every waypoint " +
             "frame, with its root moved onto the path so the pose travels along it. Author one pose " +
             "first (e.g. an upper-body partial pose), then build.")]
    public bool addPoseConstraints = false;

    [Tooltip("Show the duplicated pose ghosts in the Scene (off by default — one per waypoint is heavy).")]
    public bool showPoseGhosts = false;

    [Header("Hand rail (curve above the ground)")]
    [Tooltip("Draw the rail in the Scene view (yellow), so you can place it before generating.")]
    public bool showCurve = true;

    [Tooltip("Sine = regular waves; Noise = a random wobble; Flat = a level rail.")]
    public CurveShape curveShape = CurveShape.Sine;

    [Tooltip("Height of the rail above the path's ground plane (metres). Roughly shoulder height is " +
             "the easiest for the arm to reach.")]
    public float curveHeight = 1.3f;

    [Tooltip("How far the rail rises and falls around that height (metres). 0 = a level rail.")]
    public float curveAmplitude = 0.3f;

    [Tooltip("Waves along the whole rail. On a closed loop, whole numbers keep it seamless.")]
    [Range(0.5f, 12f)] public float curveWaves = 3f;

    [Tooltip("Slides the wave pattern along the rail (deg).")]
    public float curvePhaseDeg = 0f;

    [Tooltip("Seed for the Noise shape.")]
    public int curveSeed = 0;

    [Range(16, 512)] public int curveResolution = 128;

    [Header("Arm constraints (upper body only)")]
    [Tooltip("Build one pose key per waypoint that constrains ONLY the arm joints (shoulder, upper arm, " +
             "elbow, hand) and lays the arm along the rail. The rest of the body is left free.")]
    public bool addArmConstraints = false;

    [Tooltip("Which arm follows the rail.")]
    public ArmSide armSide = ArmSide.Right;

    [Tooltip("Include the shoulder (clavicle) in the constraint. Off = only upper arm / elbow / hand, " +
             "which leaves the model more freedom in the torso.")]
    public bool includeShoulder = true;

    [Tooltip("Also pin the spine (Hips/Spine1/Spine2/Chest) so the torso can't drift. Off = arms only.")]
    public bool includeTorso = false;

    [Tooltip("Turn each pose key's hips to face the way its own waypoint faces, instead of inheriting " +
             "the facing of the frame it was seeded from. Without this a key seeded from a straight " +
             "walk keeps that walk's heading while the path leads somewhere else — the disagreement " +
             "mirrors the arms on part of the path and makes the pose's own heading constraint fight " +
             "the waypoint's.")]
    public bool alignPoseFacing = true;

    [Header("Fit to the rail")]
    [Tooltip("How the BODY is moved to put the shoulder on the rail (a shoulder is carried by the " +
             "torso, so no rotation can place it).\n\n" +
             "Height = move the skeleton up/down only; keeps the walking path, but the arm can only " +
             "reach the rail if it is within arm's length sideways.\n" +
             "Height And Sideways = also move it across so the shoulder sits exactly ON the rail; the " +
             "waypoints are updated to match, so the walk follows the body.\n" +
             "None = don't move the body; rotations only.")]
    public ShoulderFit fitShoulderToRail = ShoulderFit.HeightAndSideways;

    [Tooltip("With BOTH arms: send them opposite ways along the rail, so the character opens up and " +
             "each hand rides a different side of the curve. Off = both arms follow the same direction.")]
    public bool spreadArms = true;

    [Tooltip("Swaps which arm goes which way along the rail, for EVERY key at once.")]
    public bool swapArmDirections = false;

    [Tooltip("Manual per-key override: tick an element to reverse that one key's arms. The list is " +
             "sized to the number of pose keys on every build, and element N is the key at waypoint N " +
             "— the Scene view labels each waypoint with its number (a ticked one reads \"3 ⇄\").")]
    public List<bool> flipArmDirection = new List<bool>();

    [Tooltip("Slides the arm along the rail before fitting (metres of curve). + = further along the " +
             "walking direction, - = back behind the character. 0 = start beside the shoulder. " +
             "With 'Spread arms' it slides each arm outward along its own direction.")]
    public float railStartOffset = 0f;

    [Tooltip("Reach used to find the ELBOW's point on the rail. 0 = the character's real upper-arm " +
             "length, which is what lands the elbow exactly ON the curve; any other value and the " +
             "bone can't quite reach the point it aims at.")]
    public float railElbowOffset = 0f;

    [Tooltip("Reach used to find the HAND's point on the rail, measured from the elbow. 0 = the " +
             "character's real forearm length (hand exactly on the curve).")]
    public float railHandOffset = 0f;

    [Header("Refs (auto-found)")]
    public KimodoGenerator generator;
    public KimodoWaypoints waypoints;

    protected KimodoGenerator Gen => generator != null ? generator : (generator = GetComponent<KimodoGenerator>());
    protected KimodoWaypoints Wp =>
        waypoints != null ? waypoints : (waypoints = GetComponent<KimodoWaypoints>() ?? gameObject.AddComponent<KimodoWaypoints>());

    // ---- what each path shape must supply ----------------------------------------------------

    /// <summary>Populate the KimodoWaypoints component with this shape's path.</summary>
    public abstract void BuildWaypoints();

    /// <summary>World point on the rail at t (0..1 along it). Include the height: usually
    /// <c>RailGroundY + curveHeight + CurveWave(t)</c>.</summary>
    protected abstract Vector3 RailPoint(float t);

    /// <summary>True when the rail joins back to itself (a loop), so arc positions wrap instead of clamp.</summary>
    protected abstract bool RailClosed { get; }

    /// <summary>Ground plane the path and rail are measured from.</summary>
    protected abstract float RailGroundY { get; }

    /// <summary>Draw the planned path in the Scene view (the rail is drawn for you).</summary>
    protected abstract void DrawPathGizmo();

    // ---- public API --------------------------------------------------------------------------

    /// <summary>Build the path and generate motion that follows it (two-pass on the first run so the
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
            // No motion yet: generate once to establish the mapping + frame count, then apply the path.
            g.Generate(() => { BuildAll(); g.Generate(); });
        }
        else
        {
            BuildAll();
            g.Generate();
        }
    }

    // The path, then whichever pose constraints are enabled. Arm constraints supersede the plain pose
    // duplication (they can still SEED from the first authored key — see BuildArmPoses).
    protected void BuildAll()
    {
        BuildWaypoints();
        if (addArmConstraints) BuildArmPoses();
        else if (addPoseConstraints) BuildPoses();
    }

    /// <summary>Duplicate the first authored pose key onto every waypoint frame, moving each copy's root
    /// onto the path so the pose travels along it rather than freezing in one spot.</summary>
    public void BuildPoses()
    {
        var g = Gen; var wp = Wp;
        var pc = GetComponent<KimodoPoseConstraints>();
        if (pc == null) pc = gameObject.AddComponent<KimodoPoseConstraints>();

        if (g.Motion == null || !g.IsPreviewBound)
        { Debug.LogWarning("[Kimodo] Generate once first so the pose root can follow the path."); return; }
        if (wp.waypoints == null || wp.waypoints.Count == 0)
        { Debug.LogWarning("[Kimodo] Build the waypoints first."); return; }
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
            // Move the pose's root onto the waypoint so it travels the path (keep the authored height).
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

    // ---- hand rail ---------------------------------------------------------------------------

    /// <summary>Vertical offset of the rail at t (0..1): a sine, noise, or nothing.</summary>
    protected float CurveWave(float t)
    {
        float ph = curvePhaseDeg * Mathf.Deg2Rad;
        switch (curveShape)
        {
            case CurveShape.Sine:
                return curveAmplitude * Mathf.Sin(curveWaves * 2f * Mathf.PI * t + ph);

            case CurveShape.Noise:
                float ox = 50f + (curveSeed * 0.7371f) % 97f;
                float oy = 50f + (curveSeed * 1.3137f) % 89f;
                float n;
                if (RailClosed)
                {
                    // Sampled around a CIRCLE in noise space, so the pattern wraps seamlessly back to
                    // the start of the loop (sampling along t itself would leave a seam).
                    float a = 2f * Mathf.PI * t + ph;
                    float nr = Mathf.Max(0.05f, curveWaves * 0.25f);
                    n = Mathf.PerlinNoise(ox + Mathf.Cos(a) * nr, oy + Mathf.Sin(a) * nr);
                }
                else
                {
                    // Open rail: no seam to hide, so walk the noise field in a straight line.
                    n = Mathf.PerlinNoise(ox + (t + ph / (2f * Mathf.PI)) * curveWaves, oy);
                }
                return curveAmplitude * (n * 2f - 1f);

            default:
                return 0f;
        }
    }

    // The rail sampled into KIMODO space as a polyline with cumulative arc length, so the arm can be
    // fitted by walking a distance ALONG the curve rather than by parameter. Kimodo units are metres
    // (and the world scale k is ~1), so the inspector offsets read as real metres.
    protected struct Rail
    {
        public Vector3[] p;    // samples; for a closed rail p[last] == p[0]
        public float[] s;      // arc length at each sample
        public float total;
        public bool closed;
    }

    private Rail BuildRail(in KimodoRootMap.Map map)
    {
        int n = Mathf.Max(16, curveResolution);
        var r = new Rail { p = new Vector3[n + 1], s = new float[n + 1], closed = RailClosed };
        for (int i = 0; i <= n; i++)
            r.p[i] = KimodoRootMap.WorldToKimodo(RailPoint((float)i / n), map);
        for (int i = 1; i <= n; i++) r.s[i] = r.s[i - 1] + Vector3.Distance(r.p[i - 1], r.p[i]);
        r.total = r.s[n];
        return r;
    }

    /// <summary>Arc length of the rail sample nearest a point — where the arm starts from.</summary>
    private static float NearestArc(in Rail rail, Vector3 k)
    {
        float best = float.MaxValue; int bi = 0;
        for (int i = 0; i < rail.p.Length - 1; i++)
        {
            float d = (rail.p[i] - k).sqrMagnitude;
            if (d < best) { best = d; bi = i; }
        }
        return rail.s[bi];
    }

    /// <summary>The point on the rail exactly <paramref name="radius"/> away from <paramref name="from"/>,
    /// walking from arc <paramref name="sFrom"/> in direction <paramref name="dir"/> (±1 along the
    /// curve — the two arms use opposite signs when spread) — i.e. where a sphere of the bone's length
    /// crosses the curve. That crossing is what lets a fixed-length bone land ON the curve instead of
    /// short of it. If the curve never comes that close, the nearest match in the window is used.</summary>
    private static Vector3 ArcAtDistance(in Rail rail, float sFrom, Vector3 from, float radius, int dir, out float sHit)
    {
        const int steps = 64;
        float window = Mathf.Max(radius * 3f, 0.05f);   // arc >= chord, so 3x the reach is plenty
        float prevS = sFrom;
        float prevD = Vector3.Distance(PointAtArc(rail, sFrom), from) - radius;   // < 0 = inside the sphere
        float bestS = sFrom, bestErr = Mathf.Abs(prevD);

        for (int i = 1; i <= steps; i++)
        {
            float s = sFrom + dir * window * i / steps;
            float d = Vector3.Distance(PointAtArc(rail, s), from) - radius;
            if (prevD <= 0f && d >= 0f)   // crossed: interpolate for the exact distance
            {
                float t = Mathf.Abs(d - prevD) > 1e-6f ? -prevD / (d - prevD) : 0f;
                sHit = Mathf.Lerp(prevS, s, t);
                return PointAtArc(rail, sHit);
            }
            if (Mathf.Abs(d) < bestErr) { bestErr = Mathf.Abs(d); bestS = s; }
            prevS = s; prevD = d;
        }
        sHit = bestS;
        return PointAtArc(rail, bestS);
    }

    /// <summary>Point at an arc length along the rail. A closed rail wraps; an open one stops at its ends.</summary>
    private static Vector3 PointAtArc(in Rail rail, float s)
    {
        if (rail.total <= 1e-5f) return rail.p[0];
        s = rail.closed ? Mathf.Repeat(s, rail.total) : Mathf.Clamp(s, 0f, rail.total);
        int lo = 0, hi = rail.p.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (rail.s[mid] <= s) lo = mid; else hi = mid;
        }
        float seg = rail.s[hi] - rail.s[lo];
        return Vector3.Lerp(rail.p[lo], rail.p[hi], seg > 1e-6f ? (s - rail.s[lo]) / seg : 0f);
    }

    // ---- arm (upper-body) pose constraints ---------------------------------------------------

    /// <summary>Build one pose key per waypoint with the arm(s) laid ALONG the rail: the body is moved
    /// so the shoulder sits on the curve, then the elbow and hand are rotated onto the next two points
    /// down it. Only the arm joints are constrained (plus the spine if asked), so the rest of the body
    /// stays free, and each key's root sits on its waypoint so the pose travels the path.</summary>
    public void BuildArmPoses()
    {
        var g = Gen; var wp = Wp;
        if (g == null || wp == null) return;

        if (g.Motion == null || !g.IsPreviewBound)
        { Debug.LogWarning("[Kimodo] Generate once first so the arm poses can be placed along the path."); return; }
        if (wp.waypoints == null || wp.waypoints.Count == 0)
        { Debug.LogWarning("[Kimodo] Build the waypoints first."); return; }

        var motion = g.PoseSkeleton;
        int J = motion.jointCount;
        var map = KimodoRootMap.Compute(g);
        if (!map.valid)
        { Debug.LogWarning("[Kimodo] Couldn't measure the world↔Kimodo mapping (no Humanoid hips?)."); return; }

        var pc = GetComponent<KimodoPoseConstraints>();
        if (pc == null) pc = gameObject.AddComponent<KimodoPoseConstraints>();
        var clip = g.Motion.clips[Mathf.Clamp(g.clipIndex, 0, g.Motion.clips.Count - 1)];

        // Optional base pose: with 'Add pose constraints' on, the first authored key is used as the
        // body pose and only its arms are overwritten. Otherwise each key starts from the walk itself.
        float[] seedQ = null; float seedY = 0f;
        if (addPoseConstraints && pc.keys != null && pc.keys.Count > 0)
        {
            var s = pc.keys[0];
            if (s.hasPose && s.localQuats != null && s.localQuats.Length == J * 4) { seedQ = s.localQuats; seedY = s.root.y; }
        }

        var rail = BuildRail(map);
        var mask = ArmMask(motion, J);
        var keys = new List<KimodoPoseConstraints.Key>(wp.waypoints.Count);
        var sources = new List<KimodoWaypoints.Waypoint>(wp.waypoints.Count);   // waypoint per key, same order
        _missShoulder = _missElbow = _missHand = 0f; _missCount = 0;
        bool movedWaypoints = false;

        for (int wi = 0; wi < wp.waypoints.Count; wi++)
        {
            var w = wp.waypoints[wi];
            if (w.frame < 0 || w.frame >= g.Motion.frameCount) continue;
            var q = seedQ != null ? (float[])seedQ.Clone() : NaturalPose(clip, w.frame, J);
            if (q == null) continue;

            var key = new KimodoPoseConstraints.Key
            {
                frame = w.frame,
                hasPose = true,
                show = showPoseGhosts,
                localQuats = q,
                jointActive = (bool[])mask.Clone(),   // per key, so editing one key's joints stays local
            };

            // Root on the waypoint, at the pelvis height the walk already has there (or the seed's).
            var kroot = KimodoRootMap.WorldToKimodo(w.world, map);
            key.root = new Vector3(kroot.x,
                                   seedQ != null ? seedY : clip.rootPositions[w.frame * 3 + 1],
                                   kroot.z);

            // Face the pose the way this waypoint faces. The seed frame carries its own heading (the
            // direction the source motion happened to be walking); on any path that turns, that heading
            // is wrong everywhere except where the two coincide.
            if (alignPoseFacing)
            {
                float hdg = WaypointHeadingDeg(wp, wi);
                if (!float.IsNaN(hdg)) AlignKeyFacing(motion, key, hdg, map);
            }

            keys.Add(key);
            sources.Add(w);
        }

        if (keys.Count == 0) { Debug.LogWarning("[Kimodo] No arm pose keys built (waypoint frames out of range?)."); return; }

        // ONE arm direction for the whole path, by majority vote of the keys. Measuring per key means a
        // single key whose seed pose reads as facing the other way reverses just that key's arms — which
        // is what leaves a few keys looking mirrored while the rest are fine. The vote is reported below;
        // if it isn't unanimous the seed motion doesn't follow the path yet (generate again and rebuild).
        int vote = 0, across = 0;
        var signs = new List<int>(keys.Count);
        foreach (var k in keys)
        {
            KimodoFK.GlobalPose(motion, ClipView(k), 0, out var gp, out var gr);
            int s = RailSideSign(rail, motion, gp, gr, out bool acrossBody);
            signs.Add(s); vote += s;
            if (acrossBody) across++;
        }
        int fwd = vote >= 0 ? 1 : -1;
        int agree = 0;
        foreach (var s in signs) if (s == fwd) agree++;

        // Keep the manual override list in step with the keys (existing ticks are preserved).
        if (flipArmDirection.Count != keys.Count)
        {
            RecordUndo(this, "Resize arm flips");
            while (flipArmDirection.Count < keys.Count) flipArmDirection.Add(false);
            while (flipArmDirection.Count > keys.Count) flipArmDirection.RemoveAt(flipArmDirection.Count - 1);
            SetDirty(this);
        }

        for (int i = 0; i < keys.Count; i++)
        {
            var delta = AimArmsAtRail(motion, keys[i], rail, flipArmDirection[i] ? -fwd : fwd);

            // The body was moved sideways to reach the rail, so move this waypoint with it — otherwise
            // the root path would pull the character back off the rail while the pose pushes it on.
            if (fitShoulderToRail == ShoulderFit.HeightAndSideways &&
                (Mathf.Abs(delta.x) > 1e-5f || Mathf.Abs(delta.z) > 1e-5f))
            {
                if (!movedWaypoints) { RecordUndo(wp, "Move waypoints onto the rail"); movedWaypoints = true; }
                var moved = KimodoRootMap.KimodoToWorld(keys[i].root, map);
                sources[i].world = new Vector3(moved.x, sources[i].world.y, moved.z);
            }
        }
        if (movedWaypoints) SetDirty(wp);

        RecordUndo(pc, "Build arm rail poses");
        pc.keys = keys;
        SetDirty(pc);

        // How close each joint actually got to the rail. All three go to ~0 with 'Height And Sideways';
        // in 'Height' mode the shoulder number is how far the rail is sideways, and if it exceeds the
        // arm's length the elbow and hand can't reach either — move the rail closer to the path.
        if (_missCount > 0)
            Debug.Log($"[Kimodo] Arm rail: {keys.Count} pose keys, rail runs "
                    + (across * 2 >= keys.Count ? "ACROSS the body (left arm left, right arm right)"
                                                : "ALONG the body (left arm ahead, right arm behind)")
                    + $", direction agreed by {agree}/{keys.Count} (all keys use the majority). "
                    + $"Off the curve by (average) — shoulder "
                    + $"{_missShoulder / _missCount * 100f:F1} cm, elbow {_missElbow / _missCount * 100f:F1} cm, "
                    + $"hand {_missHand / _missCount * 100f:F1} cm.");
    }

    // FK the key's pose, move the body so the shoulder is on the rail, then fit each arm. Returns the
    // translation applied to the root (so the caller can move the waypoint with it). The two arms are
    // independent chains (both hang off the chest), so fitting one doesn't disturb the other.
    //
    // 'fwd' is the path-wide arm direction agreed in BuildArmPoses — never re-decided here, so no key
    // can end up reaching the opposite way to its neighbours.
    private Vector3 AimArmsAtRail(KimodoMotion motion, KimodoPoseConstraints.Key key, in Rail rail, int fwd)
    {
        KimodoFK.GlobalPose(motion, ClipView(key), 0, out var gpos, out var grot);

        // A shoulder can't be rotated onto the rail — it's carried by the torso — so move the whole
        // skeleton instead. Shifting the root is a rigid translation, so every FK position just moves
        // with it and nothing has to be re-solved.
        Vector3 delta = Vector3.zero;
        if (fitShoulderToRail != ShoulderFit.None)
        {
            delta = ShoulderShift(motion, gpos, rail, fwd);
            if (fitShoulderToRail == ShoulderFit.Height) delta = new Vector3(0f, delta.y, 0f);
            if (delta.sqrMagnitude > 1e-10f)
            {
                key.root += delta;
                for (int i = 0; i < gpos.Length; i++) gpos[i] += delta;
            }
        }

        if (armSide != ArmSide.Right) FitOneArm(motion, key, gpos, grot, "Left", rail, LeftSign * fwd);
        if (armSide != ArmSide.Left) FitOneArm(motion, key, gpos, grot, "Right", rail, RightSign * fwd);
        return delta;
    }

    // Which arm reaches "ahead" along the rail. Spread sends them opposite ways from the shoulders, so
    // the character opens up along the curve instead of both arms reaching the same way.
    private int LeftSign => swapArmDirections ? -1 : 1;
    private int RightSign => (spreadArms && armSide == ArmSide.Both) ? -LeftSign : LeftSign;

    // Which way along the rail the LEFT arm should reach, as a sign on the arc parameter. The rail's own
    // direction is no use for this: the pose seeding a key can face any way, so it has to come from the
    // body. Whichever body axis the rail actually runs along decides:
    //
    //   rail beside the character (walking along it) -> fore/aft: left arm ahead, right arm behind
    //   rail across the character (facing it, crab-walking) -> left/right: left arm left, right right
    //
    // Picking the dominant axis matters — in a crab walk the rail is perpendicular to the body's
    // forward, so a forward-only test is measuring ~zero and its sign is a coin flip.
    private static int RailSideSign(in Rail rail, KimodoMotion motion, Vector3[] gpos, Quaternion[] grot,
                                    out bool acrossBody)
    {
        acrossBody = false;
        int root = motion.rootIndex >= 0 && motion.rootIndex < gpos.Length ? motion.rootIndex : 0;

        // Body axes from JOINT POSITIONS rather than the hips' rotation: the shoulder line is the body's
        // left, the spine is its up, and left × up = forward in Kimodo's frame (+X left, +Y up, +Z fwd).
        // Positions are what FK demonstrably gets right, while a pitched-over hips rotation flattens to
        // a near-random heading — which is what flips individual keys.
        Vector3 toLeft = Vector3.zero, fwd = Vector3.zero;
        int iL = KimodoFK.BoneIndex(motion, "LeftArm");
        int iR = KimodoFK.BoneIndex(motion, "RightArm");
        int iUp = UpperSpineIndex(motion);
        if (iL >= 0 && iR >= 0 && iUp >= 0)
        {
            toLeft = gpos[iL] - gpos[iR];
            fwd = Vector3.Cross(toLeft, gpos[iUp] - gpos[root]);
        }
        if (toLeft.sqrMagnitude < 1e-8f) toLeft = grot[root] * Vector3.right;     // Kimodo +X is the body's left
        if (fwd.sqrMagnitude < 1e-8f) fwd = grot[root] * Vector3.forward;
        toLeft.y = 0f; fwd.y = 0f;
        if (toLeft.sqrMagnitude < 1e-8f || fwd.sqrMagnitude < 1e-8f) return 1;

        float s = NearestArc(rail, gpos[root]);
        float e = Mathf.Max(0.01f, rail.total * 0.005f);
        Vector3 tan = PointAtArc(rail, s + e) - PointAtArc(rail, s - e);   // +arc direction
        tan.y = 0f;
        if (tan.sqrMagnitude < 1e-8f) return 1;
        tan.Normalize();

        float alongBody = Vector3.Dot(tan, fwd.normalized);
        float sideways = Vector3.Dot(tan, toLeft.normalized);
        acrossBody = Mathf.Abs(sideways) > Mathf.Abs(alongBody);
        return (acrossBody ? sideways : alongBody) >= 0f ? 1 : -1;
    }

    private static int UpperSpineIndex(KimodoMotion m)
    {
        foreach (var n in new[] { "Chest", "Spine2", "Spine1", "Neck1" })
        {
            int i = KimodoFK.BoneIndex(m, n);
            if (i >= 0) return i;
        }
        return -1;
    }

    // Translation that lands the shoulder on its anchor point on the rail (averaged when both arms
    // are used, since one body can't put two shoulders on the same curve).
    private Vector3 ShoulderShift(KimodoMotion motion, Vector3[] gpos, in Rail rail, int fwd)
    {
        Vector3 sum = Vector3.zero; int n = 0;
        if (armSide != ArmSide.Right && ShoulderDelta(motion, gpos, "Left", rail, LeftSign * fwd, out var dl)) { sum += dl; n++; }
        if (armSide != ArmSide.Left && ShoulderDelta(motion, gpos, "Right", rail, RightSign * fwd, out var dr)) { sum += dr; n++; }
        return n > 0 ? sum / n : Vector3.zero;
    }

    private bool ShoulderDelta(KimodoMotion motion, Vector3[] gpos, string side, in Rail rail, int dir, out Vector3 delta)
    {
        delta = Vector3.zero;
        int iArm = KimodoFK.BoneIndex(motion, side + "Arm");
        if (iArm < 0) return false;
        Vector3 shoulder = gpos[iArm];
        delta = PointAtArc(rail, NearestArc(rail, shoulder) + railStartOffset * dir) - shoulder;
        return true;
    }

    // Lay one arm along the rail. Two aims down the chain: the upper arm points at the ELBOW target,
    // then the forearm points from wherever the elbow actually landed at the HAND target. Each bone
    // keeps its own length, so the joints land on the curve exactly when the offsets match the bone
    // lengths (the default) — and the arm bends WITH the curve instead of staying a straight ray.
    // The wrist keeps its natural local rotation.
    private void FitOneArm(KimodoMotion motion, KimodoPoseConstraints.Key key,
                           Vector3[] gpos, Quaternion[] grot, string side, in Rail rail, int dir)
    {
        int iArm = KimodoFK.BoneIndex(motion, side + "Arm");
        int iFore = KimodoFK.BoneIndex(motion, side + "ForeArm");
        int iHand = KimodoFK.BoneIndex(motion, side + "Hand");
        if (iArm < 0 || iFore < 0 || iHand < 0) return;

        // Bone lengths are the rest offsets (FK places a child at parent + parentRotation * offset).
        Vector3 offFore = BoneOffset(motion, iFore);   // upper arm, in the shoulder joint's frame
        Vector3 offHand = BoneOffset(motion, iHand);   // forearm, in the elbow's frame
        float lUpper = offFore.magnitude, lLower = offHand.magnitude;
        if (lUpper < 1e-5f || lLower < 1e-5f) return;

        // Walk the rail from the shoulder's anchor: the elbow's point is where the curve is exactly one
        // upper arm away, the hand's point is one forearm on from THERE. Using the sphere crossing (not
        // a flat arc-length step) is what makes each bone actually land on the curve.
        float rEl = railElbowOffset > 0f ? railElbowOffset : lUpper;
        float rHa = railHandOffset > 0f ? railHandOffset : lLower;

        Vector3 shoulder = gpos[iArm];
        float sSho = NearestArc(rail, shoulder) + railStartOffset * dir;
        Vector3 elbowTarget = ArcAtDistance(rail, sSho, shoulder, rEl, dir, out float sEl);
        Vector3 handTarget = ArcAtDistance(rail, sEl, elbowTarget, rHa, dir, out _);

        int par = motion.bones[iArm].parent;
        Quaternion gPar = par >= 0 ? grot[par] : Quaternion.identity;

        // 1) upper arm -> elbow target (minimal-twist: rotate its current global direction onto the new one)
        Vector3 dirA = elbowTarget - shoulder;
        if (dirA.sqrMagnitude < 1e-8f) return;
        dirA.Normalize();
        Quaternion gArm = Quaternion.FromToRotation((grot[iArm] * offFore).normalized, dirA) * grot[iArm];
        Vector3 elbow = shoulder + dirA * lUpper;

        // 2) forearm -> hand target, measured from where the elbow ACTUALLY ended up
        Vector3 dirB = handTarget - elbow;
        dirB = dirB.sqrMagnitude < 1e-8f ? dirA : dirB.normalized;
        Quaternion gFore = Quaternion.FromToRotation((grot[iFore] * offHand).normalized, dirB) * grot[iFore];
        Vector3 hand = elbow + dirB * lLower;

        SetLocal(key, iArm, Quaternion.Inverse(gPar) * gArm);
        SetLocal(key, iFore, Quaternion.Inverse(gArm) * gFore);

        // How far each joint ended up from the rail — reported after the build so the fit can be tuned.
        _missShoulder += Vector3.Distance(shoulder, PointAtArc(rail, sSho));
        _missElbow += Vector3.Distance(elbow, elbowTarget);
        _missHand += Vector3.Distance(hand, handTarget);
        _missCount++;
    }

    // Fit error accumulated over one BuildArmPoses call (metres), for the summary log.
    private float _missShoulder, _missElbow, _missHand;
    private int _missCount;

    /// <summary>Per-joint active mask (length J): only the arm joints — plus the spine if asked — are
    /// constrained, so this is a partial (upper-body) pose and the model is free everywhere else.</summary>
    private bool[] ArmMask(KimodoMotion motion, int J)
    {
        var a = new bool[J];
        void On(string n) { int i = KimodoFK.BoneIndex(motion, n); if (i >= 0) a[i] = true; }

        if (armSide != ArmSide.Right)
        {
            if (includeShoulder) On("LeftShoulder");
            On("LeftArm"); On("LeftForeArm"); On("LeftHand");
        }
        if (armSide != ArmSide.Left)
        {
            if (includeShoulder) On("RightShoulder");
            On("RightArm"); On("RightForeArm"); On("RightHand");
        }
        if (includeTorso) { On("Hips"); On("Spine1"); On("Spine2"); On("Chest"); }
        return a;
    }

    private static Vector3 BoneOffset(KimodoMotion motion, int j)
    {
        var b = motion.bones[j];
        return new Vector3(b.ox, b.oy, b.oz);
    }

    // Write a Kimodo local rotation into the key (stored wxyz).
    private static void SetLocal(KimodoPoseConstraints.Key key, int j, Quaternion q)
    {
        key.localQuats[j * 4 + 0] = q.w;
        key.localQuats[j * 4 + 1] = q.x;
        key.localQuats[j * 4 + 2] = q.y;
        key.localQuats[j * 4 + 3] = q.z;
    }

    /// <summary>World yaw a waypoint should face: its own constrained heading, else the direction of
    /// travel to the next waypoint plus the facing offset. NaN when it can't be determined.</summary>
    private float WaypointHeadingDeg(KimodoWaypoints wpc, int i)
    {
        if (i < 0 || i >= wpc.waypoints.Count) return float.NaN;
        var w = wpc.waypoints[i];
        if (w.constrainFacing) return w.headingDeg;

        int n = wpc.waypoints.Count;
        if (n < 2) return float.NaN;
        Vector3 d = wpc.waypoints[Mathf.Min(i + 1, n - 1)].world - w.world;
        if (d.sqrMagnitude < 1e-8f && i > 0) d = w.world - wpc.waypoints[i - 1].world;
        d.y = 0f;
        if (d.sqrMagnitude < 1e-8f) return float.NaN;
        return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg + facingOffsetDeg;
    }

    // Yaw a key's whole pose about the pelvis so the body faces 'worldHeadingDeg'. The root joint has no
    // parent, so its local rotation IS its global one and turning it turns the entire pose; the root
    // position is the pivot, so the key stays on its waypoint.
    private static void AlignKeyFacing(KimodoMotion motion, KimodoPoseConstraints.Key key,
                                       float worldHeadingDeg, in KimodoRootMap.Map map)
    {
        int root = motion.rootIndex >= 0 ? motion.rootIndex : 0;
        if (key.localQuats == null || key.localQuats.Length < (root + 1) * 4) return;

        // World yaw -> Kimodo heading, the same conversion KimodoWaypoints.HeadingCosSin uses (so the
        // pose's heading and the waypoint's heading constraint agree instead of pulling against each other).
        Vector3 dirWorld = Quaternion.Euler(0f, worldHeadingDeg, 0f) * Vector3.forward;
        Vector3 dK = Quaternion.Inverse(map.charRot) * dirWorld;
        dK = new Vector3(-dK.x, 0f, dK.z);                     // FlipX, flattened
        if (dK.sqrMagnitude < 1e-8f) return;
        float want = Mathf.Atan2(dK.x, dK.z) * Mathf.Rad2Deg;   // Kimodo heading, 0 = +Z

        int b = root * 4;
        var q = new Quaternion(key.localQuats[b + 1], key.localQuats[b + 2], key.localQuats[b + 3], key.localQuats[b + 0]);
        Vector3 f = q * Vector3.forward; f.y = 0f;
        if (f.sqrMagnitude < 1e-8f) return;
        float have = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;

        // Try the yaw both ways and keep whichever actually lands on the wanted heading, rather than
        // relying on a handedness argument about what a +Y rotation does to right-handed Kimodo data.
        float d = Mathf.DeltaAngle(have, want);
        Quaternion ccw = Quaternion.AngleAxis(d, Vector3.up) * q;
        Quaternion cw = Quaternion.AngleAxis(-d, Vector3.up) * q;
        Quaternion best = HeadingError(ccw, want) <= HeadingError(cw, want) ? ccw : cw;

        key.localQuats[b + 0] = best.w; key.localQuats[b + 1] = best.x;
        key.localQuats[b + 2] = best.y; key.localQuats[b + 3] = best.z;
    }

    private static float HeadingError(Quaternion q, float wantDeg)
    {
        Vector3 f = q * Vector3.forward; f.y = 0f;
        if (f.sqrMagnitude < 1e-8f) return 180f;
        return Mathf.Abs(Mathf.DeltaAngle(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg, wantDeg));
    }

    // A one-frame clip view over a key's pose, so KimodoFK can FK it.
    private static KimodoClip ClipView(KimodoPoseConstraints.Key key) => new KimodoClip
    {
        localQuats = key.localQuats,
        rootPositions = new[] { key.root.x, key.root.y, key.root.z },
    };

    // The walk's own pose at a frame, used as the body the arms are grafted onto.
    private static float[] NaturalPose(KimodoClip clip, int frame, int J)
    {
        if (clip == null || clip.localQuats == null || clip.localQuats.Length < (frame + 1) * J * 4) return null;
        var q = new float[J * 4];
        System.Array.Copy(clip.localQuats, frame * J * 4, q, 0, J * 4);
        return q;
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>Frames available to spread waypoints over: the current motion's, or an estimate from
    /// the generator's requested duration before anything has been generated.</summary>
    protected int FrameCount(KimodoGenerator g)
    {
        if (g.FrameCount > 1) return g.FrameCount;
        float fps = g.Fps > 0f ? g.Fps : 30f;
        float secs = 0f;
        foreach (var p in (g.duration ?? "").Split(' '))
            if (float.TryParse(p, out var s)) secs += s;
        if (secs <= 0f) secs = 4f;
        return Mathf.Max(2, Mathf.RoundToInt(secs * fps));
    }

    protected static void RecordUndo(Object o, string label)
    {
#if UNITY_EDITOR
        if (!Application.isPlaying) Undo.RecordObject(o, label);
#endif
    }

    protected static void SetDirty(Object o)
    {
#if UNITY_EDITOR
        if (!Application.isPlaying) EditorUtility.SetDirty(o);
#endif
    }

    // Preview the planned path + rail in the Scene view before generating.
    private void OnDrawGizmosSelected()
    {
        if (Gen == null) return;
        DrawPathGizmo();

#if UNITY_EDITOR
        // Number each waypoint so the 'Flip arm direction' elements can be matched to what you see.
        // Element N is the key at waypoint N; a ticked one is marked with ⇄.
        var wpc = GetComponent<KimodoWaypoints>();   // GetComponent, never Wp — gizmos must not add one
        if (wpc != null && wpc.waypoints != null)
        {
            for (int i = 0; i < wpc.waypoints.Count; i++)
            {
                bool flipped = i < flipArmDirection.Count && flipArmDirection[i];
                Handles.color = flipped ? new Color(1f, 0.5f, 0.3f) : new Color(0.7f, 0.85f, 1f);
                Handles.Label(wpc.waypoints[i].world + Vector3.up * 0.25f, flipped ? $"{i} ⇄" : i.ToString());
            }
        }
#endif

        if (!showCurve) return;

        // The hand rail, with occasional drop lines to the ground so its height reads in the viewport.
        float gy = RailGroundY;
        int cs = Mathf.Max(16, curveResolution);
        Vector3 prev = default;
        for (int i = 0; i <= cs; i++)
        {
            var p = RailPoint((float)i / cs);
            Gizmos.color = new Color(1f, 0.78f, 0.2f, 0.95f);
            if (i > 0) Gizmos.DrawLine(prev, p);
            if (i % Mathf.Max(1, cs / 24) == 0)
            {
                Gizmos.color = new Color(1f, 0.78f, 0.2f, 0.18f);
                Gizmos.DrawLine(p, new Vector3(p.x, gy, p.z));
            }
            prev = p;
        }
    }

#if UNITY_EDITOR
    [ContextMenu("Build Waypoints")]
    private void CtxBuild() { BuildWaypoints(); SceneView.RepaintAll(); }

    [ContextMenu("Duplicate Pose To Waypoints")]
    private void CtxPoses() { BuildPoses(); SceneView.RepaintAll(); }

    [ContextMenu("Build Arm Poses (rail)")]
    private void CtxArmPoses() { BuildArmPoses(); SceneView.RepaintAll(); }

    [ContextMenu("Build Waypoints + Generate")]
    private void CtxBuildGenerate() => BuildAndGenerate();

    [ContextMenu("Flip All Arm Directions")]
    private void CtxFlipAll()
    {
        Undo.RecordObject(this, "Flip all arm directions");
        for (int i = 0; i < flipArmDirection.Count; i++) flipArmDirection[i] = !flipArmDirection[i];
        EditorUtility.SetDirty(this);
        BuildArmPoses();
        SceneView.RepaintAll();
    }

    [ContextMenu("Clear Arm Flips")]
    private void CtxClearFlips()
    {
        Undo.RecordObject(this, "Clear arm flips");
        for (int i = 0; i < flipArmDirection.Count; i++) flipArmDirection[i] = false;
        EditorUtility.SetDirty(this);
        BuildArmPoses();
        SceneView.RepaintAll();
    }
#endif
}
