// SPDX-License-Identifier: Apache-2.0
// Convenience menu items to set up the Kimodo component workflow in a scene:
//   * create the KimodoBridge manager
//   * add Generator + Constraints to a selected Humanoid character

using UnityEditor;
using UnityEngine;

namespace AminHP.KimodoBridge.Editor
{
    public static class KimodoMenu
    {
        [MenuItem("GameObject/Kimodo/Bridge Manager", false, 10)]
        public static KimodoBridge CreateBridge()
        {
            var existing = FindBridge();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing);
                return existing;
            }
            var go = new GameObject("Kimodo Bridge");
            var bridge = go.AddComponent<KimodoBridge>();
            Undo.RegisterCreatedObjectUndo(go, "Create Kimodo Bridge");
            Selection.activeGameObject = go;
            return bridge;
        }

        [MenuItem("GameObject/Kimodo/Set Up Selected Character", false, 11)]
        public static void SetUpCharacter()
        {
            var go = Selection.activeGameObject;
            var animator = go != null ? go.GetComponentInParent<Animator>() : null;
            if (animator == null)
            {
                EditorUtility.DisplayDialog(
                    "Kimodo",
                    "Select a character with a Humanoid Animator first.",
                    "OK");
                return;
            }

            var target = animator.gameObject;
            Undo.SetCurrentGroupName("Set up Kimodo character");
            int group = Undo.GetCurrentGroup();

            var bridge = FindBridge() ?? CreateBridge();

            var gen = target.GetComponent<KimodoGenerator>();
            if (gen == null) gen = Undo.AddComponent<KimodoGenerator>(target);
            gen.bridge = bridge;
            gen.target = animator;

            if (target.GetComponent<KimodoEndEffectors>() == null)
                Undo.AddComponent<KimodoEndEffectors>(target);

            Undo.CollapseUndoOperations(group);
            Selection.activeGameObject = target;

            if (animator.avatar == null || !animator.avatar.isHuman)
                Debug.LogWarning("[Kimodo] The selected character's avatar is not Humanoid. Set its Rig → Animation Type → Humanoid to preview.");
        }

        [MenuItem("GameObject/Kimodo/Set Up Selected Character", true)]
        private static bool SetUpCharacterValidate() => Selection.activeGameObject != null;

        private static KimodoBridge FindBridge()
        {
#if UNITY_2022_2_OR_NEWER
            return Object.FindFirstObjectByType<KimodoBridge>();
#else
            return Object.FindObjectOfType<KimodoBridge>();
#endif
        }
    }
}
