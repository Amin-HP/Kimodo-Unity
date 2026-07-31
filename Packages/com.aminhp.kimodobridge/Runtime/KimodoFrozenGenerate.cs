// SPDX-License-Identifier: Apache-2.0
// Generating a timeline that has FROZEN segments.
//
// A frozen segment is not generated at all: its captured frames are kept verbatim and only the runs
// of live segments between them are requested. Freeze the first of two segments and Generate sends
// exactly ONE request — for the second.
//
// The problem that creates is continuity: a request is generated in Kimodo's canonical frame (the
// root starts at XZ 0,0 and faces `first_heading_angle`), so a run that continues a frozen segment
// has to be told where and how that segment ended. Kimodo solves this for its own multi-prompt
// sequences (kimodo_model.py::_multiprompt, the `not is_first_motion` branch) and we do the same
// thing here, through the public constraint API:
//
//   * the frozen block's last K frames are sent as a FullBody constraint plus an end-effector one
//     (Kimodo pairs those two as well — the second is what captures hand/feet rotations) over the
//     first K frames of the request,
//   * everything is translated so that the first of those K frames sits at XZ 0,0, and the output is
//     translated back afterwards,
//   * `first_heading_angle` is the heading of that frame, so the model does not start out facing +Z
//     and then have to turn to satisfy the constraint,
//   * those K lead-in frames are dropped from the result — the frozen segment already owns them.
//
// The same trick runs the other way when a frozen block FOLLOWS a run: K extra frames are appended
// and constrained to the frozen block's first K frames, so the run arrives where the frozen content
// starts, and they are dropped too.
//
// The "hidden keyframes" a freeze creates are exactly these boundary constraints. They are derived at
// request time from the frozen clip, so unfreezing removes them with nothing left to clean up.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace AminHP.KimodoBridge
{
    public static class KimodoFrozenGenerate
    {
        /// <summary>Frames of overlap used to hand over between a frozen block and a generated run.
        /// Matches Kimodo's own default `num_transition_frames`.</summary>
        public const int LeadFrames = 5;

        private static readonly string[] AllLimbNames = { "LeftHand", "RightHand", "LeftFoot", "RightFoot" };

        /// <summary>Generate every live run of the timeline and splice the frozen blocks back in.
        /// Requests run one after another; <paramref name="onProgress"/> reports "run i of n".</summary>
        public static void Run(KimodoGenerator g, List<KimodoConstraint> userConstraints,
                               Action<string> onProgress, Action<bool, KimodoMotion, string> onDone)
        {
            var tl = g.timeline;
            var sk = g.PoseSkeleton;
            if (tl == null || sk == null) { onDone(false, null, "No timeline or skeleton."); return; }

            float fps = g.Fps;
            int J = sk.jointCount;
            var plan = tl.BuildPlan(fps);
            if (plan.Count == 0) { onDone(false, null, "The timeline has no segments to generate."); return; }

            var acc = new Accumulator(J);
            int runTotal = 0;
            foreach (var b in plan) if (!b.frozen) runTotal++;

            // Walk the blocks in order, chaining one request per live run.
            void Step(int blockIndex, int runIndex)
            {
                if (blockIndex >= plan.Count)
                {
                    onDone(true, acc.Build(sk, fps), null);
                    return;
                }

                var block = plan[blockIndex];
                if (block.frozen)
                {
                    if (!tl.TryGetFrozenFrames(block, J, out var q, out var r, out int n))
                    {
                        onDone(false, null,
                            $"Segment {block.firstSegment + 1} is frozen but its kept frames do not match the " +
                            "current skeleton. Unfreeze it, Generate, and freeze it again.");
                        return;
                    }
                    acc.Append(q, r, 0, n, Vector2.zero, Vector2.zero);
                    Step(blockIndex + 1, runIndex);
                    return;
                }

                onProgress?.Invoke($"Generating run {runIndex + 1} of {runTotal}…");
                RequestRun(g, tl, sk, fps, plan, blockIndex, userConstraints, acc, (ok, err) =>
                {
                    if (!ok) { onDone(false, null, err); return; }
                    Step(blockIndex + 1, runIndex + 1);
                });
            }

            Step(0, 0);
        }

        // -----------------------------------------------------------------------------------------
        private static void RequestRun(KimodoGenerator g, KimodoTimeline tl, KimodoMotion sk, float fps,
                                       List<KimodoTimeline.Block> plan, int blockIndex,
                                       List<KimodoConstraint> userConstraints, Accumulator acc,
                                       Action<bool, string> onDone)
        {
            var block = plan[blockIndex];
            int J = sk.jointCount;

            // --- what we continue from, and what we have to arrive at -----------------------------
            bool hasLead = blockIndex > 0 && plan[blockIndex - 1].frozen;
            bool hasTail = blockIndex + 1 < plan.Count && plan[blockIndex + 1].frozen;

            float[] leadQ = null, leadR = null, tailQ = null, tailR = null;
            int lead = 0, tail = 0;
            Vector2 origin = Vector2.zero;

            // Where the run's first and last kept frames OUGHT to sit: one step outside each frozen
            // block, extrapolated with that block's own velocity there. The generated frames that carry
            // the boundary constraints are dropped (the frozen blocks own those frames), so the frames
            // we keep are themselves unconstrained -- these are what the warp below pins them to.
            bool haveWantStart = false, haveWantEnd = false;
            Vector2 wantStart = Vector2.zero, wantEnd = Vector2.zero;

            if (hasLead && tl.TryGetFrozenFrames(plan[blockIndex - 1], J, out var pq, out var pr, out int pn))
            {
                lead = Mathf.Min(LeadFrames, pn);
                leadQ = Slice(pq, (pn - lead) * J * 4, lead * J * 4);
                leadR = Slice(pr, (pn - lead) * 3, lead * 3);
                origin = new Vector2(leadR[0], leadR[2]);   // the handover frame goes to the origin

                var lastP = new Vector2(pr[(pn - 1) * 3], pr[(pn - 1) * 3 + 2]);
                var prevP = pn >= 2 ? new Vector2(pr[(pn - 2) * 3], pr[(pn - 2) * 3 + 2]) : lastP;
                wantStart = lastP + (lastP - prevP);        // one step past where the frozen block ended
                haveWantStart = true;
            }
            if (hasTail && tl.TryGetFrozenFrames(plan[blockIndex + 1], J, out var nq, out var nr, out int nn))
            {
                tail = Mathf.Min(LeadFrames, nn);
                tailQ = Slice(nq, 0, tail * J * 4);
                tailR = Slice(nr, 0, tail * 3);

                var firstN = new Vector2(nr[0], nr[2]);
                var secondN = nn >= 2 ? new Vector2(nr[3], nr[5]) : firstN;
                wantEnd = firstN - (secondN - firstN);       // one step before the frozen block starts
                haveWantEnd = true;
            }

            // --- prompts + durations, in FRAMES so the server's int(seconds × fps) lands exactly ----
            var texts = new List<string>();
            var frameCounts = new List<int>();
            var weights = new List<float>();
            for (int i = block.firstSegment; i <= block.lastSegment; i++)
            {
                if (!tl.IsActive(i)) continue;
                texts.Add(KimodoTimeline.CleanSegmentText(tl.segments[i].prompt));
                frameCounts.Add(tl.FrameCountOf(i, fps));
                weights.Add(tl.segments[i].overrideConstraintWeight
                    ? Mathf.Clamp(tl.segments[i].constraintWeight, 1f, 8f) : g.constraintWeight);
            }
            if (texts.Count == 0) { onDone(false, "A live block had no usable prompt."); return; }

            int runFrames = 0;
            foreach (int n in frameCounts) runFrames += n;
            frameCounts[0] += lead;                          // the handover is generated, then dropped
            frameCounts[frameCounts.Count - 1] += tail;

            var prompt = new System.Text.StringBuilder();
            var duration = new System.Text.StringBuilder();
            for (int i = 0; i < texts.Count; i++)
            {
                if (i > 0) { prompt.Append(' '); duration.Append(' '); }
                prompt.Append(texts[i]).Append('.');
                // +0.5 frame: both sides TRUNCATE seconds × fps, so n/fps can land a hair under n.
                duration.Append(((frameCounts[i] + 0.5f) / fps).ToString("0.####",
                    System.Globalization.CultureInfo.InvariantCulture));
            }

            int reqFrames = 0;
            foreach (int n in frameCounts) reqFrames += n;

            // --- constraints: the boundaries, plus the user's own keys inside this run --------------
            var cons = new List<KimodoConstraint>();
            if (lead > 0) AddBoundary(cons, sk, leadQ, leadR, lead, 0, origin);
            if (tail > 0)
            {
                // The two junctions are NOT symmetric. At the start, the constrained frames come BEFORE
                // the ones we keep, so the run enters the kept range already snapped onto the frozen
                // pose. At the end they come AFTER, so without this the last kept frame is the only
                // unconstrained frame at a junction -- which is exactly what read as "it does not fit
                // the third segment". So the tail boundary is extended one frame backwards, onto the
                // last frame we actually keep: the frozen block's opening pose, with its root stepped
                // back by one frame of the block's own entry velocity.
                var q = new float[(tail + 1) * J * 4];
                var r = new float[(tail + 1) * 3];
                Array.Copy(tailQ, 0, q, 0, J * 4);                       // 33 ms earlier: same pose
                Array.Copy(tailQ, 0, q, J * 4, tail * J * 4);
                r[0] = wantEnd.x; r[1] = tailR[1]; r[2] = wantEnd.y;
                Array.Copy(tailR, 0, r, 3, tail * 3);
                AddBoundary(cons, sk, q, r, tail + 1, lead + runFrames - 1, origin);
            }

            if (userConstraints != null)
                foreach (var c in userConstraints)
                {
                    var moved = Remap(c, -block.startFrame + lead, origin, 0, reqFrames);
                    if (moved != null) cons.Add(moved);
                }

            var b = g.ResolvedBridge;
            if (b == null || !b.IsOnline) { onDone(false, "The bridge went offline."); return; }

            var req = new KimodoGenerateRequest
            {
                prompt = prompt.ToString(),
                model = b.model,
                duration = duration.ToString(),
                num_samples = 1,                 // frozen content is shared, so samples would diverge
                diffusion_steps = g.diffusionSteps,
                num_transition_frames = LeadFrames,
                seed = g.useSeed ? g.seed : -1,
                postprocess = g.postprocess,
                include_positions = false,
                constraints = cons.Count > 0 ? cons : null,
                cfg_weight = cons.Count > 0 ? new[] { 2f, g.constraintWeight } : null,
                segment_cfg_weights = cons.Count > 0 ? weights.ToArray() : null,
                has_first_heading = lead > 0,
                first_heading_angle = lead > 0 ? HeadingAt(sk, leadQ, leadR, 0) : 0f,
            };

            b.GetClient().Generate(req, (ok, motion, err) =>
            {
                if (!ok || motion == null || motion.clips == null || motion.clips.Count == 0)
                { onDone(false, "Generation failed: " + err); return; }
                if (motion.jointCount != J)
                { onDone(false, "The model returned a different skeleton than the frozen content was captured with."); return; }

                var clip = motion.clips[0];
                int have = motion.frameCount;
                int take = Mathf.Min(runFrames, Mathf.Max(0, have - lead));
                if (take <= 0) { onDone(false, "The generated run came back too short to use."); return; }

                // The boundary constraints steer the approach, but only the frames carrying them get the
                // post-process snap -- and those are exactly the ones we drop. Whatever the model has
                // left over at each junction is taken out here, spread across the whole run so no single
                // frame gains a visible step (a two-point motion warp). Ground position only: the pose
                // at the junction is the constraint's job.
                Vector2 offStart = origin, offEnd = origin;
                if (haveWantStart)
                {
                    var haveS = new Vector2(clip.rootPositions[lead * 3], clip.rootPositions[lead * 3 + 2]) + origin;
                    offStart = origin + (wantStart - haveS);
                }
                if (haveWantEnd)
                {
                    int lastKept = lead + take - 1;
                    var haveE = new Vector2(clip.rootPositions[lastKept * 3], clip.rootPositions[lastKept * 3 + 2]) + origin;
                    offEnd = origin + (wantEnd - haveE);
                }
                if (!haveWantStart) offStart = offEnd;      // a run that opens the shot keeps its own start
                if (!haveWantEnd) offEnd = offStart;        // a run that closes it keeps its own end

                // A big correction means the model did not really get near the frozen block: the
                // junction will line up (we move it there) but the run has to drift to do it, which can
                // show as sliding. Worth saying out loud rather than hiding.
                float slide = Mathf.Max((offStart - origin).magnitude, (offEnd - origin).magnitude);
                if (slide > 0.25f)
                    Debug.LogWarning($"[Kimodo] The generated run had to be moved {slide:0.00} m to meet a " +
                        "frozen segment, so it may slide. Try a prompt that gets the character closer, a " +
                        "waypoint near the junction, or a higher Constraint weight.");

                acc.AdoptTemplate(motion);
                acc.Append(clip.localQuats, clip.rootPositions, lead, take, offStart, offEnd);
                onDone(true, null);
            });
        }

        // The boundary "hidden keys": the frozen frames as a full-body pose plus the end-effector
        // constraint Kimodo pairs with it, translated so the handover frame sits at the origin.
        private static void AddBoundary(List<KimodoConstraint> into, KimodoMotion sk,
                                        float[] quats, float[] roots, int count, int atFrame, Vector2 origin)
        {
            var frames = new int[count];
            for (int i = 0; i < count; i++) frames[i] = atFrame + i;

            var r = (float[])roots.Clone();
            for (int i = 0; i < count; i++) { r[i * 3] -= origin.x; r[i * 3 + 2] -= origin.y; }

            into.Add(new KimodoConstraint
            {
                type = "fullbody",
                frameIndices = frames,
                localQuats = quats,
                rootPositions = r,
                jointNames = Array.Empty<string>(),
            });
            into.Add(new KimodoConstraint
            {
                type = "end-effector",
                frameIndices = (int[])frames.Clone(),
                localQuats = quats,
                rootPositions = (float[])r.Clone(),
                jointNames = AllLimbNames,
            });
        }

        /// <summary>Move a constraint into a run's own frame numbering and canonical origin, dropping
        /// the keyframes that fall outside it. Returns null when nothing is left.</summary>
        public static KimodoConstraint Remap(KimodoConstraint c, int frameOffset, Vector2 origin,
                                             int minFrame, int maxFrameExclusive)
        {
            if (c?.frameIndices == null || c.frameIndices.Length == 0) return null;

            var keep = new List<int>();
            for (int i = 0; i < c.frameIndices.Length; i++)
            {
                int f = c.frameIndices[i] + frameOffset;
                if (f >= minFrame && f < maxFrameExclusive) keep.Add(i);
            }
            if (keep.Count == 0) return null;

            var outC = new KimodoConstraint
            {
                type = c.type,
                jointNames = c.jointNames,
                activeJoints = c.activeJoints,
                freeRoot = c.freeRoot,
                frameIndices = new int[keep.Count],
            };
            for (int n = 0; n < keep.Count; n++) outC.frameIndices[n] = c.frameIndices[keep[n]] + frameOffset;

            if (c.localQuats != null && c.frameIndices.Length > 0 && c.localQuats.Length % c.frameIndices.Length == 0)
            {
                int stride = c.localQuats.Length / c.frameIndices.Length;
                outC.localQuats = new float[keep.Count * stride];
                for (int n = 0; n < keep.Count; n++)
                    Array.Copy(c.localQuats, keep[n] * stride, outC.localQuats, n * stride, stride);
            }
            if (c.rootPositions != null && c.rootPositions.Length >= c.frameIndices.Length * 3)
            {
                outC.rootPositions = new float[keep.Count * 3];
                for (int n = 0; n < keep.Count; n++)
                {
                    outC.rootPositions[n * 3 + 0] = c.rootPositions[keep[n] * 3 + 0] - origin.x;
                    outC.rootPositions[n * 3 + 1] = c.rootPositions[keep[n] * 3 + 1];
                    outC.rootPositions[n * 3 + 2] = c.rootPositions[keep[n] * 3 + 2] - origin.y;
                }
            }
            if (c.rootPath2d != null && c.rootPath2d.Length >= c.frameIndices.Length * 2)
            {
                outC.rootPath2d = new float[keep.Count * 2];
                for (int n = 0; n < keep.Count; n++)
                {
                    outC.rootPath2d[n * 2 + 0] = c.rootPath2d[keep[n] * 2 + 0] - origin.x;
                    outC.rootPath2d[n * 2 + 1] = c.rootPath2d[keep[n] * 2 + 1] - origin.y;
                }
            }
            if (c.rootHeading2d != null && c.rootHeading2d.Length >= c.frameIndices.Length * 2)
            {
                outC.rootHeading2d = new float[keep.Count * 2];
                for (int n = 0; n < keep.Count; n++)
                {
                    outC.rootHeading2d[n * 2 + 0] = c.rootHeading2d[keep[n] * 2 + 0];
                    outC.rootHeading2d[n * 2 + 1] = c.rootHeading2d[keep[n] * 2 + 1];
                }
            }
            return outC;
        }

        /// <summary>Kimodo's heading convention, from the hips: atan2(dz, -dx) over
        /// (right hip − left hip) — see motion_rep/feature_utils.py::compute_heading_angle.</summary>
        private static float HeadingAt(KimodoMotion sk, float[] quats, float[] roots, int frame)
        {
            int rHip = KimodoFK.BoneIndex(sk, "RightLeg"), lHip = KimodoFK.BoneIndex(sk, "LeftLeg");
            if (rHip < 0 || lHip < 0) return 0f;
            var pos = KimodoFK.GlobalPositions(sk, new KimodoClip { localQuats = quats, rootPositions = roots }, frame);
            Vector3 d = pos[rHip] - pos[lHip];
            return Mathf.Atan2(d.z, -d.x);
        }

        private static float[] Slice(float[] src, int offset, int length)
        {
            var dst = new float[length];
            Array.Copy(src, offset, dst, 0, length);
            return dst;
        }

        // -----------------------------------------------------------------------------------------
        /// <summary>Collects the final clip as the blocks are produced.</summary>
        private class Accumulator
        {
            private readonly int _j;
            private readonly List<float> _quats = new List<float>();
            private readonly List<float> _roots = new List<float>();
            private int _frames;
            private KimodoMotion _template;

            public Accumulator(int jointCount) { _j = jointCount; }

            public void AdoptTemplate(KimodoMotion m) { if (_template == null) _template = m; }

            /// <summary>Append <paramref name="count"/> frames starting at <paramref name="from"/>,
            /// sliding their ground position from <paramref name="offsetStart"/> to
            /// <paramref name="offsetEnd"/> across the range. The offset puts a run generated at the
            /// canonical origin back on the timeline, and its drift absorbs whatever the model left over
            /// at the junctions. Equal offsets = a plain translation.</summary>
            public void Append(float[] quats, float[] roots, int from, int count,
                               Vector2 offsetStart, Vector2 offsetEnd)
            {
                for (int f = 0; f < count; f++)
                {
                    int src = (from + f) * _j * 4;
                    for (int k = 0; k < _j * 4; k++) _quats.Add(quats[src + k]);

                    var off = count > 1
                        ? Vector2.Lerp(offsetStart, offsetEnd, f / (float)(count - 1))
                        : offsetStart;
                    int rs = (from + f) * 3;
                    _roots.Add(roots[rs] + off.x);
                    _roots.Add(roots[rs + 1]);
                    _roots.Add(roots[rs + 2] + off.y);
                }
                _frames += count;
            }

            public KimodoMotion Build(KimodoMotion skeleton, float fps)
            {
                var src = _template ?? skeleton;
                var m = new KimodoMotion
                {
                    model = src.model,
                    displayName = src.displayName,
                    skeletonName = src.skeletonName,
                    coordSystem = src.coordSystem,
                    quatOrder = src.quatOrder,
                    fps = fps,
                    frameCount = _frames,
                    jointCount = _j,
                    rootIndex = src.rootIndex,
                    // Frozen blocks carry no foot contacts, so an accumulated array would not line up
                    // with the frames. Nothing downstream needs them (the server does its own
                    // post-processing), so this path reports none rather than a misaligned array.
                    footContactChannels = 0,
                    bones = src.bones,
                    prompts = src.prompts,
                    generationSeconds = src.generationSeconds,
                };
                m.clips.Add(new KimodoClip
                {
                    localQuats = _quats.ToArray(),
                    rootPositions = _roots.ToArray(),
                    footContacts = Array.Empty<float>(),
                });
                return m;
            }
        }
    }
}
