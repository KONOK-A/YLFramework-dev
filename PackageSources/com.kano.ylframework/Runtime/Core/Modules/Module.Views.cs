using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Foundation;

namespace YLFramework.Core
{
    public abstract partial class Module
    {
        #region 移除View

        /// <summary>
        /// 结束本模块当前快照中的全部 View 与 Panel 会话，不移除其借用的 Model；清理错误汇总报告。
        /// </summary>
        public void ClearView()
        {
            ViewManager.ClearView(this);
        }

        /// <summary>
        /// 清空本模块指定类型及派生类型的View。
        /// </summary>
        public void ClearView<T>() where T : View
        {
            ViewManager.ClearView<T>(this);
        }

        /// <summary>
        /// 结束本模块指定资源名的 View 与 Panel 会话，不影响其他模块或其他名称的会话。
        /// </summary>
        public void ClearView(string assetName)
        {
            ViewManager.ClearView(this, assetName);
        }

        #endregion

        #region 加载View

        /// <summary>纳管已有View，不实例化、不激活、不取得GameObject所有权。合法重复挂载不重复初始化；参数由本次调用接管。</summary>
        public T AttachView<T>(T view, LoadViewArgs loadViewArgs = null) where T : View
        {
            ViewManager.AttachView(this, view, loadViewArgs);
            return view;
        }

        /// <summary>纳管已有层级的所有View（包含inactive）。失败只撤销本次新增会话，不改变已有会话和对象激活状态。</summary>
        public List<View> AttachViews(GameObject root)
        {
            return ViewManager.AttachViews(this, root);
        }

        /// <summary>明确纳管指定已加载Scene中的View（包含inactive），不自动卸载场景；使用Scene身份而非名称。</summary>
        public List<View> AttachViews(UnityEngine.SceneManagement.Scene scene)
        {
            return ViewManager.AttachViews(this, scene);
        }

        /// <summary>
        /// 按资源名同步取得并初始化 View，先查本模块资源项目，再查有效基础模块项目。
        /// </summary>
        /// <param name="assetName">View预制体名称</param>
        /// <param name="parent">父节点</param>
        /// <param name="param">传给旧版 OnLoaded 回调的参数数组。</param>
        /// <returns>完成初始化的根 View。</returns>
        [Obsolete("已过时,请使用重载函数:LoadView(string assetName, Transform parent,LoadViewArgs loadViewArgs)代替!")]
        public View LoadView(string assetName, Transform parent = null, params object[] param)
        {
            return ViewManager.LoadView(this, assetName, parent, param);
        }

        /// <summary>
        /// 按资源名同步取得 View，并要求根组件可赋给 T；资源查找不改变本模块归属。
        /// </summary>
        /// <typeparam name="T">类型</typeparam>
        /// <param name="assetName">资源名称</param>
        /// <param name="parent">父节点</param>
        /// <param name="param">传给旧版 OnLoaded 回调的参数数组。</param>
        /// <returns>完成初始化且类型匹配的根 View。</returns>
        [Obsolete("已过时,请使用重载函数:LoadView<T>(string assetName, Transform parent,LoadViewArgs loadViewArgs)代替!")]
        public T LoadView<T>(string assetName, Transform parent = null, params object[] param) where T : View
        {
            return ViewManager.LoadViewLegacy<T>(this, assetName, parent, param);
        }

        /// <summary>
        /// 从直接传入的 Prefab 同步取得并初始化 View，不依赖资源 provider。
        /// </summary>
        /// <param name="prefab">View预制体</param>
        /// <param name="parent">父节点</param>
        /// <param name="param">传给旧版 OnLoaded 回调的参数数组。</param>
        /// <returns>完成初始化的根 View。</returns>
        [Obsolete("已过时,请使用重载函数:LoadView(GameObject prefab, Transform parent = null,LoadViewArgs loadViewArgs)代替!")]
        public View LoadView(GameObject prefab, Transform parent = null, params object[] param)
        {
            return ViewManager.LoadView(this, prefab, parent, param);
        }

        /// <summary>
        /// 从直接传入的 Prefab 同步取得 View，并要求根组件可赋给 T，不查找资源项目。
        /// </summary>
        /// <typeparam name="T">要求根 View 可赋给的组件类型。</typeparam>
        /// <param name="prefab">View预制体</param>
        /// <param name="parent">父节点</param>
        /// <param name="param">传给旧版 OnLoaded 回调的参数数组。</param>
        /// <returns>完成初始化且类型匹配的根 View。</returns>
        [Obsolete("已过时,请使用重载函数:LoadView<T>(GameObject prefab, Transform parent,LoadViewArgs loadViewArgs)代替!")]
        public T LoadView<T>(GameObject prefab, Transform parent = null, params object[] param) where T : View
        {
            return ViewManager.LoadViewLegacy<T>(this, prefab, parent, param);
        }

