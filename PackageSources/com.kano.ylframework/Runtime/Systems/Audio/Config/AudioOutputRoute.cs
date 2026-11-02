using System;
using UnityEngine.Audio;

namespace YLFramework.Audio
{
    /// <summary>单次播放的可选输出路由；零值继承所属分类的运行时输出组。</summary>
    [Serializable]
    public struct AudioOutputRoute
    {
        /// <summary>区分分类路由继承、显式 Mixer 输出组与直接输出。</summary>
        public enum RouteMode
        {
            /// <summary>随所属分类的当前默认输出组变化。</summary>
            Inherit,
            /// <summary>使用请求显式指定的有效输出组。</summary>
            MixerGroup,
            /// <summary>绕过分类默认组和显式组，直接输出。</summary>
            Direct
        }

        /// <summary>零值表示继承；仅 MixerGroup 模式允许携带 Group 引用。</summary>
        public RouteMode Mode;
        /// <summary>由调用方保活的输出组，仅 MixerGroup 路由必须提供。</summary>
        public AudioMixerGroup Group;
        /// <summary>使用分类当前默认路由，并跟随之后的默认组变化。</summary>
        public static AudioOutputRoute Inherit => default;
        /// <summary>即使分类配置了默认组，也绕过 Mixer 路由直接输出。</summary>
        public static AudioOutputRoute Direct => new AudioOutputRoute { Mode = RouteMode.Direct };

        /// <summary>创建独立于分类默认值的显式组路由，不接管资源所有权。</summary>
        /// <param name="group">由调用方保活的有效 Mixer 输出组。</param>
        public static AudioOutputRoute ToMixer(AudioMixerGroup group)
        {
            var route = new AudioOutputRoute { Mode = RouteMode.MixerGroup, Group = group };
            route.Validate();
            return route;
        }

        /// <summary>校验路由模式及组引用组合，拒绝已销毁的显式组。</summary>
        internal void Validate()
        {
            if ((uint)Mode > (uint)RouteMode.Direct)
            {
                throw new ArgumentOutOfRangeException(nameof(Mode));
            }

            if (Mode == RouteMode.MixerGroup)
            {
                if (!Group)
                {
                    throw new ArgumentException("A mixer route requires a live output group.", nameof(Group));
                }
            }
            else if (HasReference(Group))
            {
                throw new ArgumentException("Only a MixerGroup route can contain an output group.", nameof(Group));
            }
        }

        // 序列化空引用可能仍有实例 ID 为零的托管包装，不能只按 C# null 判断是否填写。
        internal static bool HasReference(UnityEngine.Object value) => !ReferenceEquals(value, null) && value.GetInstanceID() != 0;

        /// <summary>解析当前输出组；直接输出或失活组统一返回 null。</summary>
        internal AudioMixerGroup Resolve(AudioMixerGroup categoryGroup)
        {
            var group = Mode == RouteMode.Inherit ? categoryGroup : Mode == RouteMode.MixerGroup ? this.Group : null;
            return group ? group : null;
        }
    }
}
