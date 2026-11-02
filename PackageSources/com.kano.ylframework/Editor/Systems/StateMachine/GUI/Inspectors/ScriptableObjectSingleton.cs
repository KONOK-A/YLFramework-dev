using UnityEngine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>为 Inspector 选择代理按需创建临时 ScriptableObject，不创建或加载持久化资产。</summary>
    /// <typeparam name="T">继承此泛型基类的具体代理类型。</typeparam>
    public class ScriptableObjectSingleton<T> : ScriptableObject where T : ScriptableObjectSingleton<T>
    {
        private static T _instance;

        /// <summary>取得当前类型的临时代理实例，引用失效时重新创建。</summary>
        public static T Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = CreateInstance<T>();
                }
                return _instance;
            }
        }

        /// <summary>对象停用时清除该泛型类型的静态缓存，后续访问重新创建。</summary>
        private void OnDisable()
        {
            _instance = null;
        }

    }

}
