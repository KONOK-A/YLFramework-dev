using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Extensions
{
    /// <summary>
    /// 在指定场景的根对象中按名称查找，不递归搜索子层级。
    /// </summary>
    public static class SceneExtensions
    {

        /// <summary>
        /// 获取根对象数组后，返回首个名称完全匹配的根对象，区分大小写；也可匹配未激活的根对象。
        /// </summary>
        /// <param name="scene">提供根对象的场景，须满足 Scene.GetRootGameObjects 的有效性要求。</param>
        /// <param name="name">待精确匹配的对象名称，不作为层级路径解析。</param>
        /// <returns>首个同名根对象，未找到时返回 null。</returns>
        public static GameObject FindRootGameObject(this Scene scene, string name)
        {
            GameObject[] objs = scene.GetRootGameObjects();
            foreach (GameObject obj in objs)
            {
                if (obj.name == name)
                {
                    return obj;
                }
            }

            return null;
        }
    }

}
