// SPDX-License-Identifier: Apache-2.0
// Editor-only "ghost mesh" preview for KimodoPoseConstraints. For each shown pose key it keeps a
// transparent clone of the target character, posed at that key's authored pose via the same
// retarget path as the live preview (KimodoPlayer). Lets you see the actual model (skin/mesh
// renderers) — not just the skeleton — while you edit a pose, without moving the real character.
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
        private class Ghost
        {
            public GameObject go;
            public KimodoPlayer player;
        }

        private readonly Dictionary<KimodoPoseConstraints.Key, Ghost> _ghosts =
            new Dictionary<KimodoPoseConstraints.Key, Ghost>();
        private Material _mat;
        private GameObject _sourceRef;   // character we cloned from
        private KimodoMotion _motionRef; // motion the ghost players are bound to
        private bool _swept;             // cleaned stray clones from a previous domain reload
        private const string GhostName = "~KimodoPoseGhost";

        // Fixed ghost look: white, ~51% base opacity (fresnel rim adds a subtle edge on top).
        private static readonly Color GhostColor = new Color(1f, 1f, 1f, 0.51f);

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
                        _mat.SetColor("_BaseColor", GhostColor);
                        _mat.SetColor("_RimColor", new Color(1f, 1f, 1f, 0.4f));
                    }
                }
                return _mat;
            }
        }

        /// <summary>Ensure a posed ghost exists for every shown key; remove the rest. Call each OnSceneGUI.</summary>
        public void Sync(KimodoPoseConstraints pc, KimodoGenerator g)
        {
            if (!_swept) { SweepOrphans(); _swept = true; }
            var tgt = g != null ? g.ResolvedTarget : null;
            if (pc == null || g == null || g.Motion == null || tgt == null || !pc.showGhostMesh || Mat == null)
            {
                DestroyAll();
                return;
            }

            var srcGo = tgt.gameObject;
            if (_sourceRef != srcGo || _motionRef != g.Motion) { DestroyAll(); _sourceRef = srcGo; _motionRef = g.Motion; }

            int need = g.Motion.jointCount * 4;

            // Drop ghosts whose key was removed or hidden.
            var stale = new List<KimodoPoseConstraints.Key>();
            foreach (var kv in _ghosts)
                if (!pc.keys.Contains(kv.Key) || !kv.Key.show)
                    stale.Add(kv.Key);
            foreach (var k in stale) { Destroy(_ghosts[k]); _ghosts.Remove(k); }

            // Ensure + pose a ghost per shown key.
            foreach (var k in pc.keys)
            {
                if (!k.show || k.localQuats == null || k.localQuats.Length != need) continue;
                if (!_ghosts.TryGetValue(k, out var ghost) || ghost.go == null)
                {
                    ghost = Create(srcGo, g.Motion);
                    if (ghost == null) continue;
                    _ghosts[k] = ghost;
                }
                // Match the live character's placement, then pose (root travel scaled like the preview).
                ghost.go.transform.SetPositionAndRotation(srcGo.transform.position, srcGo.transform.rotation);
                ghost.go.transform.localScale = srcGo.transform.lossyScale;
                ghost.player.RootMotionScale = g.rootMotionScale;
                ghost.player.PoseFromLocal(k.localQuats, k.root);
            }
        }

        private Ghost Create(GameObject srcGo, KimodoMotion motion)
        {
            var clone = UnityEngine.Object.Instantiate(srcGo);
            clone.name = GhostName;
            clone.hideFlags = HideFlags.HideAndDontSave;

            // Swap every renderer to the shared ghost material; no shadows.
            foreach (var r in clone.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
                for (int i = 0; i < mats.Length; i++) mats[i] = Mat;
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }

            var anim = clone.GetComponent<Animator>() ?? clone.GetComponentInChildren<Animator>();
            if (anim == null) { UnityEngine.Object.DestroyImmediate(clone); return null; }
            anim.runtimeAnimatorController = null; // don't let it evaluate a controller

            var player = new KimodoPlayer();
            if (!player.Bind(anim, motion)) { player.Dispose(); UnityEngine.Object.DestroyImmediate(clone); return null; }
            return new Ghost { go = clone, player = player };
        }

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
