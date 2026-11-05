using UnityEngine;

namespace YLFramework.Character2D
{
    /// <summary>根据刚体接触筛选地面，并用向下形状投射复核落地状态和命中信息。</summary>
    internal class CheckGround
    {
        private ContactPoint2D[] _contacts = null;

        private int _contactsCount;

        private CharacterController2D _character;

        private float _groundAngle = 35;

        private float _lastCheckTime;

        private float _lastUpdateGroundInfoTime;

        /// <summary>最近一次传入接触数组的地面判定，与限频投射得到的落地状态可能暂时不同。</summary>
        public bool IsGroundedByContacts
        {
            get; protected set;
        }

        //private RaycastHit2D raycastHit;
        //private bool check = false;

        private float _timeInterval = 1.0f / 15; // 每秒最多检测15次

        /// <summary>借用接触数组的有效前缀更新地面判断，并按需复核投射信息。</summary>
        /// <param name="contacts">角色复用的接触缓冲区，不复制其内容。</param>
        /// <param name="characher">接收落地状态和命中信息的角色。</param>
        /// <param name="count">本次有效接触数量。</param>
        /// <param name="groundAngle">法线相对世界向上的夹角阈值。</param>
        internal void Check(ContactPoint2D[] contacts, CharacterController2D characher, int count, float groundAngle)
        {
            this._contacts = contacts;
            this._character = characher;
            _contactsCount = count;
            this._groundAngle = groundAngle;

            IsGroundedByContacts = HasGroundContacts(count);

            if (IsGroundedByContacts != characher.IsGrounded)
            {
                CheckGrounded();
            }

            if (IsGroundedByContacts)
            {
                UpdateGroundInfo();
            }
        }

        /// <summary>排除无效、触发器及最低点高于角色底部的接触，再按向上法线夹角筛选地面。</summary>
        /// <param name="count">接触缓冲区中有效元素的数量。</param>
        private bool HasGroundContacts(int count)
        {
            bool isGrounded = false;

            float minAngle = 90;

            for (int i = 0; i < count; i++)
            {
                if (_contacts[i].collider == null)
                {
                    continue;
                }

                if (_contacts[i].collider.isTrigger)
                {
                    continue;
                }
                if (_character.Bottom.bounds.min.y < _contacts[i].collider.bounds.min.y)
                {
                    continue;
                }

                float angle = Vector2.Angle(_contacts[i].normal, Vector2.up);

                if (angle < minAngle)
                {
                    minAngle = angle;
                }
            }
            if (minAngle < _groundAngle)
            {
                isGrounded = true;
            }

            return isGrounded;
        }

        /// <summary>地面检测开启且满足检测间隔时，沿底部碰撞体局部向下方向投射其形状。</summary>
        private void CheckGrounded()
        {
            if (!_character.ChecksGround)
            {
                return;
            }

            if (Time.time - _lastCheckTime < _timeInterval)
            {
                return;
            }

            _lastCheckTime = Time.time;

#if UNITY_EDITOR
            _character.CheckCount++;
#endif


            Vector2 dir = -_character.Bottom.transform.up;

            int count = 0;

            if (_character.Bottom is CircleCollider2D)
            {
                count = _character.Raycast(_character.Bottom as CircleCollider2D, dir);
            }
            else if (_character.Bottom is BoxCollider2D)
            {
                count = _character.Raycast(_character.Bottom as BoxCollider2D, dir);
            }
            else if (_character.Bottom is CapsuleCollider2D)
            {
                count = _character.Raycast(_character.Bottom as CapsuleCollider2D, dir);
            }

            CaculateIsGrounded(count);
        }

        /// <summary>上升时取消落地，否则排除自身层级与触发器，选取首个角度合格的投射命中。</summary>
        /// <param name="count">角色 Hits 缓冲区中本次有效命中的数量。</param>
        private void CaculateIsGrounded(int count)
        {
            if (_character.SpeedVertical > 0)
            {
                _character.IsGrounded = false;
                return;
            }

            bool grounded = false;

            for (int i = 0; i < count; i++)
            {
                RaycastHit2D raycast = _character.Hits[i];
                if (raycast.collider == null)
                {
                    continue;
                }

                if (raycast.collider.isTrigger)
                {
                    continue;
                }

                if (raycast.transform == _character.transform || raycast.transform.IsChildOf(_character.transform))
                {
                    continue;
                }

                float angle = Vector2.Angle(raycast.normal, Vector2.up);

                if (angle < _groundAngle)
                {
                    _character.GroundInfo = raycast;
                    grounded = true;
                    break;
                }
            }
            _character.IsGrounded = grounded;
        }

        /// <summary>落地接触点或法线已明显变化时，按刷新间隔请求重新投射。</summary>
        private void UpdateGroundInfo()
        {
            if (!_character.IsGrounded)
            {
                return;
            }

            if (Time.time - _lastUpdateGroundInfoTime < 0.1f)
            {
                return;
            }

            for (int i = 0; i < _contactsCount; i++)
            {
                // 任一接触点的位置和法线都接近缓存时，继续保留原命中信息。
                bool d = Vector3.Distance(_contacts[i].point, _character.GroundInfo.point) < 0.06f;
                bool a = Vector3.Angle(_contacts[i].normal, _character.GroundInfo.normal) < 3;
                if (d && a)
                {
                    return;
                }
            }
            _lastUpdateGroundInfoTime = Time.time;
            CheckGrounded();
        }

        /// <summary>使用上次 Check 绑定的角色和角度阈值，判断当前接触中是否存在合格地面。</summary>
        /// <param name="contacts">本次刚体接触缓冲区。</param>
        /// <param name="count">有效接触数量，仅检查此前缀。</param>
        public bool HasColliderBelow(ContactPoint2D[] contacts, int count)
        {

            for (int i = 0; i < count; i++)
            {
                if (contacts[i].collider == null)
                {
                    continue;
                }

                if (contacts[i].collider.isTrigger)
                {
                    continue;
                }
                if (_character.Bottom.bounds.min.y < contacts[i].collider.bounds.min.y)
                {
                    continue;
                }

                float angle = Vector2.Angle(contacts[i].normal, Vector2.up);

                if (angle < _groundAngle)
                {
                    return true;
                }
            }

            return false;
        }

    }
}