        /// <summary>
        /// 按脚本类型名加载 View；脚本名称必须与预制体名称一致。
        /// 从当前模块和基础模块中加载
        /// </summary>
        /// <typeparam name="T">类型</typeparam>
        /// <param name="parent">父节点</param>
        /// <param name="param">传给旧版 OnLoaded 回调的参数数组。</param>
        /// <returns>完成初始化且类型匹配的根 View。</returns>
        [Obsolete("已过时,请使用重载函数:LoadView<T>(Transform parent,LoadViewArgs loadViewArgs)代替!")]
        public T LoadView<T>(Transform parent = null, params object[] param) where T : View
        {
            return LoadView<T>(typeof(T).Name, parent, param);
        }


        /// <summary>
        /// 创建独立的命名 View 请求，从下一帧开始按本模块、基础模块的项目顺序查找资源。
        /// </summary>
        /// <param name="assetName">资源名称</param>
        /// <param name="parent">父节点</param>
        /// <param name="param">传给旧版 OnLoaded 回调的参数数组。</param>
        /// <returns>独立请求，交付结果及取消状态通过该请求观察。</returns>
        [Obsolete("已过时,请使用重载函数:LoadViewAsync(string assetName, Transform parent,LoadViewArgs loadViewArgs)代替!")]
        public LoadViewRequest<View> LoadViewAsync(string assetName, Transform parent = null, params object[] param)
        {
            return ViewManager.LoadViewAsync<View>(this, assetName, parent, true, param);
        }

        /// <summary>
        /// 创建要求根组件可赋给 T 的命名 View 请求；资源来源不改变本模块的会话归属。
        /// </summary>
        /// <typeparam name="T">View类型</typeparam>
        /// <param name="assetName">资源名称</param>
        /// <param name="parent">父节点</param>
        /// <param name="param">传给旧版 OnLoaded 回调的参数数组。</param>
        /// <returns>独立请求，交付结果及取消状态通过该请求观察。</returns>
        [Obsolete("已过时,请使用重载函数:LoadViewAsync<T>(string assetName, Transform parent,LoadViewArgs loadViewArgs)代替!")]
        public LoadViewRequest<T> LoadViewAsync<T>(string assetName, Transform parent = null, params object[] param) where T : View
        {
            return ViewManager.LoadViewAsync<T>(this, assetName, parent, true, param);
        }

        /// <summary>
        /// 按脚本类型名异步加载 View；脚本名称必须与预制体名称一致。
        /// 从当前模块和基础模块中加载
        /// </summary>
        /// <typeparam name="T">View类型</typeparam>
        /// <param name="parent">父节点</param>
        /// <param name="param">传给旧版 OnLoaded 回调的参数数组。</param>
        /// <returns>使用类型短名作为资源地址的独立请求。</returns>
        [Obsolete("已过时,请使用重载函数:LoadViewAsync<T>(Transform parent,LoadViewArgs loadViewArgs)代替!")]
        public LoadViewRequest<T> LoadViewAsync<T>(Transform parent = null, params object[] param) where T : View
        {
            return ViewManager.LoadViewAsync<T>(this, typeof(T).Name, parent, true, param);
        }


        #endregion

        #region 加载View(With LoadViewArgs)

        /// <summary>
        /// 按资源名同步取得并初始化 View，先查本模块资源项目，再查有效基础模块项目。
        /// </summary>
        /// <param name="assetName">View预制体名称</param>
        /// <param name="parent">父节点</param>
        /// <param name="loadViewArgs">用 LoadViewArgs.New 创建的参数；成功接管后由本调用归还，包括入口拒绝路径。</param>
        /// <returns>完成初始化的根 View。</returns>
        public View LoadView(string assetName, Transform parent = null, LoadViewArgs loadViewArgs = null)
        {
            return ViewManager.LoadView<View>(this, assetName, parent, loadViewArgs);
        }

