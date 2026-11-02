using UnityEngine;

namespace YLFramework.Actions
{
    /// <summary>将 Unity AudioSource 的播放过程接入动作组合。</summary>
    public static class AudioSourceShortCutExtensions
    {
        /// <summary>创建启动时调用 Play、随后等待 AudioSource 停止播放的动作。</summary>
        /// <param name="self">要播放并观察的 AudioSource。</param>
        /// <returns>未启动的动作；取消动作不调用 Stop，也不销毁 AudioSource。</returns>
        /// <remarks>循环播放不会自行完成；AudioSource 销毁后也不会自动完成，应由调用方取消。</remarks>
        public static IAction PlayAction(this AudioSource self)
        {
            return ActionKit.Custom<AudioSource>(api =>
            {
                api.OnStart(() =>
                {
                    api.Data = self;
                    self.Play();
                }).OnExecute(_ =>
                {
                    if (api.Data && !api.Data.isPlaying)
                    {
                        api.Finish();
                    }
                });
            });
        }
    }
}
