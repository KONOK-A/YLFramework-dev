namespace YLFramework.Pooling
{
    /// <summary>
    /// 对象池通用接口。
    /// </summary>
    /// <typeparam name="T">池化对象类型。</typeparam>
    public interface IPool<T>
    {
        /// <summary>
        /// 从池中分配一个对象；缓存为空时由实现决定新建或返回空。
        /// </summary>
        T Allocate();

        /// <summary>
        /// 回收对象。
        /// </summary>
        /// <param name="obj">待回收对象。</param>
        /// <returns>
        /// 成功进入缓存时返回 true；否则为 false。false 不表示引用仍可使用或应重试，
        /// 清理、所有权和失败处理以具体实现的契约为准。
        /// </returns>
        bool Recycle(T obj);
    }
}
