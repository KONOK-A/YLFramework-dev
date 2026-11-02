using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace YLFramework.Extensions
{
    /// <summary>
    /// 按是否忽略时间缩放读取帧间隔，并对未缩放的长帧使用固定回退值。
    /// </summary>
    public class TimeExtensions
    {
        /// <summary>
        /// 返回帧间隔秒数；忽略时间缩放时，unscaledDeltaTime 超过 0.2 秒就返回 0.02 秒。
        /// 使用时间缩放时直接返回 Time.deltaTime，不应用长帧回退。
        /// </summary>
        /// <param name="ignoreTimeScale">是否读取不受 Time.timeScale 影响的帧间隔。</param>
        /// <returns>当前帧间隔或未缩放长帧的固定回退值，单位为秒。</returns>
        public static float GetDeltaTime(bool ignoreTimeScale)
        {
            if (ignoreTimeScale)
            {
                return Time.unscaledDeltaTime > 0.2f ? 0.02f : Time.unscaledDeltaTime;
            }

            return Time.deltaTime;
        }

    }
}
