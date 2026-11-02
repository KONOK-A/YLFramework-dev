using System;
using System.Collections;
using System.Collections.Generic;

namespace YLFramework.Tables
{
    /// <summary>
    /// 轻量数据表基类。
    /// <para>
    /// 基类自身不保存任何数据，只负责把 <see cref="Add"/> / <see cref="Remove"/> / <see cref="Clear"/> /
    /// <see cref="Dispose"/> 转发给子类的抽象钩子，并约定统一的枚举入口；
    /// 实际存储与索引由子类维护，基类不选择存储容器。
    /// </para>
    /// <para>
    /// 数据所有权：基类不取得 <typeparamref name="TDataItem"/> 的生命周期，也不直接释放数据项；
    /// 子类钩子若需要接管或释放数据项，必须另行约定，不能仅凭 Table 的接口判断。
    /// </para>
    /// </summary>
    /// <typeparam name="TDataItem">数据项类型。</typeparam>
    public abstract class Table<TDataItem> : IEnumerable<TDataItem>, IDisposable
    {
        /// <summary>
        /// 添加数据项，转发到 <see cref="OnAdd"/>。
        /// 基类不校验 null、不去重，是否允许重复由子类存储决定。
        /// </summary>
        /// <param name="item">要添加的数据项。</param>
        public void Add(TDataItem item)
        {
            OnAdd(item);
        }

        /// <summary>
        /// 移除数据项，转发到 <see cref="OnRemove"/>。
        /// 不存在的项如何处理由子类决定，基类不做存在性校验。
        /// </summary>
        /// <param name="item">要移除的数据项。</param>
        public void Remove(TDataItem item)
        {
            OnRemove(item);
        }

        /// <summary>
        /// 清空表数据，转发到 <see cref="OnClear"/>。
        /// 基类不改变可用状态；子类应按清空后可复用的约定实现，与 <see cref="Dispose"/> 的释放职责区分。
        /// </summary>
        public void Clear()
        {
            OnClear();
        }

        /// <summary>
        /// 预留的更新入口，当前为空实现，不会遍历数据项或刷新索引。
        /// </summary>
        public void Update()
        {
        }

        /// <summary>
        /// 数据项数量。默认通过枚举统计，复杂度 O(n)；子类可用自有存储覆写为 O(1)。
        /// </summary>
        public virtual int Count
        {
            get
            {
                var count = 0;
                foreach (var _ in this)
                {
                    count++;
                }

                return count;
            }
        }

        /// <summary>
        /// 添加数据项时调用；子类在此写入实际存储并同步维护索引。
        /// 基类不拦截异常，钩子抛出的异常会原样传给 <see cref="Add"/> 调用方；
        /// 基类也不做回滚，异常前钩子已产生的副作用保留，写入顺序由子类负责。
        /// </summary>
        /// <param name="item">要添加的数据项。</param>
        protected abstract void OnAdd(TDataItem item);

        /// <summary>
        /// 移除数据项时调用；子类在此同步移除存储与索引。
        /// 不存在的项如何处理由子类决定；钩子异常原样传播，基类不回滚已有副作用。
        /// </summary>
        /// <param name="item">要移除的数据项。</param>
        protected abstract void OnRemove(TDataItem item);

        /// <summary>
        /// 清空时调用；子类在此清空存储并释放索引资源
        /// （例如 TableIndex.Clear 会把内部列表归还 ListPool）。
        /// 基类不做重入保护，钩子内再次调用 Add/Remove/Clear 由子类自行保证安全；
        /// 钩子异常原样传播，基类不回滚已有副作用。
        /// </summary>
        protected abstract void OnClear();

        /// <summary>
        /// 获取枚举器；枚举内容由子类存储决定，枚举期间修改可能导致异常（取决于子类存储实现）。
        /// </summary>
        public abstract IEnumerator<TDataItem> GetEnumerator();

        /// <summary>将非泛型枚举入口转发到子类提供的泛型枚举器。</summary>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        /// 释放表，转发到 <see cref="OnDispose"/>。
        /// 基类不跟踪释放状态、不做幂等保护：每次调用都会触发 OnDispose，子类钩子应自行保证可重复调用。
        /// 基类不会直接 Dispose 数据项；钩子中的数据项处理责任及释放后能否继续使用由子类决定。
        /// </summary>
        public void Dispose()
        {
            OnDispose();
        }

        /// <summary>
        /// 释放钩子；子类在此释放自身维护的数据结构（如索引）。
        /// 与 <see cref="OnClear"/> 的“清空可复用”不同，这里表达的是“销毁”；
        /// 钩子异常原样传播，基类不回滚也不重试。
        /// </summary>
        protected abstract void OnDispose();
    }
}
