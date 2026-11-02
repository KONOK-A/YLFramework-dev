namespace YLFramework.DependencyInjection
{
    /// <summary>
    /// 解析路径中同一类型被重复构造（自引用或 A→B→A）时抛出，避免无限递归导致栈溢出。
    /// </summary>
    public class IOCCircularDependencyException : IOCResolutionException
    {
        /// <summary>
        /// 循环链，例如 "A -> B -> A"。
        /// </summary>
        public string CyclePath
        {
            get;
        }

        /// <summary>
        /// 使用循环链创建异常，消息中包含完整依赖链。
        /// </summary>
        /// <param name="cyclePath">循环链字符串，例如 "A -> B -> A"。</param>
        public IOCCircularDependencyException(string cyclePath)
            : base("Circular dependency detected: " + cyclePath + ".", cyclePath)
        {
            CyclePath = cyclePath;
        }
    }
}
