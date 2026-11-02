using System.Collections;
using System.Collections.Generic;
using System.Dynamic;
using UnityEngine;

namespace YLFramework.Character2D
{
    /// <summary>根据接触法线筛选墙面，并按右侧优先的双向形状投射复核触墙状态。</summary>
    internal class CheckWall
    {
        private ContactPoint2D[] _contacts = null;

        private CharacterController2D _character;

        private float _wallAngle = 20;

        private float _lastCheckTime;

        private float _lastUpdateWallInfoTime;

        private float _timeInterval = 1.0f / 10.0f; // 每秒最多检测 10 次

        private int _contactsCount;

        /// <summary>借用接触数组判断墙面，必要时刷新投射状态，并在回调使角色失效时终止。</summary>
        /// <param name="contacts">角色复用的接触缓冲区，不复制其内容。</param>
        /// <param name="characher">接收触墙状态和命中信息的角色。</param>
        /// <param name="count">本次有效接触数量。</param>
        /// <param name="wallAngle">法线相对世界水平轴的夹角阈值。</param>
        internal void Check(ContactPoint2D[] contacts, CharacterController2D characher, int count, float wallAngle)
        {
            this._contacts = contacts;
            this._character = characher;
            this._wallAngle = wallAngle;

            this._contactsCount = count;
            int version = characher.PhysicsVersion;

            int wallIndex = 0;

            bool isWallByContacts = IsWallByContacts(count, out wallIndex);

            if (isWallByContacts != characher.IsTouchingWall)
            {
                CheckWallTouch();
            }

            if (!characher.IsPhysicsCurrent(version))
            {
                return;
            }

            if (isWallByContacts)
            {
                UpdateWallInfo(wallIndex);
            }
        }

        /// <summary>按近水平法线筛选墙面，箱体和胶囊还要求法线近似垂直于对方局部向上轴。</summary>
        /// <param name="count">接触缓冲区中有效元素的数量。</param>
        /// <param name="index">最接近水平轴的候选接触索引；返回 false 时不代表有效墙面。</param>
        private bool IsWallByContacts(int count, out int index)
        {
            bool isWall = false;
            float minAngle = 90;
            int minIndex = 0;

            for (int i = 0; i < count; i++)
            {
                ContactPoint2D raycast = _contacts[i];
                if (raycast.collider == null)
                {
                    continue;
                }

                if (raycast.collider.isTrigger)
                {
                    continue;
                }

                if (raycast.collider is BoxCollider2D || raycast.collider is CapsuleCollider2D)
                {
                    if (Mathf.Abs(Vector2.Dot(raycast.normal, raycast.collider.transform.up)) > 0.01f)
                    {
                        continue;
                    }
                }

                float angle = Vector2.Angle(raycast.normal, raycast.normal.x < 0 ? -Vector2.right : Vector2.right);

                if (angle < minAngle)
                {
                    minAngle = angle;
                    minIndex = i;
                }
            }


            if (minAngle < _wallAngle)
            {
                isWall = true;
            }

            index = minIndex;
            return isWall;
        }

        /// <summary>触墙接触点或法线已明显变化时，按刷新间隔请求重新投射。</summary>
        /// <param name="index">接触筛选得到的候选索引，当前实现不使用此参数。</param>
        private void UpdateWallInfo(int index)
        {
            if (!_character.IsTouchingWall)
            {
                return;
            }

            if (Time.time - _lastUpdateWallInfoTime < 0.1f)
            {
                return;
            }

            for (int i = 0; i < _contactsCount; i++)
            {
                // 任一接触点的位置和法线都接近缓存时，继续保留原命中信息。
                bool d = Vector3.Distance(_contacts[i].point, _character.WallInfo.point) < 0.1f;
                bool a = Vector3.Angle(_contacts[i].normal, _character.WallInfo.normal) < 3;
                if (d && a)
                {
                    return;
                }
            }

            _lastUpdateWallInfoTime = Time.time;
            CheckWallTouch();
        }


        /// <summary>限频投射右侧形状，未触墙且角色仍有效时再投射左侧形状。</summary>
        private void CheckWallTouch()
        {
            if (!_character.ChecksWalls)
            {
                return;
            }

            int version = _character.PhysicsVersion;

            if (Time.time - _lastCheckTime < _timeInterval)
            {
                return;
            }

            _lastCheckTime = Time.time;

#if UNITY_EDITOR
            _character.CheckCount++;
#endif


            Vector2 dir = _character.Right.transform.right;

            // 投射轴随碰撞体旋转，X 轴负缩放时再反向，以匹配该侧的世界方向。
            if (_character.Right.transform.lossyScale.x < 0)
            {
                dir = -dir;
            }

            int count = 0;

            if (_character.Right is CircleCollider2D)
            {
                count = _character.Raycast(_character.Right as CircleCollider2D, dir);
            }
            else if (_character.Right is BoxCollider2D)
            {
                count = _character.Raycast(_character.Right as BoxCollider2D, dir);

            }
            else if (_character.Right is CapsuleCollider2D)
            {
                count = _character.Raycast(_character.Right as CapsuleCollider2D, dir);
            }

            CaculateIsWallTouch(count);

            if (!_character.IsPhysicsCurrent(version))
            {
                return;
            }

            if (_character.IsTouchingWall)
            {
                return;
            }

            dir = -_character.Left.transform.right;

            if (_character.Left.transform.lossyScale.x < 0)
            {
                dir = -dir;
            }

            if (_character.Left is CircleCollider2D)
            {
                count = _character.Raycast(_character.Left as CircleCollider2D, dir);
            }
            else if (_character.Left is BoxCollider2D)
            {
                count = _character.Raycast(_character.Left as BoxCollider2D, dir);
            }
            else if (_character.Left is CapsuleCollider2D)
            {
                count = _character.Raycast(_character.Left as CapsuleCollider2D, dir);
            }

            CaculateIsWallTouch(count);
        }

        /// <summary>排除自身层级、触发器和不合格法线后，使用首个墙面命中更新触墙状态。</summary>
        /// <param name="count">角色 Hits 缓冲区中本次有效命中的数量。</param>
        private void CaculateIsWallTouch(int count)
        {
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

                if (raycast.collider is BoxCollider2D || raycast.collider is CapsuleCollider2D)
                {
                    if (Mathf.Abs(Vector2.Dot(raycast.normal, raycast.transform.up)) > 0.01f)
                    {
                        continue;
                    }
                }

                float angle = Vector2.Angle(raycast.normal, raycast.normal.x < 0 ? -Vector2.right : Vector2.right);

                if (angle < _wallAngle)
                {
                    _character.WallInfo = raycast;
                    touching = true;
                    break;
                }
            }
            _character.IsTouchingWall = touching;


        }

    }

}
