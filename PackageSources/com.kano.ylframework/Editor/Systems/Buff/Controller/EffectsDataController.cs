using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Buff;
using YLFramework.Editor.Common;


namespace YLFramework.Editor.Buff
{
    /// <summary>创建效果定义资产并登记到 Buff 项目设置，供效果列表与选择器使用。</summary>
    public class EffectsDataController
    {

        /// <summary>创建并选中效果资产，登记并保存设置；效果 ID 与显示名由外层创建流程赋值。</summary>
        /// <param name="scriptableObject">待创建的 EffectData 数据实例。</param>
        /// <param name="path">保存目录，不存在时创建。</param>
        /// <param name="ping">是否在 Project 窗口中高亮。</param>
        /// <param name="preName">候选文件名；为空时使用实例类型名。</param>
        public static void CreateAsset(ScriptableObject scriptableObject, string path, bool ping = true, string preName = null)
        {
            if (!System.IO.Directory.Exists(path))
            {
                System.IO.Directory.CreateDirectory(path);
                AssetDatabase.Refresh();
            }

            string name = GetName(string.IsNullOrEmpty(preName) ? scriptableObject.GetType().Name : preName, path);
            AssetDatabase.CreateAsset(scriptableObject, string.Format("{0}/{1}.asset", path, name));
            Selection.activeObject = scriptableObject;


            EffectData currentEffectData = scriptableObject as EffectData;
            // 保存
            BuffSystemSettings.Instance.AddEffectData(currentEffectData);

            if (ping)
            {
                EditorGUIUtility.PingObject(scriptableObject);
            }
        }

        /// <summary>以设置中的效果显示名为占用集合，为候选名称递增数字后缀。</summary>
        private static string GetName(string name, string path)
        {

            if (!AssetDatabase.IsValidFolder(path))
            {
                return name;
            }

            int count = 0;

            if (Regex.IsMatch(name, "\\([0-9]+\\)$"))
            {
                for (int i = name.Length - 1; i >= 0; i--)
                {
                    if (name[i] == '(')
                    {
                        string c = name.Substring(i + 1, name.Length - i - 2);
                        count = int.Parse(c);

                        name = name.Substring(0, i);
                        break;
                    }
                }
            }

            name = name.TrimEnd();

            string tempName = name;

            while (IsContainEffectName(tempName))
            {
                count++;
                tempName = string.Format("{0} ({1})", name, count);
            }

            return tempName;
        }


        /// <summary>检查项目设置登记的效果是否已使用指定显示名。</summary>
        private static bool IsContainEffectName(string name)
        {
            foreach (var item in BuffSystemSettings.Instance.AllEffectDatas)
            {
                if (item.effectName == name)
                {
                    return true;
                }
            }

            return false;
        }

    }

}
