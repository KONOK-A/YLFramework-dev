
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Platform2D
{
    /// <summary>提交路径位置后，按平台世界位移携带最近检测到的二维刚体乘客。</summary>
    /// <remarks>乘客位移由平台直接加到刚体 position，不依赖角色控制器，也不执行额外障碍投射。</remarks>
    [RequireComponent(typeof(PlatformCatcher2D))]
    public class MoveablePlatform2D : Moveable
    {
        private PlatformCatcher2D _catcher;
        private readonly List<RaycastHit2D> _snapshot = new List<RaycastHit2D>();
        private readonly HashSet<Rigidbody2D> _movedBodies = new HashSet<Rigidbody2D>();


        /// <summary>取得乘客检测器后，再执行基类的自动路径启动。</summary>
        protected override void Awake()
        {
            _catcher = GetComponent<PlatformCatcher2D>();
            base.Awake();
        }


        /// <summary>设置平台世界位置，将本次 XY 位移对平台层级外每个存活的乘客刚体施加一次。</summary>
        /// <param name="target">平台的目标世界位置，Z 分量不传递给二维乘客。</param>
        protected override void Move(Vector3 target)
        {
            Vector2 detal = target - transform.position;
            transform.position = target;

            // 检测结果来自最近一次刷新而非此刻重新检测；先复制列表，再按刚体去重，避免多区域或多碰撞体重复携带。
            _snapshot.Clear();
            _snapshot.AddRange(_catcher.Contacts);
            _movedBodies.Clear();
            foreach (var item in _snapshot)
            {
                if (!item.rigidbody || item.rigidbody.transform == transform || item.rigidbody.transform.IsChildOf(transform) || !_movedBodies.Add(item.rigidbody))
                {
                    continue;
                }

                item.rigidbody.position += detal;
            }
            _snapshot.Clear();
            _movedBodies.Clear();
        }



    }

}
