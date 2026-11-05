using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace YLFramework.Character2D
{
    /// <summary>根据顶部接触和向上形状投射识别触顶，供角色截停上升。</summary>
    internal class CheckTop
    {

        private ContactPoint2D[] _contacts = null;

        private CharacterController2D _character;

        private float _topAngle = 45;

        private float _lastCheckTime;

        private float _lastUpdateTopInfoTime;

        private float _timeInterval = 1.0f / 10.0f; // 每秒最多检测10次

        private int _contactsCount;

        /// <summary>最近一次接触数组的触顶判定，不要求公开的顶部投射检测已开启。</summary>
        public bool IsTopByContact
        {
            get; set;
        }

        /// <summary>借用接触数组更新触顶判断，启用顶部检测时按需复核投射信息。</summary>
        /// <param name="contacts">角色复用的接触缓冲区，不复制其内容。</param>
        /// <param name="characher">接收触顶状态和命中信息的角色。</param>
        /// <param name="count">本次有效接触数量。</param>
        /// <param name="topAngle">法线相对世界向下的夹角阈值。</param>
        internal void Check(ContactPoint2D[] contacts, CharacterController2D characher, int count, float topAngle)
        {
            this._contacts = contacts;
            this._character = characher;
            this._topAngle = topAngle;
            this._contactsCount = count;
            IsTopByContact = IsTopByContacts(count);

            if (IsTopByContact != characher.IsTouchingCeiling)
            {
                CheckTopInfo();
            }

            if (IsTopByContact)
            {
                UpdateTopInfo();
            }
        }

        /// <summary>顶部检测开启且满足检测间隔时，沿顶部碰撞体局部向上方向投射其形状。</summary>
        private void CheckTopInfo()
        {
            if (!_character.ChecksCeiling)
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


            Vector2 dir = _character.Top.transform.up;

            int count = 0;

            if (_character.Top is CircleCollider2D)
            {
                count = _character.Raycast(_character.Top as CircleCollider2D, dir);
            }
            else if (_character.Top is BoxCollider2D)
            {
                count = _character.Raycast(_character.Top as BoxCollider2D, dir);
            }
            else if (_character.Top is CapsuleCollider2D)
            {
                count = _character.Raycast(_character.Top as CapsuleCollider2D, dir);
            }

            CalculateIsTop(count);
        }

        /// <summary>下落时取消触顶，否则排除自身层级与触发器，采用首个角度合格的投射命中。</summary>
        /// <param name="count">角色 Hits 缓冲区中本次有效命中的数量。</param>
        private void CalculateIsTop(int count)
        {
            if (_character.SpeedVertical < 0)
            {
                _character.IsTouchingCeiling = false;
                return;
            }

            bool touching = false;

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

                float angle = Vector2.Angle(raycast.normal, Vector2.down);

                if (angle < _topAngle)
                {
                    _character.TopInfo = raycast;
                    touching = true;
                    break;
                }
            }
            _character.IsTouchingCeiling = touching;

        }

        /// <summary>过滤无效接触、触发器及特定 Effector 接触，再按向下法线夹角判断触顶。</summary>
        /// <param name="count">接触缓冲区中有效元素的数量。</param>
        private bool IsTopByContacts(int count)
        {
            bool isTop = false;

            float minAngle = 90;
            //int min_index = 0;

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

                if (_contacts[i].collider.usedByEffector)
                {
                    // 使用 Effector 时，角色最近点仍在对方包围盒内的接触不作为顶部接触。
                    Vector3 center = _contacts[i].collider.bounds.center;
                    Vector2 point = _character.Rigidbody.ClosestPoint(center);
                    if (_contacts[i].collider.bounds.Contains(point))
                    {
                        continue;
                    }
                }

                float angle = Vector2.Angle(_contacts[i].normal, Vector2.down);

                if (angle < minAngle)
                {
                    minAngle = angle;
                }
            }

            if (minAngle < _topAngle)
            {
                isTop = true;
            }

            return isTop;
        }

        /// <summary>触顶接触点或法线已明显变化时，按刷新间隔请求重新投射。</summary>
        private void UpdateTopInfo()
        {
            if (!_character.IsTouchingCeiling)
            {
                return;
            }

            if (Time.time - _lastUpdateTopInfoTime < 0.06f)
            {
                return;
            }

            for (int i = 0; i < _contactsCount; i++)
            {
                // 任一接触点的位置和法线都接近缓存时，继续保留原命中信息。
                bool d = Vector3.Distance(_contacts[i].point, _character.TopInfo.point) < 0.1f;
                bool a = Vector3.Angle(_contacts[i].normal, _character.TopInfo.normal) < 3;
                if (d && a)
                {
                    return;
                }
            }

            _lastUpdateTopInfoTime = Time.time;
            CheckTopInfo();
        }

    }

}
