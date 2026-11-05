
using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Extensions;


namespace YLFramework.Platform2D
{
    /// <summary>用平台局部坐标定义一条乘客检测线段。</summary>
    [Serializable]
    public struct Area
    {
        /// <summary>线段起点的局部坐标，检测前转换为世界坐标。</summary>
        public Vector3 from;
        /// <summary>线段终点的局部坐标，检测前转换为世界坐标。</summary>
        public Vector3 to;
    }

    /// <summary>沿配置线段执行二维 Linecast，缓存可供移动平台携带的候选乘客。</summary>
    /// <remarks>自动检测由 Update 中的接触变化和限频刷新触发，与平台 FixedUpdate 不保证同步。</remarks>
    public class PlatformCatcher2D : MonoBehaviour
    {

        #region 字段

        /// <summary>直接可写的局部检测线段数组，每条线段独立查询。</summary>
        [Tooltip("检测区域")]
        public Area[] areas;

        private readonly RaycastHit2D[] _hits = new RaycastHit2D[10];

        /// <summary>最近一次线段查询的可写命中列表，可包含同一刚体或碰撞体的重复项。</summary>
        /// <remarks>下次检测会清空重填，停用时清空；刷新前不会自动移除已失效命中，不是稳定快照。</remarks>
        [Tooltip("当前平台接触到的游戏物体")]
        public List<RaycastHit2D> Contacts = new List<RaycastHit2D>();

        private LayerMask _layerMask;

        private int _layer;

        private List<ContactPoint2D> _contacts = new List<ContactPoint2D>();

        private readonly ContactPoint2D[] _contactsTemp = new ContactPoint2D[20];

        private List<ContactPoint2D> _allContacts = new List<ContactPoint2D>();

        private float _timer = 0;

        private Collider2D[] _collider2d;



        private float _checkTimer = 0;

        #endregion


        #region 生命周期

        /// <summary>按对象层建立二维碰撞掩码，并缓存当前子层级碰撞体。</summary>
        private void Awake()
        {
            _layer = gameObject.layer;
            _layerMask.Physics2DSetting(gameObject.layer);
            _collider2d = GetComponentsInChildren<Collider2D>();
        }

        /// <summary>重新缓存子层级碰撞体并清零检测计时，不立即执行线段查询。</summary>
        private void OnEnable()
        {
            _collider2d = GetComponentsInChildren<Collider2D>();
            _timer = _checkTimer = 0;
        }

        /// <summary>清除乘客、接触和查询缓冲区，避免停用后继续携带旧候选。</summary>
        private void OnDisable()
        {
            Contacts.Clear();
            _contacts.Clear();
            _allContacts.Clear();
            Array.Clear(_hits, 0, _hits.Length);
            Array.Clear(_contactsTemp, 0, _contactsTemp.Length);
        }

        /// <summary>立即重建层掩码和碰撞体缓存并检测乘客；组件未启用时仅清空结果。</summary>
        public void RefreshContacts()
        {
            if (!isActiveAndEnabled)
            {
                Contacts.Clear();
                return;
            }
            _layer = gameObject.layer;
            _layerMask.Physics2DSetting(_layer);
            _collider2d = GetComponentsInChildren<Collider2D>();
            Check();
        }


        /// <summary>存在同对象 BoxCollider2D 时，在其世界包围盒顶部上方生成默认局部检测线段。</summary>
        private void Reset()
        {
            BoxCollider2D box = GetComponent<BoxCollider2D>();
            if (box != null)
            {
                Vector2 center = box.bounds.center;
                Vector2 size = box.bounds.size;
                Vector2 from = new Vector2(center.x - size.x / 2, center.y + size.y / 2 + 0.1f);
                Vector2 to = new Vector2(center.x + size.x / 2, center.y + size.y / 2 + 0.1f);
                Area area = new Area();
                area.from = transform.worldToLocalMatrix.MultiplyPoint(from);
                area.to = transform.worldToLocalMatrix.MultiplyPoint(to);
                areas = new Area[] { area };
            }
        }

