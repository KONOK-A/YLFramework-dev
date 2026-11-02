using UnityEngine;
using UnityEngine.Scripting;

namespace YLFramework.Samples.HybridCLR.HotUpdate
{
    /// <summary>附着于样例 Prefab 的真实热更脚本，供宿主在代码加载后检查反序列化结果。</summary>
    [Preserve]
    public sealed class ExampleBehaviour : MonoBehaviour
    {
        /// <summary>携带 Support 版本的实例标记，便于与 Module 和异步输出交叉核对。</summary>
        public string Marker => VersionInfo.Version + "-prefab";
    }
}
