using UnityEditor;

namespace YLFramework.Editor.Common
{
    /// <summary>为作者工具提供 Unity 项目设置页面的统一打开入口。</summary>
    public class ProjectSettingTools
    {
        /// <summary>打开并定位项目设置页面，不加载、创建或保存 YL 配置对象。</summary>
        /// <param name="path">SettingsProvider 注册的页面路径，例如 Project/Physics。</param>
        public static void OpenProjectSettings(string path)
        {
            SettingsService.OpenProjectSettings(path);
        }

    }
}
