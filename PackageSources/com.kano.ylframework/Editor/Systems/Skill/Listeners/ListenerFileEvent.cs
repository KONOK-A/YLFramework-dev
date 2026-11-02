using UnityEditor;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Skill;

namespace YLFramework.Editor.Skill
{
    /// <summary>接入技能配表资产的删除清理与双击打开行为。</summary>
    public class ListenerFileEvent : UnityEditor.AssetModificationProcessor
    {
        /// <summary>技能配表删除前请求清理其受管技能目录，返回 DidNotDelete 让 Unity 继续删除原资产。</summary>
        /// <remarks>不检查目录删除结果；目录清理与配表删除不构成可回滚的原子操作。</remarks>
        public static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions option)
        {
            SkillsConfig skillsConfig = AssetDatabase.LoadAssetAtPath<SkillsConfig>(assetPath);

            if (skillsConfig != null)
            {

                string dir = SkillsConfigController.GetSkillConfigDataPath(skillsConfig);
                AssetDatabase.DeleteAsset(dir);
            }

            return AssetDeleteResult.DidNotDelete;
        }


        /// <summary>识别技能配表并打开专用窗口，返回是否接管本次打开请求。</summary>
        [UnityEditor.Callbacks.OnOpenAsset(0)]
        private static bool OnOpenAsset(int insId, int line)
        {
            SkillsConfig obj = EditorUtility.InstanceIDToObject(insId) as SkillsConfig;
            if (obj != null)
            {
                //EditorWindow.GetWindow<FSMEditorWindow>().Show();
                SkillsConfigInspector.OpenSkillsConfig(obj);
            }
            return obj != null;
        }

    }
}
