using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.DataAssets;
using YLFramework.Editor.DataAssets;

namespace YLFramework.Editor.Localization.DataAssets
{
    /// <summary>只检查所选业务根的官方引用，不改变 DataAssets 的运行时依赖。</summary>
    public static class LocalizationDataAssetsCommands
    {
        /// <summary>返回含根/行/字段上下文的只读诊断；不修改或保存资产。</summary>
        public static IReadOnlyList<string> Validate(ScriptableObjectConfigs root)
        {
            var errors = new List<string>();
            if (!root)
            {
                errors.Add("Select a YL DataAssets root.");
                return errors;
            }
            foreach (var diagnostic in ScriptableObjectConfigsController.Validate(root))
                errors.Add(root.name + " row " + diagnostic.Row + ": " + diagnostic.Message);
            if (root.configs == null)
                return errors;
            foreach (var row in root.configs)
            {
                if (!row)
                    continue;
                if (!ScriptableObjectConfigsController.IsOwned(root, row))
                    errors.Add(root.name + " row " + row.id + ": entry is not owned by this root.");
                foreach (var error in LocalizationReferenceAuthoring.ValidateObject(row))
                    errors.Add(root.name + " row " + row.id + ": " + error);
            }
            return errors;
        }

        [MenuItem("Assets/YLFramework/Localization/Validate Data References", false, 2110)]
        private static void ValidateSelected()
        {
            var root = Selection.activeObject as ScriptableObjectConfigs;
            var errors = Validate(root);
            if (errors.Count == 0)
                Debug.Log("Localization references are valid: " + root.name, root);
            else
                foreach (var error in errors)
                    Debug.LogError(error, root);
        }

        [MenuItem("Assets/YLFramework/Localization/Validate Data References", true)]
        private static bool CanValidateSelected() => Selection.activeObject is ScriptableObjectConfigs;
    }
}
