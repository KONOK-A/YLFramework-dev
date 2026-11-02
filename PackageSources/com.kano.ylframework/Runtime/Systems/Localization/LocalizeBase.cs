using System;
using UnityEngine;
using YLFramework.Events;

namespace YLFramework.Localization
{
    /// <summary>本地化显示绑定基类，启用时订阅刷新，停用时解除自身订阅。</summary>
    [DisallowMultipleComponent]
    public abstract class LocalizeBase : MonoBehaviour
    {
        [SerializeField] private int id;
        private IUnregister _subscription;
        /// <summary>绑定的本地化条目 ID；变更后仅在组件启用时刷新显示。</summary>
        public int Id
        {
            get => id; set
            {
                if (id == value)
                {
                    return;
                }
                id = value;
                Refresh();
            }
        }

        /// <summary>替换自身旧订阅并立即刷新，以接收之后登记或切换语言的通知。</summary>
        protected virtual void OnEnable()
        {
            _subscription?.Unregister();
            _subscription = LocalizationManager.Changed.Register(Refresh);
            Refresh();
        }

        /// <summary>解除本组件的刷新订阅，不关闭全局本地化责任域。</summary>
        protected virtual void OnDisable()
        {
            _subscription?.Unregister();
            _subscription = null;
        }

        /// <summary>直接更新显示，不切换GameObject活动状态；停用时不加载或回写UI。</summary>
        public void Refresh()
        {
            if (!isActiveAndEnabled)
            {
                return;
            }

            try
            {
                OnLanguageValueChange();
            }
            catch (Exception error)
            {
                Debug.LogException(error, this);
            }
        }

        /// <summary>派生绑定在此查询并应用当前内容；基类刷新入口负责记录应用异常。</summary>
        protected virtual void OnLanguageValueChange()
        {
        }
    }
}
