using System;
using System.Collections;

namespace YLFramework.Utilities.Json {
	/// <summary>
	/// JSONObject 的枚举器：Array 枚举元素，Object 枚举字段值（不含 key）。
	/// 目标节点必须是 Array 或 Object；枚举期间不检测集合修改（与上游语义一致）。
	/// </summary>
	public class JSONObjectEnumerator : IEnumerator {
		/// <summary>被枚举的 JSONObject。</summary>
		public JSONObject target;

		// Enumerators are positioned before the first element until the first MoveNext() call.
		int position = -1;

		/// <summary>为容器节点创建枚举器。</summary>
		/// <exception cref="InvalidOperationException"><paramref name="jsonObject"/> 不是 Array/Object。</exception>
		public JSONObjectEnumerator(JSONObject jsonObject) {
			if (!jsonObject.IsContainer)
				throw new InvalidOperationException("JSONObject must be an array or object to provide an iterator");

			target = jsonObject;
		}

		/// <summary>移动到下一个元素；越过末尾后返回 false。</summary>
		public bool MoveNext() {
			position++;
			return position < target.Count;
		}

		/// <summary>重置到第一个元素之前。</summary>
		public void Reset() {
			position = -1;
		}

		object IEnumerator.Current {
			get { return Current; }
		}

		// ReSharper disable once InconsistentNaming
		/// <summary>当前元素；越界时为 null（由索引器保证）。</summary>
		public JSONObject Current {
			get {
				return target[position];
			}
		}
	}
}
