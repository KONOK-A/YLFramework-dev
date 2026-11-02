using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Foundation;

namespace YLFramework.Core
{
    /// <summary>
    /// 模块生命周期；启动失败复用关闭管线结束逻辑所有权后才报告。
    /// </summary>
    public enum ModuleState
    {
        /// <summary>新建且尚未由启动管线推进的实例。</summary>
        Created,
        /// <summary>已发布启动请求和注册；允许查询，拒绝Close与Abort取消。</summary>
        Starting,
        /// <summary>OnStart成功结束；重复启动不再次执行生命周期。</summary>
        Running,
        /// <summary>正在结束业务对象与在途表现请求；此时仍可查到模块注册。</summary>
        Closing,
        /// <summary>已解除注册且核心对象索引无残留，允许显式重启同一实例。</summary>
        Closed,
        /// <summary>启动失败的内部过渡状态；回滚完成后进入Closed。</summary>
        StartFailed,
        /// <summary>保留的旧失败状态；唯一关闭管线在报告错误前已进入Closed。</summary>
        CloseFailed
    }

    /// <summary>管理本模块 Model、Controller 与 View 的业务归属，并提供启动、关闭及加载入口。</summary>
    /// <remarks>
    /// 同步LoadView只返回已完成初始化的实例，参数、资源或初始化错误直接抛出。
    /// LoadViewArgs接管成功即由本次操作负责归还，入口拒绝也消耗参数；回调只借读，不得保存容器。
    /// 异步View请求独立创建；Abort为逻辑取消，所属模块关闭后失效，终态与原始Exception只提交一次。
    /// </remarks>
    public abstract partial class Module
    {

        #region 字段
        /// <summary>
        /// 按 Model 类型与实例标识建立数据索引。
        /// </summary>
        internal Dictionary<Type, Dictionary<long, Model>> Models = new Dictionary<Type, Dictionary<long, Model>>();
        /// <summary>
        /// 按资源名称与实例标识建立视图索引。
        /// </summary>
        internal Dictionary<string, Dictionary<int, View>> Views = new Dictionary<string, Dictionary<int, View>>();

        /// <summary>
        /// 按实际类型分组保存视图，供类型查询使用。
        /// </summary>
        internal Dictionary<Type, List<View>> TypeViews = new Dictionary<Type, List<View>>();
        // 不依赖资源名的完整归属索引，包含显式挂载的场景 View。
        internal Dictionary<int, View> OwnedViews = new Dictionary<int, View>();
        internal int ViewTransactions;
        // 每次模块注册都换新，防止旧请求在同一模块实例重启后提交结果。
        internal object ViewLifecycle;
        internal readonly HashSet<IPendingViewRequest> PendingViews = new HashSet<IPendingViewRequest>();

        /// <summary>
        /// 存放 所有的业务逻辑
        /// </summary>
        internal Dictionary<Type, Controller> Controllers = new Dictionary<Type, Controller>();

        internal long NextModelId = 1;
        internal HashSet<Type> CreatingModels = new HashSet<Type>();
        internal HashSet<Type> CreatingControllers = new HashSet<Type>();


        private string _projectName;

        #endregion

        #region 属性
        /// <summary>
        /// 模块具体类型的完整名称，用作 ModuleManager 的注册键，与资源项目名分离。
        /// </summary>
        public string ModuleName => GetType().FullName;

        /// <summary>
        /// 由ModuleManager维护的生命周期状态；注册可见不代表已经Running。
        /// </summary>
        public ModuleState State { get; internal set; } = ModuleState.Created;

        /// <summary>
        /// YL Resource ProjectId，与Module类型身份分离；默认类型短名，必须对应显式挂载的新YL项目。
        /// 可在启动前赋值或重写，启动请求在首次加载前捕获该值，不依赖OnStart赋值。
        /// </summary>
        public virtual string ProjectName
        {
            get => _projectName ?? GetType().Name; set => _projectName = value;
        }


        /// <summary>
        /// 是否是基础模块
        /// 基础模块不能普通关闭，由ModuleManager.Shutdown或启动失败回滚结束；默认false
        /// </summary>
        public virtual bool IsBaseModule => false;

        /// <summary>
        /// 必需的启动配表（资产名到Model数组类型）。空数组合法，缺失或无效文本导致启动失败。
        /// 配表及其Bundle须启动过程专用，取文本期间不得由其他调用者并发加载或持有；
        /// Provider复制文本并归还自己的资产租约后，再执行JSON构造与Model初始化。
        /// </summary>

        [Obsolete("已过时!")]
        public virtual Dictionary<string, Type> PreloadConfigTables => null;

        /// <summary>
        /// 默认启动的场景(在启动模块时自动加载的场景名称)
        /// </summary>
        public virtual string DefaultStartUpScene => string.Empty;

        #endregion

        #region 方法

        /// <summary>
        /// 配表和默认场景处理后执行的业务启动钩子；正常返回后才进入 Running。
        /// </summary>
        public abstract void OnStart();

        /// <summary>
        /// 进入Closing后、业务对象清理前触发；无需调用base即可完成框架收尾。
        /// </summary>
        protected virtual void OnClose()
        {
        }

        /// <summary>由统一关闭管线调用业务关闭钩子，不在此清理对象索引。</summary>
        internal void Exit() => OnClose();

        /// <summary>拒绝向已进入关闭流程或已关闭的模块添加业务对象。</summary>
        internal void EnsureCanAddObjects()
        {
            if (State == ModuleState.Closing || State == ModuleState.Closed || State == ModuleState.CloseFailed)
            {
                throw new InvalidOperationException($"Cannot add objects to Module in state {State}: {ModuleName}");
            }
        }

        /// <summary>
        /// 关闭当前注册的自身实例；Starting期间拒绝关闭，不作为启动取消。
        /// </summary>
        public void Close()
        {
            ModuleManager.CloseModule(this);
        }

        #endregion

    }
}
