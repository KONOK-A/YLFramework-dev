using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Foundation;

namespace YLFramework.Core
{
    public abstract partial class Module
    {
        #region Controller

        /// <summary>
        /// 注册外部构造的本地Controller；已完成注册的同实例重复调用幂等，冲突抛出异常。
        /// </summary>
        public void AddController(Controller controller)
        {
            ControllerManager.AddController(this, controller);
        }

        /// <summary>
        /// 仅移除本模块拥有且与索引完全一致的实例；退出异常也会清除索引及归属。
        /// </summary>
        public void RemoveController(Controller controller)
        {
            ControllerManager.RemoveController(this, controller);
        }

        /// <summary>
        /// 只查询本模块已注册的精确类型Controller，不创建；缺失返回null。
        /// 跨模块共享须在明确的拥有者Module上调用查询，不改变归属或生命周期责任。
        /// </summary>
        public Controller GetController(Type controllerType)
        {
            return ControllerManager.GetController(this, controllerType);
        }

        /// <summary>
        /// 只查询本模块已注册的精确类型Controller，不创建；缺失返回null。
        /// </summary>
        public T GetController<T>() where T : Controller
        {
            return GetController(typeof(T)) as T;
        }

        /// <summary>
        /// 按完整类型名只查询本模块Controller，不创建；类型或实例不存在时返回null。
        /// </summary>
        public Controller GetController(string controllerTypeFullName)
        {
            if (string.IsNullOrEmpty(controllerTypeFullName))
            {
                return null;
            }

            Type type = AssemblyTool.GetType(controllerTypeFullName);
            if (type == null)
            {
                return null;
            }

            return GetController(type);
        }

        /// <summary>
        /// 按完整类型名加载本模块Controller，缺失时才创建并注册到本模块。
        /// </summary>
        /// <param name="controllerTypeFullName">业务逻辑脚本名称(包含命名空间)</param>
        /// <returns>已有实例或完成初始化的新实例。</returns>
        /// <exception cref="Exception">无法解析指定的完整类型名。</exception>
        public Controller LoadController(string controllerTypeFullName)
        {
            if (string.IsNullOrEmpty(controllerTypeFullName))
            {
                throw new ArgumentException("Controller type name is required.", nameof(controllerTypeFullName));
            }

            Type type = AssemblyTool.GetType(controllerTypeFullName);
            if (type == null)
            {
                throw new Exception(string.Format("未查询到类:{0}!", controllerTypeFullName));
            }

            return LoadController(type);
        }

        /// <summary>
        /// 移除本模块精确类型的 Controller；缺失时无操作，不影响其他模块。
        /// </summary>
        /// <param name="controllerType">业务逻辑类型</param>
        public void RemoveController(Type controllerType)
        {
            ControllerManager.RemoveController(this, controllerType);
        }

        /// <summary>
        /// 加载本模块Controller，缺失时才创建；初始化期间返回临时注册对象，退出期间拒绝Load。
        /// </summary>
        /// <param name="controllerType">业务逻辑类型</param>
        /// <returns>已有实例或完成初始化的新实例。</returns>
        public Controller LoadController(Type controllerType)
        {
            return ControllerManager.LoadController(this, controllerType);
        }

        /// <summary>
        /// 按调用时快照逐个移除本模块 Controller；某项退出抛错时停止，未处理项仍保留。
        /// </summary>
        public void ClearController()
        {
            ControllerManager.ClearController(this);
        }

        /// <summary>
        /// 加载本模块Controller，缺失时才创建；初始化期间返回临时注册对象，退出期间拒绝Load。
        /// </summary>
        /// <typeparam name="T">要精确匹配的 Controller 类型。</typeparam>
        /// <returns>已有实例或完成初始化的新实例。</returns>
        public T LoadController<T>() where T : Controller
        {
            return ControllerManager.LoadController<T>(this);
        }

        /// <summary>
        /// 移除本模块精确类型的 Controller；缺失时无操作。
        /// </summary>
        /// <typeparam name="T">类型</typeparam>
        public void RemoveController<T>() where T : Controller
        {
            RemoveController(typeof(T));
        }


        /// <summary>
        /// 新建并注册本地Controller；类型已注册时在构造前拒绝，不借用其他模块实例。
        /// </summary>
        public T CreateController<T>() where T : Controller
        {
            return ControllerManager.CreateController<T>(this);
        }

        /// <summary>
        /// 新建并注册本地Controller；类型已注册时在构造前拒绝，不借用其他模块实例。
        /// </summary>
        public Controller CreateController(Type type)
        {
            return ControllerManager.CreateController(this, type);
        }

        /// <summary>
        /// 按完整类型名新建本地Controller，不查询或借用其他模块的实例。
        /// </summary>
        public Controller CreateController(string controllerTypeFullName)
        {
            if (string.IsNullOrEmpty(controllerTypeFullName))
            {
                throw new ArgumentException("Controller type name is required.", nameof(controllerTypeFullName));
            }

            Type type = AssemblyTool.GetType(controllerTypeFullName);
            if (type == null)
            {
                throw new Exception(string.Format("未查询到类:{0}!", controllerTypeFullName));
            }

            return CreateController(type);
        }

        #endregion
    }
}
