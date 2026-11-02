using UnityEditor;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Samples.Resource.Editor
{
    /// <summary>在已稳定的 Editor 主线程调用；不打开窗口、不依赖 Selection，不代表独立 CI/batchmode 验收。</summary>
    public static class ResourceBuildRecipeExample
    {
        /// <summary>显式加载已保存的构建配方并交给正式服务执行，不初始化 Runtime。</summary>
        /// <param name="recipeAssetPath">Assets 下可编辑的 ResourceBuildRecipe 资产路径。</param>
        /// <param name="applicationKeys">应用持有的可选内容密钥来源，不保存到配方，也不由样例释放。</param>
        /// <returns>分阶段记录构建、发布和常量生成结果的报告，调用方须分别检查错误字段。</returns>
        /// <remarks>配方与项目定义须先显式保存，目标平台须已激活；发布和常量生成由配方决定，输出不在调用结束时删除。</remarks>
        public static ResourceEditorBuildReport Build(string recipeAssetPath, IResourceContentKeyProvider applicationKeys = null)
        {
            var recipe = AssetDatabase.LoadAssetAtPath<ResourceBuildRecipe>(recipeAssetPath);
            return ResourceEditorOperations.ExecuteRecipe(recipe, provider: applicationKeys);
        }
    }
}