        /// <summary>
        /// 按资源名同步取得 View，并要求根组件可赋给 T；资源查找不改变本模块归属。
        /// </summary>
        /// <typeparam name="T">类型</typeparam>
        /// <param name="assetName">资源名称</param>
        /// <param name="parent">父节点</param>
        /// <param name="loadViewArgs">参数(使用LoadViewArgs.New() 来创建参数对象)</param>
        /// <returns>完成初始化且类型匹配的根 View。</returns>
        public T LoadView<T>(string assetName, Transform parent = null, LoadViewArgs loadViewArgs = null) where T : View
        {
            return ViewManager.LoadView<T>(this, assetName, parent, loadViewArgs);
        }

        /// <summary>
        /// 从直接传入的 Prefab 同步取得并初始化 View，不依赖资源 provider。
        /// </summary>
        /// <param name="prefab">View预制体</param>
        /// <param name="parent">父节点</param>
        /// <param name="loadViewArgs">参数(使用LoadViewArgs.New() 来创建参数对象)</param>
        /// <returns>完成初始化的根 View。</returns>
        public View LoadView(GameObject prefab, Transform parent = null, LoadViewArgs loadViewArgs = null)
        {
            return ViewManager.LoadView<View>(this, prefab, parent, loadViewArgs);
        }

        /// <summary>
        /// 从直接传入的 Prefab 同步取得 View，并要求根组件可赋给 T，不查找资源项目。
        /// </summary>
        /// <typeparam name="T">要求根 View 可赋给的组件类型。</typeparam>
        /// <param name="prefab">View预制体</param>
        /// <param name="parent">父节点</param>
        /// <param name="loadViewArgs">参数(使用LoadViewArgs.New() 来创建参数对象)</param>
        /// <returns>完成初始化且类型匹配的根 View。</returns>
        public T LoadView<T>(GameObject prefab, Transform parent = null, LoadViewArgs loadViewArgs = null) where T : View
        {
            return ViewManager.LoadView<T>(this, prefab, parent, loadViewArgs);
        }

        /// <summary>
        /// 按脚本类型名加载 View；脚本名称必须与预制体名称一致。
        /// 从当前模块和基础模块中加载
        /// </summary>
        /// <typeparam name="T">类型</typeparam>
        /// <param name="parent">父节点</param>
        /// <param name="loadViewArgs">参数(使用LoadViewArgs.New() 来创建参数对象)</param>
        /// <returns>完成初始化且类型匹配的根 View。</returns>
        public T LoadView<T>(Transform parent = null, LoadViewArgs loadViewArgs = null) where T : View
        {
            return LoadView<T>(typeof(T).Name, parent, loadViewArgs);
        }


        /// <summary>
        /// 创建独立的命名 View 请求，从下一帧开始按本模块、基础模块的项目顺序查找资源。
        /// </summary>
        /// <param name="assetName">资源名称</param>
        /// <param name="parent">父节点</param>
        /// <param name="loadViewArgs">用 LoadViewArgs.New 创建的参数；成功接管后由请求负责归还，回调只可借读。</param>
        /// <returns>独立请求，交付结果及取消状态通过该请求观察。</returns>
        public LoadViewRequest<View> LoadViewAsync(string assetName, Transform parent = null, LoadViewArgs loadViewArgs = null)
        {
            return ViewManager.LoadViewAsync<View>(this, assetName, parent, true, loadViewArgs);
        }

        /// <summary>
        /// 创建要求根组件可赋给 T 的命名 View 请求；资源来源不改变本模块的会话归属。
        /// </summary>
        /// <typeparam name="T">View类型</typeparam>
        /// <param name="assetName">资源名称</param>
        /// <param name="parent">父节点</param>
        /// <param name="loadViewArgs">参数(使用LoadViewArgs.New() 来创建参数对象)</param>
        /// <returns>独立请求，交付结果及取消状态通过该请求观察。</returns>
        public LoadViewRequest<T> LoadViewAsync<T>(string assetName, Transform parent = null, LoadViewArgs loadViewArgs = null) where T : View
        {
            return ViewManager.LoadViewAsync<T>(this, assetName, parent, true, loadViewArgs);
        }

        /// <summary>
        /// 按脚本类型名异步加载 View；脚本名称必须与预制体名称一致。
        /// 从当前模块和基础模块中加载
        /// </summary>
        /// <typeparam name="T">View类型</typeparam>
        /// <param name="parent">父节点</param>
        /// <param name="loadViewArgs">参数(使用LoadViewArgs.New() 来创建参数对象)</param>
        /// <returns>使用类型短名作为资源地址的独立请求。</returns>
        public LoadViewRequest<T> LoadViewAsync<T>(Transform parent = null, LoadViewArgs loadViewArgs = null) where T : View
        {
            return ViewManager.LoadViewAsync<T>(this, typeof(T).Name, parent, true, loadViewArgs);
        }

