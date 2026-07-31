// SPDX-License-Identifier: Apache-2.0
// Editor-only "ghost mesh" preview for KimodoPoseConstraints. For each shown pose key it keeps a
// transparent clone of the target character, posed at that key's authored pose via the same
// retarget path as the live preview (KimodoPlayer). Lets you see the actual model (skin/mesh
// renderers) — not just the skeleton — while you edit a pose, without moving the real character.
//
// Deactivated joints (per key) fade out on the ghost: each vertex's opacity is the skin-weighted
// average of its bones' active flags, so the boundary blends smoothly (no hard cut). A per-key
// signature avoids recomputing vertex colors unless the activation actually changed.
//
// Clones are HideAndDontSave and are torn down when the editor is deselected/disposed. The
// character's own (inert) Kimodo components are cloned too but never run: they are not
// [ExecuteAlways] and have no Update, and Awake/OnEnable don't fire in edit mode.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AminHP.KimodoBridge.Editor
{
    public class KimodoPoseGhosts : IDisposable
    {
        private class SkinnedInfo
        {
            public SkinnedMeshRenderer smr;
            public Mesh mesh;              // instanced copy we own (colors carry the activation mask)
            public BoneWeight[] weights;
            public Color[] colors;         // reused buffer
        }

        private class Ghost
        {
            public GameObject go;
            public Animator anim;
            public KimodoPlayer player;
            public List<SkinnedInfo> skinned = new List<SkinnedInfo>();
            public List<Mesh> ownedMeshes = new List<Mesh>();  // to destroy (skinned + static instances)
            public int activeSig = int.MinValue;               // last applied activation signature
        }

        /// <summary>One pose to ghost. <c>owner</c> only identifies the ghost across frames (the key
        /// object it came from), so pose keys and hand/foot keys can both be ghosted.</summary>
        public struct PoseView
        {
            public object owner;
            public float[] localQuats;   // Kimodo local rotations, J*4
            public Vector3 root;         // Kimodo pelvis position
            public bool[] jointActive;   // optional per-joint fade mask (null = fully opaque)
        }

        private readonly Dictionary<object, Ghost> _ghosts = new Dictionary<object, Ghost>();
        private Material _mat;
        private GameObject _sourceRef;   // character we cloned from
        private KimodoMotion _motionRef; // motion the ghost players are bound to
        private bool _swept;             // cleaned stray clones from a previous domain reload
        private const string GhostName = "~KimodoPoseGhost";

        private Material Mat
        {
            get
            {
                if (_mat == null)
                {
                    var sh = Shader.Find("Kimodo/GhostMesh");
                    if (sh != null)
                    {
                        _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                        _mat.SetColor("_RimColor", new Color(1f, 1f, 1f, 0.4f));
                    }
                }
                return _mat;
            }
        }

        /// <summary>Ensure a posed ghost exists for every given pose; remove the rest. Call each
        /// OnSceneGUI. Any editor can drive this (pose keys, hand/foot keys, …).</summary>
        public void Sync(KimodoGenerator g, float opacity, IList<PoseView> views)
        {
            if (!_swept) { SweepOrphans(); _swept = true; }
            var tgt = g != null ? g.ResolvedTarget : null;
            var sk = g != null ? g.PoseSkeleton : null;   // real motion, or the rest skeleton pre-generation
            if (g == null || sk == null || tgt == null || views == null || Mat == null)
            {
                DestroyAll();
                return;
            }

            var srcGo = tgt.gameObject;
            if (_sourceRef != srcGo || _motionRef != sk) { DestroyAll(); _sourceRef = srcGo; _motionRef = sk; }

            // White ghost, opacity from the slider.
            Mat.SetColor("_BaseColor", new Color(1f, 1f, 1f, Mathf.Clamp01(opacity)));

            int need = sk.jointCount * 4;

            // Drop ghosts whose pose is no longer in the list.
            var live = new HashSet<object>();
            foreach (var v in views) if (v.owner != null) live.Add(v.owner);
            var stale = new List<object>();
            foreach (var kv in _ghosts) if (!live.Contains(kv.Key)) stale.Add(kv.Key);
            foreach (var k in stale) { Destroy(_ghosts[k]); _ghosts.Remove(k); }

            // Ensure + pose a ghost per view.
            foreach (var v in views)
            {
                if (v.owner == null || v.localQuats == null || v.localQuats.Length != need) continue;
                if (!_ghosts.TryGetValue(v.owner, out var ghost) || ghost.go == null)
                {
                    ghost = Create(srcGo, sk);
                    if (ghost == null) continue;
                    _ghosts[v.owner] = ghost;
                }
                // Match the live character's placement, then pose (root travel scaled like the preview).
                ghost.go.transform.SetPositionAndRotation(srcGo.transform.position, srcGo.transform.rotation);
                ghost.go.transform.localScale = srcGo.transform.lossyScale;
                ghost.player.RootMotionScale = g.rootMotionScale;
                ghost.player.PoseFromLocal(v.localQuats, v.root);

                // Refresh the fade mask only when the activation changed.
                int sig = ActiveSig(v.jointActive);
                if (ghost.activeSig != sig) { ApplyActivation(ghost, v.jointActive, sk); ghost.activeSig = sig; }
            }
        }

        private Ghost Create(GameObject srcGo, KimodoMotion motion)
        {
            var clone = UnityEngine.Object.Instantiate(srcGo);
            clone.name = GhostName;
            clone.hideFlags = HideFlags.HideAndDontSave;
            var gh = new Ghost { go = clone };

            // Swap every renderer to the shared ghost material; no shadows.
            foreach (var r in clone.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
                for (int i = 0; i < mats.Length; i++) mats[i] = Mat;
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }

            // Instance skinned meshes so we can write per-vertex activation into their colors.
            foreach (var smr in clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                var inst = UnityEngine.Object.Instantiate(smr.sharedMesh);
                inst.hideFlags = HideFlags.HideAndDontSave;
                smr.sharedMesh = inst;
                var colors = SolidWhite(inst.vertexCount);
                inst.colors = colors;
                gh.skinned.Add(new SkinnedInfo { smr = smr, mesh = inst, weights = inst.boneWeights, colors = colors });
                gh.ownedMeshes.Add(inst);
            }
            // Static meshes: give them a defined (fully-visible) vertex-colour channel too.
            foreach (var mf in clone.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var inst = UnityEngine.Object.Instantiate(mf.sharedMesh);
                inst.hideFlags = HideFlags.HideAndDontSave;
                inst.colors = SolidWhite(inst.vertexCount);
                mf.sharedMesh = inst;
                gh.ownedMeshes.Add(inst);
            }

            var anim = clone.GetComponent<Animator>() ?? clone.GetComponentInChildren<Animator>();
            if (anim == null) { Destroy(gh); return null; }
            anim.runtimeAnimatorController = null; // don't let it evaluate a controller
            gh.anim = anim;

            gh.player = new KimodoPlayer();
            if (!gh.player.Bind(anim, motion)) { Destroy(gh); return null; }
            return gh;
        }

        private static Color[] SolidWhite(int n)
        {
            var c = new Color[n];
            for (int i = 0; i < n; i++) c[i] = Color.white;
            return c;
        }

        // ---- activation / fade -------------------------------------------------------------

        private static int ActiveSig(bool[] jointActive)
        {
            if (jointActive == null) return 0;
            int h = 17;
            for (int i = 0; i < jointActive.Length; i++) h = h * 31 + (jointActive[i] ? 1 : 0);
            return h;
        }

        private static bool AllActive(bool[] jointActive)
        {
            if (jointActive == null) return true;
            for (int i = 0; i < jointActive.Length; i++) if (!jointActive[i]) return false;
            return true;
        }

        // Write each vertex's opacity = skin-weighted average of its bones' active flags.
        private static void ApplyActivation(Ghost gh, bool[] jointActive, KimodoMotion motion)
        {
            bool allActive = AllActive(jointActive);

            // SOMA joint active flags -> per-HumanBodyBones active (via the SOMA->Human map).
            Dictionary<HumanBodyBones, float> activeHbb = null;
            if (!allActive)
            {
                activeHbb = new Dictionary<HumanBodyBones, float>();
                for (int j = 0; j < motion.bones.Count && j < jointActive.Length; j++)
                    if (KimodoSomaHumanoid.SomaToHuman.TryGetValue(motion.bones[j].name, out var hbb))
                        activeHbb[hbb] = jointActive[j] ? 1f : 0f;
            }

            // Clone bone transform -> HumanBodyBones (to resolve each skin bone's active flag).
            Dictionary<Transform, HumanBodyBones> t2hbb = null;
            if (!allActive && gh.anim != null)
            {
                t2hbb = new Dictionary<Transform, HumanBodyBones>();
                for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
                {
                    var t = gh.anim.GetBoneTransform((HumanBodyBones)i);
                    if (t != null) t2hbb[t] = (HumanBodyBones)i;
                }
            }

            foreach (var sm in gh.skinned)
            {
                var colors = sm.colors;
                if (allActive)
                {
                    for (int v = 0; v < colors.Length; v++) colors[v] = Color.white;
                    sm.mesh.colors = colors;
                    continue;
                }

                var bones = sm.smr.bones;
                var boneAct = new float[bones.Length];
                for (int b = 0; b < bones.Length; b++)
                    boneAct[b] = ResolveBoneActive(bones[b], t2hbb, activeHbb);

                var bw = sm.weights;
                int n = Mathf.Min(colors.Length, bw.Length);
                for (int v = 0; v < n; v++)
                {
                    var w = bw[v];
                    float a = w.weight0 * BoneAct(boneAct, w.boneIndex0)
                            + w.weight1 * BoneAct(boneAct, w.boneIndex1)
                            + w.weight2 * BoneAct(boneAct, w.boneIndex2)
                            + w.weight3 * BoneAct(boneAct, w.boneIndex3);
                    colors[v] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
                }
                sm.mesh.colors = colors;
            }
        }

        private static float BoneAct(float[] act, int idx) => (idx >= 0 && idx < act.Length) ? act[idx] : 1f;

        // Walk up the bone's parents until a humanoid bone with a known active flag is found.
        private static float ResolveBoneActive(Transform bone, Dictionary<Transform, HumanBodyBones> t2hbb,
                                               Dictionary<HumanBodyBones, float> activeHbb)
        {
            var t = bone; int guard = 0;
            while (t != null && guard++ < 64)
            {
                if (t2hbb.TryGetValue(t, out var hbb) && activeHbb.TryGetValue(hbb, out var a)) return a;
                t = t.parent;
            }
            return 1f; // no mapped ancestor -> keep visible
        }

        // ---- lifecycle ---------------------------------------------------------------------

        // Destroy any ghost clones left over by a previous editor instance (e.g. after a domain
        // reload, where our tracking dictionary was reset but the HideAndDontSave objects survived).
        private static void SweepOrphans()
        {
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || t.parent != null || t.name != GhostName) continue;
                if (t.gameObject.scene.IsValid()) UnityEngine.Object.DestroyImmediate(t.gameObject);
            }
        }

        private static void Destroy(Ghost gh)
        {
            gh.player?.Dispose();
            foreach (var m in gh.ownedMeshes) if (m != null) UnityEngine.Object.DestroyImmediate(m);
            gh.ownedMeshes.Clear();
            if (gh.go != null) UnityEngine.Object.DestroyImmediate(gh.go);
        }

        private void DestroyAll()
        {
            foreach (var gh in _ghosts.Values) Destroy(gh);
            _ghosts.Clear();
            _sourceRef = null;
            _motionRef = null;
        }

        public void Dispose()
        {
            DestroyAll();
            if (_mat != null) UnityEngine.Object.DestroyImmediate(_mat);
            _mat = null;
        }
    }
}
