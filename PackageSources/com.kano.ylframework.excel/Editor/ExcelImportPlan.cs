using System;
using System.Collections.Generic;
using System.Linq;

namespace YLFramework.Integrations.Excel.Editor
{
    /// <summary>一次性 Excel 导入计划的处理结果，不代表所有后端具有相同回滚能力。</summary>
    public enum ExcelCommitResult
    {
        /// <summary>尚未确认或取消，仍可检查预览。</summary>
        Pending,
        /// <summary>校验及应用回调已正常返回。</summary>
        Applied,
        /// <summary>已取消，不再允许提交。</summary>
        Cancelled,
        /// <summary>预检阻止提交，或提交时校验、应用回调抛出异常。</summary>
        Failed
    }
    /// <summary>按行键比较前后快照得到的变更种类。</summary>
    public enum ExcelChangeKind
    {
        /// <summary>原快照中不存在该键。</summary>
        Add,
        /// <summary>同键快照内容发生变化。</summary>
        Update,
        /// <summary>导入快照中不再包含该键。</summary>
        Delete
    }

    /// <summary>一项预览差异，保留行键及变更前后的快照文本。</summary>
    public sealed class ExcelChange
    {
        /// <summary>此项差异的新增、更新或删除种类。</summary>
        public ExcelChangeKind Kind
        {
            get;
        }
        /// <summary>比较快照使用的键，由具体后端定义为 ID、行位置或输出路径。</summary>
        public string Key
        {
            get;
        }
        /// <summary>变更前的快照；新增项为 null。</summary>
        public string Before
        {
            get;
        }
        /// <summary>变更后的快照；删除项为 null。</summary>
        public string After
        {
            get;
        }
        internal ExcelChange(ExcelChangeKind kind, string key, string before, string after)
        {
            Kind = kind;
            Key = key;
            Before = before;
            After = after;
        }
    }

    /// <summary>预检不改目标；一次性计划必须明确确认，取消或关闭后不可再提交。</summary>
    public sealed class ExcelImportPlan : IDisposable
    {
        private readonly List<string> _errors = new List<string>();
        private readonly List<ExcelChange> _changes = new List<ExcelChange>();
        private Action _apply;
        private Action _release;
        private Action _validation;
        /// <summary>预览及提交过程中已收集的错误，只读视图会反映后续记录。</summary>
        public IReadOnlyList<string> Errors => _errors.AsReadOnly();
        /// <summary>预览生成的差异；无差异不表示没有待执行的应用回调。</summary>
        public IReadOnlyList<ExcelChange> Changes => _changes.AsReadOnly();
        /// <summary>计划当前的待处理状态或不可再次提交的终态。</summary>
        public ExcelCommitResult Result
        {
            get; private set;
        }
        /// <summary>计划待处理、无已记录错误且有应用回调时为 true，不预先执行提交校验。</summary>
        public bool CanCommit => Result == ExcelCommitResult.Pending && _errors.Count == 0 && _apply != null;

        // 记录一条会阻止提交的预检错误。
        internal void Error(string error) => _errors.Add(error);
        // 安装应用与暂存资源释放回调，由计划在处理终态时调用。
        internal void Prepare(Action commit, Action cleanup = null)
        {
            _apply = commit;
            _release = cleanup;
        }
        /// <summary>只读检查领域外部约束，在预览及真正写入前各执行一次；失败不提交。</summary>
        /// <param name="check">非空只读校验；添加时立即执行，成功后登记到提交前再次执行。</param>
        /// <remarks>添加时的异常直接抛出；提交时的异常由计划记录并标记失败。</remarks>
        public void AddValidation(Action check)
        {
            if (check == null)
            {
                throw new ArgumentNullException(nameof(check));
            }

            if (Result != ExcelCommitResult.Pending)
            {
                throw new InvalidOperationException("The import plan is already resolved.");
            }

            check();
            _validation += check;
        }
        // 按键比较两份快照，追加新增、更新及删除项。
        internal void Diff(IDictionary<string, string> before, IDictionary<string, string> after)
        {
            foreach (var item in after)
            {
                if (!before.TryGetValue(item.Key, out var previous))
                {
                    _changes.Add(new ExcelChange(ExcelChangeKind.Add, item.Key, null, item.Value));
                }
                else if (previous != item.Value)
                {
                    _changes.Add(new ExcelChange(ExcelChangeKind.Update, item.Key, previous, item.Value));
                }
            }
            foreach (var item in before.Where(item => !after.ContainsKey(item.Key)))
            {
                _changes.Add(new ExcelChange(ExcelChangeKind.Delete, item.Key, item.Value, null));
            }
        }

        /// <summary>确认后校验并应用计划，或取消计划；已处理的计划只返回原结果。</summary>
        /// <param name="confirmed">明确同意应用时为 true；false 会永久取消待处理计划。</param>
        /// <returns>本次处理后或此前已确定的结果。</returns>
        /// <remarks>
        /// 校验和应用异常会记入错误；回滚由具体应用回调负责，此类型不提供通用事务。
        /// 暂存清理在结果确定后执行，其异常直接向调用方传播。
        /// </remarks>
        public ExcelCommitResult Commit(bool confirmed)
        {
            if (Result != ExcelCommitResult.Pending)
            {
                return Result;
            }

            if (!confirmed)
            {
                Result = ExcelCommitResult.Cancelled;
            }
            else if (!CanCommit)
            {
                Result = ExcelCommitResult.Failed;
            }
            else
            {
                try
                {
                    _validation?.Invoke();
                    _apply();
                    Result = ExcelCommitResult.Applied;
                }
                catch (Exception e)
                {
                    _errors.Add(e.ToString());
                    Result = ExcelCommitResult.Failed;
                }
            }
            _apply = null;
            _validation = null;
            _release?.Invoke();
            _release = null;
            return Result;
        }
        /// <summary>将仍待处理的计划取消并释放其暂存资源，不撤销已应用的结果。</summary>
        public void Dispose()
        {
            if (Result == ExcelCommitResult.Pending)
            {
                Commit(false);
            }
        }
    }
}