        #endregion
        #region 查询View

        /// <summary>
        /// 仅查询本模块View或Panel；跨模块访问须显式调用其所属模块。
        /// </summary>
        /// <param name="assetName">资源名</param>
        /// <param name="id">View ID</param>
        /// <returns>名称和 ID 均匹配的存活实例；不存在时为 null。</returns>
        public View GetView(string assetName, int id)
        {
            return ViewManager.GetView(this, assetName, id);
        }

        /// <summary>
        /// 仅查询本模块View或Panel；结果类型不匹配时抛出异常。
        /// </summary>
        /// <typeparam name="T">View类型</typeparam>
        /// <param name="assetName">资源名称</param>
        /// <param name="id">View ID</param>
        /// <returns>名称、ID 和类型均匹配的存活实例；不存在时为 null。</returns>
        public T GetView<T>(string assetName, int id) where T : View
        {
            return ViewManager.GetView<T>(this, assetName, id);
        }

        /// <summary>
        /// 按实例ID查询本模块View或Panel，不要求脚本名与资源名一致。
        /// </summary>
        /// <typeparam name="T">要求实例可赋给的组件类型。</typeparam>
        /// <param name="id">View 的 Unity 实例 ID。</param>
        /// <returns>ID 和类型均匹配的存活实例；不存在时为 null。</returns>
        public T GetView<T>(int id) where T : View
        {
            return ViewManager.GetView<T>(this, id);
        }

        /// <summary>
        /// 按资源名查询本模块唯一View或Panel；多个实例时须指定ID。
        /// </summary>
        /// <param name="assetName">资源名</param> 
        /// <returns>唯一匹配实例；不存在时为 null。</returns>
        public View GetView(string assetName)
        {
            return ViewManager.GetView(this, assetName);
        }

        /// <summary>
        /// 按资源名查询本模块唯一View或Panel；结果类型不匹配时抛出异常。
        /// </summary>
        /// <typeparam name="T">View类型</typeparam>
        /// <param name="assetName">资源名称</param> 
        /// <returns>唯一且类型匹配的实例；不存在时为 null。</returns>
        public T GetView<T>(string assetName) where T : View
        {
            return ViewManager.GetView<T>(this, assetName);
        }

        /// <summary>
        /// 按精确组件类型查询本模块唯一View或Panel，不包含派生类型；多个实例时须指定ID。
        /// </summary>
        /// <typeparam name="T">要精确匹配的组件类型。</typeparam>
        /// <returns>该具体类型的唯一实例；不存在时为 null。</returns>
        public T GetView<T>() where T : View
        {
            return ViewManager.GetView<T>(this);
        }

        /// <summary>
        /// 查询本模块某个资源名的所有View或Panel，返回独立结果集合。
        /// </summary>
        /// <param name="assetName">预制体资源名称</param>
        /// <returns>匹配实例的独立列表；缺失时为空。</returns>
        public List<View> GetViews(string assetName)
        {
            return ViewManager.GetViews(this, assetName);
        }

        /// <summary>
        /// 查询本模块某个资源名的所有可赋给T的View或Panel。
        /// </summary>
        /// <typeparam name="T">筛选实例的基类或具体组件类型。</typeparam>
        /// <param name="assetName">登记的预制体资源名。</param>
        /// <returns>名称及类型匹配的独立列表；缺失时为空。</returns>
        public List<T> GetViews<T>(string assetName) where T : View
        {
            return ViewManager.GetViews<T>(this, assetName);
        }

        /// <summary>
        /// 查询本模块指定类型及派生类型的所有View或Panel。
        /// </summary>
        /// <typeparam name="T">筛选实例的基类或具体组件类型。</typeparam>
        /// <returns>类型匹配的独立列表；缺失时为空。</returns>
        public List<T> GetViews<T>() where T : View
        {
            return ViewManager.GetViews<T>(this);
        }


        /// <summary>复用调用方列表查询本模块指定类型及派生类型的 View 或 Panel，不保留列表原内容。</summary>
        /// <typeparam name="T">筛选实例的基类或具体组件类型。</typeparam>
        /// <param name="results">先清空再写入结果的列表，不能为 null；此入口不保证内部清理过程零分配。</param>
        public void GetViewsNonAlloc<T>(List<T> results) where T : View
        {
            ViewManager.GetViewsNonAlloc(this, results);
        }

        #endregion
    }
}
