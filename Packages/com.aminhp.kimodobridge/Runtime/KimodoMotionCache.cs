// SPDX-License-Identifier: Apache-2.0
// Keeps a generated motion on disk so it survives a script compile, entering play mode, or closing
// Unity — without having to bake it.
//
// A KimodoMotion is transient by nature (it comes off the wire and is rebuilt on Generate), so it
// lives in a [NonSerialized] field: every domain reload threw it away and left the character with
// nothing to preview or bake, after a generation that takes a minute. Serialising it into the scene
// instead would put hundreds of KB of float arrays into the scene file and mark it dirty on every
// Generate, which is not what a throwaway preview should cost.
//
// So it is written next to the project in `Library/`, the folder Unity already uses for derived data:
// it survives reloads and restarts, is not part of the project's source, and is safe to delete (the
// only cost is regenerating). One file per generator, keyed by a hidden id on the component.

using System;
using System.IO;
using UnityEngine;

namespace AminHP.KimodoBridge
{
    public static class KimodoMotionCache
    {
        private const string FolderName = "KimodoMotionCache";

        /// <summary>Where the cache lives: <c>&lt;project&gt;/Library/KimodoMotionCache</c>.</summary>
        public static string Folder =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", FolderName);

        public static string PathFor(string id) => Path.Combine(Folder, id + ".json");

        public static bool Exists(string id) =>
            !string.IsNullOrEmpty(id) && File.Exists(PathFor(id));

        /// <summary>Write a motion to the cache. Failures are logged, never thrown: a cache miss only
        /// costs a regenerate, so it must not take the generate down with it.</summary>
        public static bool Save(string id, KimodoMotion motion)
        {
            if (string.IsNullOrEmpty(id) || motion == null) return false;
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(PathFor(id), JsonUtility.ToJson(motion));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Kimodo] Could not cache the generated motion: {e.Message}");
                return false;
            }
        }

        public static KimodoMotion Load(string id)
        {
            if (!Exists(id)) return null;
            try
            {
                var m = JsonUtility.FromJson<KimodoMotion>(File.ReadAllText(PathFor(id)));
                // A half-written or stale file must not come back as a motion with no frames.
                if (m == null || m.frameCount <= 0 || m.jointCount <= 0 || m.clips == null || m.clips.Count == 0)
                    return null;
                var clip = m.clips[0];
                if (clip.localQuats == null || clip.localQuats.Length < m.frameCount * m.jointCount * 4) return null;
                if (clip.rootPositions == null || clip.rootPositions.Length < m.frameCount * 3) return null;
                return m;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Kimodo] Could not read the cached motion: {e.Message}");
                return null;
            }
        }

        public static void Delete(string id)
        {
            try { if (Exists(id)) File.Delete(PathFor(id)); }
            catch (Exception e) { Debug.LogWarning($"[Kimodo] Could not clear the cached motion: {e.Message}"); }
        }

        /// <summary>Size on disk of one cached motion, in bytes (0 if there is none).</summary>
        public static long SizeOf(string id)
        {
            try { return Exists(id) ? new FileInfo(PathFor(id)).Length : 0L; }
            catch { return 0L; }
        }
    }
}