        /// <summary>同步对象层变化，检查接触变化，并对已有乘客以约 0.1 秒间隔刷新线段结果。</summary>
        private void Update()
        {
            if (gameObject.layer != _layer)
            {
                _layer = gameObject.layer;
                _layerMask.Physics2DSetting(gameObject.layer);
            }

            CheckWithTimer();

            if (Contacts.Count > 0)
            {
                _checkTimer += Time.deltaTime;
                if (_checkTimer >= 0.1f)
                {
                    Check();
                    _checkTimer = 0;
                }
            }

        }


        #endregion

        #region 方法


        /// <summary>按约每秒 30 次的上限收集碰撞体接触，仅在接触集合变化时重查乘客。</summary>
        private void CheckWithTimer()
        {
            _timer += Time.deltaTime;

            if (_timer <= 1.0f / 30.0f)
            {
                return;
            }

            _timer = 0;

            if (_collider2d == null || _collider2d.Length == 0)
            {
                return;
            }

            _allContacts.Clear();

            for (int i = 0; i < _collider2d.Length; i++)
            {
                Collider2D collider = _collider2d[i];
                if (!collider)
                {
                    continue;
                }

                int contactsCount = collider.GetContacts(_contactsTemp);

                for (int j = 0; j < contactsCount; j++)
                {
                    _allContacts.Add(_contactsTemp[j]);
                }
            }


            if (!CompareTheSame())
            {
                Check();

                _contacts.Clear();
                _contacts.AddRange(_allContacts);
            }


        }

        /// <summary>将局部线段变换到世界坐标并查询，排除触发器及平台自身层级后重填乘客列表。</summary>
        private void Check()
        {
            Contacts.Clear();
            if (areas == null || areas.Length == 0)
            {
                return;
            }

            foreach (Area area in areas)
            {
                // 每条线段最多写入 10 个命中，不保证覆盖整个平台周围的全部乘客。
                Vector3 from = transform.localToWorldMatrix.MultiplyPoint(area.from);
                Vector3 to = transform.localToWorldMatrix.MultiplyPoint(area.to);

#if UNITY_6000_0_OR_NEWER
                ContactFilter2D contactFilter = CreateLegacyFilter(_layerMask.value, float.NegativeInfinity, float.PositiveInfinity);
                int count = Physics2D.defaultPhysicsScene.Linecast(from, to, contactFilter, _hits);
#else
                int count = Physics2D.LinecastNonAlloc(from, to, _hits, _layerMask.value);
#endif

                for (int i = 0; i < count; i++)
                {
                    if (!_hits[i].collider || _hits[i].collider.isTrigger || _hits[i].transform == transform || _hits[i].transform.IsChildOf(transform))
                    {
                        continue;
                    }

                    Contacts.Add(_hits[i]);
                }
            }
            Array.Clear(_hits, 0, _hits.Length);
        }


        /// <summary>比较接触数量及碰撞体 Transform 是否仍出现在旧集合中，不比较接触点与法线。</summary>
        private bool CompareTheSame()
        {

            if (_allContacts.Count != _contacts.Count)
            {
                return false;
            }

            for (int i = 0; i < _allContacts.Count; i++)
            {
                if (!_allContacts[i].collider || !ContainsTransform(_allContacts[i].collider.transform))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>判断旧接触列表中是否仍有存活碰撞体使用指定 Transform。</summary>
        /// <param name="trans">待查找的碰撞体变换。</param>
        private bool ContainsTransform(Transform trans)
        {

            foreach (var item in _contacts)
            {
                if (item.collider && item.collider.transform == trans)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>按层掩码、深度和全局触发器查询开关构造二维查询过滤器。</summary>
        /// <param name="layerMask">允许查询的层位掩码。</param>
        /// <param name="minDepth">允许的最小深度。</param>
        /// <param name="maxDepth">允许的最大深度。</param>
        internal static ContactFilter2D CreateLegacyFilter(int layerMask, float minDepth, float maxDepth)
        {
            ContactFilter2D result = default(ContactFilter2D);
            result.useTriggers = Physics2D.queriesHitTriggers;
            result.SetLayerMask(layerMask);
            result.SetDepth(minDepth, maxDepth);
            return result;
        }


        #endregion





    }

}
