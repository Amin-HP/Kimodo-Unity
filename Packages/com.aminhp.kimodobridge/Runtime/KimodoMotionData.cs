// SPDX-License-Identifier: Apache-2.0
// Serializable data-transfer objects mirroring the Kimodo bridge server JSON.
// Field names MUST match the server keys exactly (JsonUtility is case-sensitive).

using System;
using System.Collections.Generic;

namespace AminHP.KimodoBridge
{
    // ----- /health -----
    [Serializable]
    public class KimodoHealth
    {
        public string status;
        public string device;
        public bool cudaAvailable;
        public string[] loadedModels;
        public string defaultModel;
    }

    // ----- /progress -----
    // How far the generation in flight has got. `fraction` is the whole request, 0..1; `phase` is
    // "encoding" (the text encoder, which on CPU is a real slice of the wall clock and has no step
    // count), "denoising", or "postprocess".
    [Serializable]
    public class KimodoProgress
    {
        public bool running;
        public string phase;
        public int segment, segments;   // multi-prompt runs one denoising loop per segment
        public int step, steps;
        public float fraction;
        public float elapsed;           // seconds since the request started
    }

    // ----- /models -----
    [Serializable]
    public class KimodoModelInfo
    {
        public string shortKey;
        public string displayName;
        public string family;
        public string skeleton;
        public string dataset;
        public string version;
        public bool loaded;
    }

    [Serializable]
    public class KimodoModelList
    {
        public List<KimodoModelInfo> models = new List<KimodoModelInfo>();
    }

    // ----- /load_model -----
    [Serializable]
    public class KimodoLoadResult
    {
        public string loaded;
        public string displayName;
        public string detail; // set on error
    }

    // ----- /generate request -----
    // A single keyframe constraint. Poses are captured straight from a KimodoClip, so they are
    // already in Kimodo coords (wxyz quats + metres) — no conversion needed on either side.
    // type: "left-hand" | "right-hand" | "left-foot" | "right-foot" | "fullbody" | "end-effector".
    [Serializable]
    public class KimodoConstraint
    {
        public string type;
        public int[] frameIndices;      // N frame indices (into the source clip)
        public float[] localQuats;      // N*J*4, wxyz, Kimodo coords (full-body pose per keyframe)
        public float[] rootPositions;   // N*3, metres, Kimodo coords (pelvis position per keyframe)
        public string[] jointNames;     // only for generic "end-effector"; empty otherwise
        public string[] activeJoints;   // "fullbody" only: if set, constrain ONLY these joints'
                                        // positions (a partial pose, e.g. upper body); the rest of
                                        // the body is left free. Omit/empty => full-body pose.
        public float[] targetOffsets;   // N*3, metres, Kimodo coords: shift of the pinned joint from
                                        // its captured position (0 = pin where it is). Lets the user
                                        // drag the hand/foot to a new target.
        public float[] rootPath2d;      // only for type "root2d": N*2, Kimodo (x,z) ground positions
                                        // the pelvis must pass through at the given frameIndices.
        public float[] rootHeading2d;   // optional, root2d only: N*2 [cos,sin] of the Kimodo heading
                                        // angle to face at each frame (null = don't constrain facing).

        // Hand/foot keyframes only (KimodoEndEffectors). false (the demo's behaviour) pins the pose's
        // root ground position, height and facing along with the limb, so the result matches the
        // ghost you authored. true drops height + facing, letting the body rise/turn to reach.
        public bool freeRoot;

        // Direct effector target (legacy KimodoEndEffectors path, no longer sent): explicit world->Kimodo
        // target for a hand/foot instead of a captured pose. If present the server builds a
        // single-joint target constraint, which gets no post-processing snap — hence the pose path.
        public float[] effectorPos;     // N*3, Kimodo global position of the effector
        public float[] effectorRot;     // N*4 wxyz, Kimodo global rotation (only if constrainRot)
        public float[] effectorRootXZ;  // N*2, Kimodo smooth-root (x,z) at each frame (required)
        public bool constrainRot;       // also constrain the effector's orientation
    }

    [Serializable]
    public class KimodoGenerateRequest
    {
        public string prompt;
        public string model = "soma";
        public string duration = "5.0";
        public int num_samples = 1;
        public int diffusion_steps = 100;
        public int num_transition_frames = 5;
        public float[] cfg_weight;         // [text_weight, constraint_weight]; higher constraint = stronger pins
        public float[] segment_cfg_weights;// optional: one constraint weight per prompt segment, overriding
                                           // cfg_weight[1] for that segment (null/empty = same weight throughout)
        public int seed = -1;              // -1 => omit (see KimodoClient serialization)
        public bool postprocess = true;
        // Initial body heading (radians, Kimodo convention: 0 = facing +Z). Set both when a request
        // continues motion the client already has — the frames after a frozen timeline segment — so the
        // model starts facing the way that motion ended. The bool is needed because JsonUtility cannot
        // send a null float.
        public bool has_first_heading;
        public float first_heading_angle;
        public bool include_positions = false;
        public List<KimodoConstraint> constraints;   // null/empty => unconstrained
    }

    // ----- /generate response -----
    [Serializable]
    public class KimodoBone
    {
        public string name;
        public int parent;   // index into bones[], -1 for root
        public float ox, oy, oz;   // local rest offset from parent, metres, Kimodo coords
    }

    [Serializable]
    public class KimodoClip
    {
        public float[] rootPositions;  // T*3, metres, Kimodo coords
        public float[] localQuats;     // T*J*4, wxyz, Kimodo coords
        public float[] footContacts;   // T*C (may be empty)
        public float[] posedJoints;    // T*J*3 (only if include_positions)
    }

    [Serializable]
    public class KimodoMotion
    {
        public string model;
        public string displayName;
        public string skeletonName;
        public string coordSystem;
        public string quatOrder;
        public float fps;
        public int frameCount;
        public int jointCount;
        public int rootIndex;
        public int footContactChannels;
        public List<KimodoBone> bones = new List<KimodoBone>();
        public List<KimodoClip> clips = new List<KimodoClip>();
        public string[] prompts;
        public int[] numFramesPerSegment;
        public float generationSeconds;

        // Server may return {"detail": "..."} on error; captured for diagnostics.
        public string detail;
    }
}
