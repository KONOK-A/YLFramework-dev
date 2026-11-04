using System;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.DataAssets
{
    /// <summary>保留具体SO子类的显式单例加载用途；LoadAsync返回本类拥有的借用句柄，Release或owner Dispose归还。</summary>
    /// <typeparam name="T">以自身类型封闭此基类的配置资产类型。</typeparam>
    public class SingletonScriptableObjectConfigs<T> : ScriptableObject where T : SingletonScriptableObjectConfigs<T>
    {
        private static ScriptableObjectAsset<T> _current;
        /// <summary>借读显式加载且租约仍有效的配置；未加载或已释放时为空，不自动加载或创建。</summary>
        public static T Instance => _current != null && _current.IsValid ? _current.Asset : null;

        /// <summary>显式取得本类型唯一的配置句柄；已有在途或有效加载时拒绝重复加载。</summary>
        /// <param name="resource">负责取得资产的资源系统。</param>
        /// <param name="address">配置资产的目录地址。</param>
        /// <returns>可由调用方释放的当前加载句柄，不创建配置副本。</returns>
        public static ScriptableObjectAsset<T> LoadAsync(ResourceSystem resource, ResourceAddress address)
        {
            if (_current != null && !_current.IsDisposed && (!_current.IsDone || _current.IsValid))
            {
                throw new InvalidOperationException($"Singleton DataAssets '{typeof(T).FullName}' is already loading or loaded. Release it first.");
            }

            _current?.Dispose();
            var next = new ScriptableObjectAsset<T>();
            _current = next;
            try
            {
                next.Start(resource, address, onReleased: () =>
                {
                    if (ReferenceEquals(_current, next))
                    {
                        _current = null;
                    }
                });
            }
            catch
            {
                if (ReferenceEquals(_current, next))
                {
                    _current = null;
                }

                throw;
            }
            return next;
        }

        /// <summary>清除当前单例入口并释放其加载句柄，不关闭资源系统。</summary>
        public static void Release()
        {
            var owned = _current;
            _current = null;
            owned?.Dispose();
        }
    }
}
