using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Extensions
{

    /// <summary>
    /// 将另一个 Animator 的部分参数及当前层状态复制到目标 Animator，不更换动画控制器资源。
    /// </summary>
    public static class AnimatorExtensions
    {
        /// <summary>
        /// 复制 Float、Int、Bool 参数，以及目标每层对应的当前状态、权重和归一化时间，再以 Time.deltaTime 推进目标。
        /// 不复制 Trigger 或过渡进度；调用方须保证双方的参数、层索引和状态兼容。
        /// </summary>
        /// <param name="animator">接收参数和状态并立即执行一次 Update 的目标 Animator。</param>
        /// <param name="other">提供参数和状态的源 Animator。</param>
        public static void Sync(this Animator animator, Animator other)
        {
            // 同步参数
            foreach (var parameter in other.parameters)
            {
                int hash = parameter.nameHash;
                switch (parameter.type)
                {
                    case AnimatorControllerParameterType.Float:
                        animator.SetFloat(hash, other.GetFloat(hash));
                        break;
                    case AnimatorControllerParameterType.Int:
                        animator.SetInteger(hash, other.GetInteger(hash));
                        break;
                    case AnimatorControllerParameterType.Bool:
                        animator.SetBool(hash, other.GetBool(hash));
                        break;
                }
            }
            // 同步正在播放的动画
            int layerCount = animator.layerCount;
            for (int i = 0; i < layerCount; i++)
            {
                animator.SetLayerWeight(i, other.GetLayerWeight(i));
                AnimatorStateInfo info = other.GetCurrentAnimatorStateInfo(i);
                animator.Play(info.fullPathHash, i, info.normalizedTime);
            }
            // 更新
            animator.Update(Time.deltaTime);
        }
    }
}
