// SPDX-License-Identifier: Apache-2.0
// The timeline "file": an asset you assign to a KimodoGenerator (Assets ▸ Create ▸ Kimodo ▸ Timeline).
// It owns the ORDER of the shot — a list of prompt segments, each with its own duration — and can
// also hold a snapshot ("take") of the character's constraint keys so a shot can be saved/restored.
//
// Kimodo's multi-prompt API is really "one string split on periods" + "space-separated seconds per
// segment" (see the bridge server's _texts_and_frames). That is awkward to author by hand, so this
// asset is the structured form of exactly that: BuildPrompt() / BuildDuration() compose it back for
// /generate, and StartFrame()/FrameCountOf() give each segment its place on the frame axis.
//
// The constraint keys themselves live on the scene components (KimodoWaypoints / KimodoEndEffectors /
// KimodoPoseConstraints) — they hold WORLD positions and draw Scene gizmos, so the scene is their
// natural home. CaptureTake / ApplyTake copy them in and out of this asset on demand.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace AminHP.KimodoBridge
{
    [CreateAssetMenu(fileName = "Kimodo Timeline", menuName = "Kimodo/Timeline", order = 200)]
    public class KimodoTimeline : ScriptableObject
    {
        /// <summary>Master switch for frozen segments. Kept as a switch so the feature can be parked
        /// again without ripping the code out; see HANDOFF §7h for how freezing actually works now
        /// (frozen segments are NOT generated — only the runs between them are requested).</summary>
        /// <remarks>`static readonly`, not `const`: a const would be folded at compile time, making the
        /// guarded branches "unreachable code" warnings and baking the value into other assemblies.</remarks>
        public static readonly bool FreezeEnabled = true;

        [Serializable]
        public class Segment
        {
            [Tooltip("What happens during this segment. Keep it to one sentence — periods separate " +
                     "segments for Kimodo, so an interior period is turned into a comma when sent.")]
            [TextArea(2, 4)] public string prompt = "A person walks forward";

            [Tooltip("Seconds this segment lasts.")]
            [Min(0.1f)] public float seconds = 2f;

            [Tooltip("Use a different constraint strength during this segment (e.g. pin hard where it " +
                     "matters, let the model move freely elsewhere). Off = the Generator's global weight.")]
            public bool overrideConstraintWeight;

            [Tooltip("Constraint guidance weight for this segment only (Kimodo cfg_weight[1]).")]
            [Range(1f, 8f)] public float constraintWeight = 3f;

            [Tooltip("Keep this segment exactly as it was generated. Its frames are captured and put back " +
                     "after every Generate, so only the unfrozen segments actually change.")]
            public bool frozen;

            [Tooltip("The captured frames of a frozen segment.")]
            public FrozenClip frozenClip;

            public bool HasFrozenContent =>
                FreezeEnabled && frozen && frozenClip != null && frozenClip.IsValid;
        }

        /// <summary>The frames kept for a frozen segment (Kimodo coords, same layout as a KimodoClip).</summary>
        [Serializable]
        public class FrozenClip
        {
            public int jointCount;
            public int frameCount;
            public float[] localQuats;      // frameCount * jointCount * 4, wxyz
            public float[] rootPositions;   // frameCount * 3
            public string capturedAt = "";

            public bool IsValid =>
                frameCount > 0 && jointCount > 0 &&
                localQuats != null && localQuats.Length == frameCount * jointCount * 4 &&
                rootPositions != null && rootPositions.Length == frameCount * 3;
        }

        [Tooltip("Ordered prompt segments. Each becomes one Kimodo prompt segment with its own duration.")]
        public List<Segment> segments = new List<Segment> { new Segment() };

        // ---------------------------------------------------------------
        // Composition (asset -> what /generate wants)
        // ---------------------------------------------------------------

        public int SegmentCount => segments != null ? segments.Count : 0;

        /// <summary>True when this segment will actually be sent (blank prompts are skipped).</summary>
        public bool IsActive(int index)
        {
            if (segments == null || index < 0 || index >= segments.Count) return false;
            var s = segments[index];
            return s != null && CleanSegmentText(s.prompt).Length > 0;
        }

        /// <summary>The segments that will be sent, as (text, seconds), in order.</summary>
        public List<KeyValuePair<string, float>> Compose()
        {
            var list = new List<KeyValuePair<string, float>>();
            if (segments == null) return list;
            foreach (var s in segments)
            {
                if (s == null) continue;
                string t = CleanSegmentText(s.prompt);
                if (t.Length == 0) continue;
                list.Add(new KeyValuePair<string, float>(t, Mathf.Max(0.1f, s.seconds)));
            }
            return list;
        }

        public bool HasContent => Compose().Count > 0;

        /// <summary>The prompt string for /generate: "First segment. Second segment."</summary>
        public string BuildPrompt()
        {
            var sb = new StringBuilder();
            foreach (var e in Compose())
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(e.Key).Append('.');
            }
            return sb.ToString();
        }

        /// <summary>The duration string for /generate: one space-separated value per segment.</summary>
        public string BuildDuration()
        {
            var parts = new List<string>();
            foreach (var e in Compose())
                parts.Add(e.Value.ToString("0.###", CultureInfo.InvariantCulture));
            return string.Join(" ", parts);
        }

        /// <summary>One constraint weight per sent segment, or null when no segment overrides the global
        /// one. The server (`segment_cfg_weights`) applies these per segment — Kimodo generates a
        /// multi-prompt sequence one segment at a time, so each can have its own guidance strength.</summary>
        public float[] BuildSegmentConstraintWeights(float globalWeight)
        {
            if (segments == null) return null;
            var list = new List<float>();
            bool any = false;
            foreach (var s in segments)
            {
                if (s == null || CleanSegmentText(s.prompt).Length == 0) continue;
                if (s.overrideConstraintWeight) { any = true; list.Add(Mathf.Clamp(s.constraintWeight, 1f, 8f)); }
                else list.Add(globalWeight);
            }
            return any && list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>Periods are Kimodo's segment separator: an interior period would silently split the
        /// segment (and then the duration count no longer matches the prompt count → server error), so
        /// interior periods become commas and trailing ones are dropped.</summary>
        public static string CleanSegmentText(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            string t = s.Replace('\r', ' ').Replace('\n', ' ').Trim().TrimEnd('.', ' ');
            return t.Replace('.', ',').Trim();
        }

        /// <summary>True if the text has a period inside it (which we will rewrite as a comma).</summary>
        public static bool HasInteriorPeriod(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return s.Trim().TrimEnd('.', ' ').IndexOf('.') >= 0;
        }

        // ---------------------------------------------------------------
        // Frame math (the timeline's horizontal axis)
        // ---------------------------------------------------------------

        /// <summary>Frames a duration occupies — mirrors the server's int(seconds * fps).</summary>
        public static int FramesFor(float seconds, float fps) =>
            Mathf.Max(1, Mathf.FloorToInt(Mathf.Max(0.01f, seconds) * Mathf.Max(1f, fps)));

        /// <summary>Frames this segment occupies (0 for a skipped/blank one).</summary>
        public int FrameCountOf(int index, float fps) =>
            IsActive(index) ? FramesFor(segments[index].seconds, fps) : 0;

        /// <summary>First frame of a segment, blanks contributing nothing.</summary>
        public int StartFrame(int index, float fps)
        {
            int f = 0;
            for (int i = 0; i < SegmentCount && i < index; i++) f += FrameCountOf(i, fps);
            return f;
        }

        public int TotalFrames(float fps)
        {
            int f = 0;
            for (int i = 0; i < SegmentCount; i++) f += FrameCountOf(i, fps);
            return Mathf.Max(1, f);
        }

        public float TotalSeconds
        {
            get { float t = 0f; foreach (var e in Compose()) t += e.Value; return t; }
        }

        /// <summary>Index (into <c>segments</c>) of the segment covering a frame, or -1.</summary>
        public int SegmentAtFrame(int frame, float fps)
        {
            int f = 0;
            for (int i = 0; i < SegmentCount; i++)
            {
                int n = FrameCountOf(i, fps);
                if (n == 0) continue;
                if (frame < f + n) return i;
                f += n;
            }
            return SegmentCount > 0 ? SegmentCount - 1 : -1;
        }

        // ---------------------------------------------------------------
        // Frozen segments
        // ---------------------------------------------------------------

        /// <summary>Capture a segment's frames from a generated motion and mark it frozen. The segment's
        /// duration is snapped to exactly what was captured, so the block always matches its content.
        /// Returns false if there is nothing to capture.</summary>
        public bool FreezeSegment(int index, KimodoMotion motion, int clipIndex, float fps)
        {
            if (index < 0 || index >= SegmentCount || !IsActive(index)) return false;
            if (motion == null || motion.clips == null || motion.clips.Count == 0) return false;
            var clip = motion.clips[Mathf.Clamp(clipIndex, 0, motion.clips.Count - 1)];
            int J = motion.jointCount;
            int start = StartFrame(index, fps);
            int len = Mathf.Min(FrameCountOf(index, fps), motion.frameCount - start);
            if (start < 0 || len <= 0) return false;
            if (clip.localQuats == null || clip.localQuats.Length < (start + len) * J * 4) return false;
            if (clip.rootPositions == null || clip.rootPositions.Length < (start + len) * 3) return false;

            var fc = new FrozenClip
            {
                jointCount = J,
                frameCount = len,
                localQuats = new float[len * J * 4],
                rootPositions = new float[len * 3],
                capturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            };
            Array.Copy(clip.localQuats, start * J * 4, fc.localQuats, 0, len * J * 4);
            Array.Copy(clip.rootPositions, start * 3, fc.rootPositions, 0, len * 3);

            segments[index].frozenClip = fc;
            segments[index].frozen = true;
            // Snap the duration to exactly the frames captured. Half a frame is added because both this
            // class and the server TRUNCATE seconds × fps: len/fps can land a hair under (61/30 → 2.0333,
            // ×30 = 60.999 → 60), which would silently shorten the segment by a frame.
            segments[index].seconds = (len + 0.5f) / Mathf.Max(1f, fps);
            return true;
        }

        /// <summary>Drop a segment's frozen content (it will be generated again from now on).</summary>
        public void Unfreeze(int index)
        {
            if (index < 0 || index >= SegmentCount || segments[index] == null) return;
            segments[index].frozen = false;
            segments[index].frozenClip = null;
        }

        public bool AnyFrozenContent
        {
            get
            {
                for (int i = 0; i < SegmentCount; i++)
                    if (segments[i] != null && segments[i].HasFrozenContent) return true;
                return false;
            }
        }

        /// <summary>A stretch of the timeline that is either kept as-is or generated in one request.
        /// Frozen neighbours are what a run has to start from and arrive at.</summary>
        public struct Block
        {
            public bool frozen;
            public int firstSegment, lastSegment;   // inclusive
            public int startFrame, frameCount;
        }

        /// <summary>Split the timeline into alternating frozen / generated blocks. Consecutive frozen
        /// segments merge into one kept block, and consecutive live segments into one request — so
        /// freezing the first of two segments means exactly ONE generate call, for the second.</summary>
        public List<Block> BuildPlan(float fps)
        {
            var blocks = new List<Block>();
            int frame = 0;
            for (int i = 0; i < SegmentCount; i++)
            {
                int n = FrameCountOf(i, fps);
                if (n == 0) continue;                       // blank prompt: contributes nothing
                bool frozen = segments[i].HasFrozenContent;
                if (blocks.Count > 0)
                {
                    var last = blocks[blocks.Count - 1];
                    if (last.frozen == frozen)
                    {
                        last.lastSegment = i;
                        last.frameCount += n;
                        blocks[blocks.Count - 1] = last;
                        frame += n;
                        continue;
                    }
                }
                blocks.Add(new Block
                {
                    frozen = frozen, firstSegment = i, lastSegment = i,
                    startFrame = frame, frameCount = n,
                });
                frame += n;
            }
            return blocks;
        }

        /// <summary>The kept frames of a frozen block, concatenated (Kimodo coords).
        /// <paramref name="quats"/> is frameCount*J*4 and <paramref name="roots"/> frameCount*3.</summary>
        public bool TryGetFrozenFrames(Block block, int jointCount, out float[] quats, out float[] roots, out int frameCount)
        {
            quats = null; roots = null; frameCount = 0;
            if (!block.frozen) return false;

            int total = 0;
            for (int i = block.firstSegment; i <= block.lastSegment; i++)
            {
                var s = segments[i];
                if (s == null || !s.HasFrozenContent || s.frozenClip.jointCount != jointCount) return false;
                total += s.frozenClip.frameCount;
            }
            if (total <= 0) return false;

            quats = new float[total * jointCount * 4];
            roots = new float[total * 3];
            int at = 0;
            for (int i = block.firstSegment; i <= block.lastSegment; i++)
            {
                var fc = segments[i].frozenClip;
                Array.Copy(fc.localQuats, 0, quats, at * jointCount * 4, fc.frameCount * jointCount * 4);
                Array.Copy(fc.rootPositions, 0, roots, at * 3, fc.frameCount * 3);
                at += fc.frameCount;
            }
            frameCount = total;
            return true;
        }

        // ---------------------------------------------------------------
        // Take snapshot (save/load the character's constraint keys)
        // ---------------------------------------------------------------

        [Serializable]
        public class WaypointRec
        {
            public int frame;
            public Vector3 world;
            public bool constrainFacing;
            public float headingDeg;
        }

        [Serializable]
        public class EffectorRec
        {
            public int frame;
            public KimodoEndEffectors.Limb limbs;
            public bool hasPose;
            public bool show = true;
            public bool freeRoot;
            public Vector3 root;
            public float[] localQuats;

            // legacy (target-based keys), kept so older takes still restore and then migrate
            public KimodoEndEffectors.Kind kind;
            public Vector3 world;
            public Quaternion rot = Quaternion.identity;
            public bool constrainRotation;
        }

        [Serializable]
        public class PoseRec
        {
            public int frame;
            public bool hasPose;
            public bool show = true;
            public Vector3 root;
            public float[] localQuats;
            public bool[] jointActive;
            public string prompt;
            public bool useWaypoints;
        }

        [Serializable]
        public class Take
        {
            public bool saved;
            public string savedAt = "";
            public List<WaypointRec> waypoints = new List<WaypointRec>();
            public List<EffectorRec> effectors = new List<EffectorRec>();
            public List<PoseRec> poses = new List<PoseRec>();

            public int Count => waypoints.Count + effectors.Count + poses.Count;
        }

        [Tooltip("Saved copy of the character's constraint keys (Take ▸ Save in the Timeline window). " +
                 "The live keys stay on the scene components; this is a snapshot you can restore.")]
        public Take take = new Take();

        /// <summary>Copy the character's current constraint keys into this asset. The caller is
        /// responsible for Undo/SetDirty (the window does both).</summary>
        public void CaptureTake(KimodoGenerator g)
        {
            take = new Take { saved = true, savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
            if (g == null) return;

            var wp = g.GetComponent<KimodoWaypoints>();
            if (wp != null)
                foreach (var w in wp.waypoints)
                    take.waypoints.Add(new WaypointRec
                    {
                        frame = w.frame, world = w.world,
                        constrainFacing = w.constrainFacing, headingDeg = w.headingDeg,
                    });

            var eff = g.GetComponent<KimodoEndEffectors>();
            if (eff != null)
                foreach (var t in eff.targets)
                    take.effectors.Add(new EffectorRec
                    {
                        frame = t.frame, limbs = KimodoEndEffectors.LimbsOf(t),
                        hasPose = t.hasPose, show = t.show, freeRoot = t.freeRoot, root = t.root,
                        localQuats = t.localQuats != null ? (float[])t.localQuats.Clone() : null,
                        kind = t.kind, world = t.world, rot = t.rot, constrainRotation = t.constrainRotation,
                    });

            var pc = g.GetComponent<KimodoPoseConstraints>();
            if (pc != null)
                foreach (var k in pc.keys)
                    take.poses.Add(new PoseRec
                    {
                        frame = k.frame, hasPose = k.hasPose, show = k.show, root = k.root,
                        localQuats = k.localQuats != null ? (float[])k.localQuats.Clone() : null,
                        jointActive = k.jointActive != null ? (bool[])k.jointActive.Clone() : null,
                        prompt = k.prompt, useWaypoints = k.useWaypoints,
                    });
        }

        /// <summary>Restore the saved keys onto the character, REPLACING its current ones. Components
        /// that aren't on the character are skipped (the window adds them first, with Undo).</summary>
        public void ApplyTake(KimodoGenerator g)
        {
            if (g == null || take == null) return;

            var wp = g.GetComponent<KimodoWaypoints>();
            if (wp != null)
            {
                wp.waypoints.Clear();
                foreach (var r in take.waypoints)
                    wp.waypoints.Add(new KimodoWaypoints.Waypoint
                    {
                        frame = r.frame, world = r.world,
                        constrainFacing = r.constrainFacing, headingDeg = r.headingDeg,
                    });
            }

            var eff = g.GetComponent<KimodoEndEffectors>();
            if (eff != null)
            {
                eff.targets.Clear();
                foreach (var r in take.effectors)
                    eff.targets.Add(new KimodoEndEffectors.Target
                    {
                        frame = r.frame, limbs = r.limbs,
                        hasPose = r.hasPose, show = r.show, freeRoot = r.freeRoot, root = r.root,
                        localQuats = r.localQuats != null ? (float[])r.localQuats.Clone() : null,
                        kind = r.kind, world = r.world, rot = r.rot, constrainRotation = r.constrainRotation,
                    });
            }

            var pc = g.GetComponent<KimodoPoseConstraints>();
            if (pc != null)
            {
                pc.keys.Clear();
                foreach (var r in take.poses)
                    pc.keys.Add(new KimodoPoseConstraints.Key
                    {
                        frame = r.frame, hasPose = r.hasPose, show = r.show, root = r.root,
                        localQuats = r.localQuats != null ? (float[])r.localQuats.Clone() : null,
                        jointActive = r.jointActive != null ? (bool[])r.jointActive.Clone() : null,
                        prompt = r.prompt, useWaypoints = r.useWaypoints,
                    });
            }
        }
    }
}
