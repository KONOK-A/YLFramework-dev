//#define JSONOBJECT_DISABLE_PRETTY_PRINT // Use when you no longer need to read JSON to disable pretty Print system-wide
//#define JSONOBJECT_USE_FLOAT //Use floats for numbers instead of doubles (enable if you don't need support for doubles and want to cut down on significant digits in output)
//#define JSONOBJECT_POOLING //Create JSONObjects from a pool and prevent finalization by returning objects to the pool

// ReSharper disable ArrangeAccessorOwnerBody
// ReSharper disable MergeConditionalExpression
// ReSharper disable UseStringInterpolation

#if UNITY_2 || UNITY_3 || UNITY_4 || UNITY_5 || UNITY_5_3_OR_NEWER
#define USING_UNITY
#endif

using System;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Globalization;

#if USING_UNITY
using UnityEngine;
using Debug = UnityEngine.Debug;
#endif

namespace YLFramework.Utilities.Json {
	/// <summary>
	/// 轻量 JSON 节点：可解析、构建、访问、修改并输出 JSON。
	/// 同一节点在任一时刻只表现为一种 <see cref="Type"/>；List/Keys 仅在容器类型下有效。
	/// </summary>
	public class JSONObject : IEnumerable {
#if JSONOBJECT_POOLING
		const int MaxPoolSize = 100000;
		static readonly Queue<JSONObject> JSONObjectPool = new Queue<JSONObject>();
		static readonly Queue<List<JSONObject>> JSONObjectListPool = new Queue<List<JSONObject>>();
		static readonly Queue<List<string>> StringListPool = new Queue<List<string>>();
		static bool poolingEnabled = true;
#endif

#if !JSONOBJECT_DISABLE_PRETTY_PRINT
		const string Newline = "\r\n";
		const string Tab = "\t";
#endif

		const string Infinity = "Infinity";
		const string NegativeInfinity = "-Infinity";
		const string NaN = "NaN";
		const string True = "true";
		const string False = "false";
		const string Null = "null";

		const float MaxFrameTime = 0.008f;
		static readonly Stopwatch PrintWatch = new Stopwatch();
		/// <summary>解析器认可的空白字符集合（'\t' 与 '\u0009' 为同一字符的重复项，保留自上游）。</summary>
		public static readonly char[] Whitespace = { ' ', '\r', '\n', '\t', '\uFEFF', '\u0009' };

		/// <summary>JSON 节点类型。</summary>
		public enum Type {
			/// <summary>JSON null；空输入或解析失败时根节点也为 Null。</summary>
			Null,
			/// <summary>字符串。</summary>
			String,
			/// <summary>数字（整数与浮点统一为该类型，整数由 <see cref="IsInteger"/> 区分）。</summary>
			Number,
			/// <summary>对象（有序键值集合，键存于 <see cref="Keys"/>）。</summary>
			Object,
			/// <summary>数组。</summary>
			Array,
			/// <summary>布尔值。</summary>
			Bool,
			/// <summary>Baked 原始片段：保存已序列化字符串并原样输出，parse/Print 不会重构其内部结构。</summary>
			Baked
		}

		/// <summary>
		/// 分步解析的单次迭代结果：当前容器、下一个待消费偏移量，以及本轮是否因超过分帧预算而暂停。
		/// </summary>
		public struct ParseResult {
			/// <summary>当前解析出的 JSONObject。</summary>
			public readonly JSONObject Result;
			/// <summary>输入字符串中下一个待处理位置。</summary>
			public readonly int Offset;
			/// <summary>true 表示本轮为分帧暂停，稍后继续枚举即可；false 表示解析结束。</summary>
			public readonly bool Pause;

			public ParseResult(JSONObject result, int offset, bool pause) {
				this.Result = result;
				this.Offset = offset;
				this.Pause = pause;
			}
		}

		/// <summary>GetField 系列方法在字段不存在时的回调。</summary>
		public delegate void FieldNotFound(string name);
		/// <summary>GetField 委托式取值的成功回调。</summary>
		public delegate void GetFieldResponse(JSONObject jsonObject);

		private Type mType = Type.Null;
		/// <summary>当前节点类型。</summary>
		public Type ObjectType => mType;

		/// <summary>容器子节点列表：数组元素或对象字段值；非容器通常为 null。</summary>
		public List<JSONObject> List;
		/// <summary>对象字段名列表，与 <see cref="List"/> 按索引一一对应；数组通常为 null。</summary>
		public List<string> Keys;
		/// <summary>String 类型的文本值，或 Baked 类型的原始序列化片段。</summary>
		public string StringValue;
		/// <summary>Number 类型是否为整数；为 true 时输出使用 <see cref="LongValue"/>。</summary>
		public bool IsInteger;
		/// <summary>Number 类型的整数值。</summary>
		public long LongValue;
		/// <summary>Bool 类型的值。</summary>
		public bool BoolValue;
#if JSONOBJECT_USE_FLOAT
		public float floatValue;
#else
		/// <summary>Number 类型的浮点值。</summary>
		public double DoubleValue;
#endif

		bool mIsPooled;

		/// <summary>容器子节点数量；非容器或未初始化时为 0。</summary>
		public int Count {
			get {
				return List == null ? 0 : List.Count;
			}
		}


		/// <summary>是否为 Object 或 Array。</summary>
		public bool IsContainer {
			get { return mType == Type.Array || mType == Type.Object; }
		}

		/// <summary>以 int 读写整数值（从 <see cref="LongValue"/> 截断转换）。</summary>
		public int IntValue {
			get {
				return (int) LongValue;
			}
			set {
				LongValue = value;
			}
		}

#if JSONOBJECT_USE_FLOAT
		public double doubleValue {
			get {
				return floatValue;
			}
			set {
				floatValue = (float) value;
			}
		}
#else
		/// <summary>以 float 读写浮点值（基于 <see cref="DoubleValue"/> 转换）。</summary>
		public float FloatValue {
			get {
				return (float) DoubleValue;
			}
			set {
				DoubleValue = value;
			}
		}
#endif

		/// <summary>用于在构建时向内填充内容的委托。</summary>
		public delegate void AddJSONContents(JSONObject self);

		/// <summary>新建一个 Null 节点。</summary>
		public static JSONObject NullObject {
			get { return Create(Type.Null); }
		}

		/// <summary>新建一个空对象节点。</summary>
		public static JSONObject EmptyObject {
			get { return Create(Type.Object); }
		}

		/// <summary>新建一个空数组节点。</summary>
		public static JSONObject EmptyArray {
			get { return Create(Type.Array); }
		}

		/// <summary>按指定类型创建空节点。</summary>
		public JSONObject(Type type) { this.mType = type; }

		/// <summary>创建 Bool 节点。</summary>
		public JSONObject(bool value) {
			mType = Type.Bool;
			BoolValue = value;
		}

		/// <summary>创建 Number 节点（浮点值）。</summary>
		public JSONObject(float value) {
			mType = Type.Number;
#if JSONOBJECT_USE_FLOAT
			floatValue = value;
#else
			DoubleValue = value;
#endif
		}

		/// <summary>创建 Number 节点（浮点值）。</summary>
		public JSONObject(double value) {
			mType = Type.Number;
#if JSONOBJECT_USE_FLOAT
			floatValue = (float)value;
#else
			DoubleValue = value;
#endif
		}

		/// <summary>创建 Number 节点（整数值，<see cref="IsInteger"/> 为 true）。</summary>
		public JSONObject(int value) {
			mType = Type.Number;
			LongValue = value;
			IsInteger = true;
#if JSONOBJECT_USE_FLOAT
			floatValue = value;
#else
			DoubleValue = value;
#endif
		}

		/// <summary>创建 Number 节点（整数值，<see cref="IsInteger"/> 为 true）。</summary>
		public JSONObject(long value) {
			mType = Type.Number;
			LongValue = value;
			IsInteger = true;
#if JSONOBJECT_USE_FLOAT
			floatValue = value;
#else
			DoubleValue = value;
#endif
		}

		/// <summary>创建 Object 节点，每个值转为 String 节点。</summary>
		public JSONObject(Dictionary<string, string> dictionary) {
			mType = Type.Object;
			Keys = CreateStringList();
			List = CreateJSONObjectList();
			foreach (KeyValuePair<string, string> kvp in dictionary) {
				Keys.Add(kvp.Key);
				List.Add(CreateStringObject(kvp.Value));
			}
		}

		/// <summary>创建 Object 节点，值节点直接引用字典中的实例（不复制）。</summary>
		public JSONObject(Dictionary<string, JSONObject> dictionary) {
			mType = Type.Object;
			Keys = CreateStringList();
			List = CreateJSONObjectList();
			foreach (KeyValuePair<string, JSONObject> kvp in dictionary) {
				Keys.Add(kvp.Key);
				List.Add(kvp.Value);
			}
		}

		/// <summary>
		/// 创建节点并立即调用 <paramref name="content"/> 填充内容（节点类型由填充过程决定）。
		/// </summary>
		/// <exception cref="ArgumentNullException"><paramref name="content"/> 为 null。</exception>
		public JSONObject(AddJSONContents content) {
			if (content == null)
				throw new ArgumentNullException(nameof(content));

			content.Invoke(this);
		}

		/// <summary>创建 Array 节点，元素直接引用传入数组中的实例。</summary>
		public JSONObject(JSONObject[] objects) {
			mType = Type.Array;
			List = CreateJSONObjectList();
			List.AddRange(objects);
		}

		/// <summary>创建 Array 节点，直接使用传入的列表作为内部容器（不复制）。</summary>
		public JSONObject(List<JSONObject> objects) {
			mType = Type.Array;
			List = objects;
		}

		/// <summary>
		/// 创建 String 节点（等价于 <see cref="CreateStringObject"/>）。
		/// 之所以不作为构造函数，是为了让格式错误的 JSON 数据不会被意外转换成字符串节点，必须显式调用本方法。
		/// </summary>
		/// <param name="value">字符串值；null 输出为空字符串。</param>
		/// <returns>新的 String 节点。</returns>
		public static JSONObject StringObject(string value) {
			return CreateStringObject(value);
		}

		/// <summary>
		/// 将 <paramref name="other"/> 的内容追加到当前节点：List/Keys 采用追加式合并（子节点为引用共享），
		/// 标量字段与类型直接复制。<paramref name="other"/> 为 null 时不做任何事。
		/// </summary>
		public void Absorb(JSONObject other) {
			if (other == null)
				return;

			var otherList = other.List;
			if (otherList != null) {
				if (List == null)
					List = CreateJSONObjectList();

				List.AddRange(otherList);
			}

			var otherKeys = other.Keys;
			if (otherKeys != null) {
				if (Keys == null)
					Keys = CreateStringList();

				Keys.AddRange(otherKeys);
			}

			StringValue = other.StringValue;
#if JSONOBJECT_USE_FLOAT
			floatValue = other.floatValue;
#else
			DoubleValue = other.DoubleValue;
#endif

			IsInteger = other.IsInteger;
			LongValue = other.LongValue;
			BoolValue = other.BoolValue;
			mType = other.mType;
		}

		/// <summary>创建 Null 节点。</summary>
		public static JSONObject Create() {
#if JSONOBJECT_POOLING
			lock (JSONObjectPool) {
				if (JSONObjectPool.Count > 0) {
					var result = JSONObjectPool.Dequeue();

					result.isPooled = false;
					return result;
				}
			}
#endif

			return new JSONObject();
		}

		/// <summary>按指定类型创建空节点。</summary>
		public static JSONObject Create(Type type) {
			var jsonObject = Create();
			jsonObject.mType = type;
			return jsonObject;
		}

		/// <summary>创建 Bool 节点。</summary>
		public static JSONObject Create(bool value) {
			var jsonObject = Create();
			jsonObject.mType = Type.Bool;
			jsonObject.BoolValue = value;
			return jsonObject;
		}

		/// <summary>创建 Number 节点（浮点值）。</summary>
		public static JSONObject Create(float value) {
			var jsonObject = Create();
			jsonObject.mType = Type.Number;
#if JSONOBJECT_USE_FLOAT
			jsonObject.floatValue = value;
#else
			jsonObject.DoubleValue = value;
#endif

			return jsonObject;
		}

		/// <summary>创建 Number 节点（浮点值）。</summary>
		public static JSONObject Create(double value) {
			var jsonObject = Create();
			jsonObject.mType = Type.Number;
#if JSONOBJECT_USE_FLOAT
			jsonObject.floatValue = (float)value;
#else
			jsonObject.DoubleValue = value;
#endif

			return jsonObject;
		}

		/// <summary>创建 Number 节点（整数值，<see cref="IsInteger"/> 为 true）。</summary>
		public static JSONObject Create(int value) {
			var jsonObject = Create();
			jsonObject.mType = Type.Number;
			jsonObject.IsInteger = true;
			jsonObject.LongValue = value;
#if JSONOBJECT_USE_FLOAT
			jsonObject.floatValue = value;
#else
			jsonObject.DoubleValue = value;
#endif

			return jsonObject;
		}

		/// <summary>创建 Number 节点（整数值，<see cref="IsInteger"/> 为 true）。</summary>
		public static JSONObject Create(long value) {
			var jsonObject = Create();
			jsonObject.mType = Type.Number;
			jsonObject.IsInteger = true;
			jsonObject.LongValue = value;
#if JSONOBJECT_USE_FLOAT
			jsonObject.floatValue = value;
#else
			jsonObject.DoubleValue = value;
#endif

			return jsonObject;
		}

		/// <summary>创建 String 节点；<paramref name="value"/> 为 null 时输出为空字符串。</summary>
		public static JSONObject CreateStringObject(string value) {
			var jsonObject = Create();
			jsonObject.mType = Type.String;
			jsonObject.StringValue = value;
			return jsonObject;
		}

		/// <summary>创建 Baked 节点：<paramref name="value"/> 被视为已序列化片段，输出时原样写出。</summary>
		public static JSONObject CreateBakedObject(string value) {
			var bakedObject = Create();
			bakedObject.mType = Type.Baked;
			bakedObject.StringValue = value;
			return bakedObject;
		}

		/// <summary>
		/// 解析 JSON 字符串并创建 JSONObject（启用池化时使用池）。
		/// </summary>
		/// <param name="jsonString">待解析的 JSON 字符串；null 或空串返回 Null 节点。</param>
		/// <param name="offset">解析起点（字符串索引）。</param>
		/// <param name="endOffset">解析终点（含）；-1 表示直到字符串末尾。</param>
		/// <param name="maxDepth">最大展开深度；0 表示完全不解析，负数表示不限制。</param>
		/// <param name="storeExcessLevels">超出 maxDepth 的层级是否按 Baked 保留原始片段；false 时该部分存为 Null。</param>
		/// <returns>解析结果；数据不完整时可能是 Null、空容器或部分结果。</returns>
		/// <exception cref="ArgumentException">offset 越界或 endOffset ≥ 字符串长度（仅输入非空且 maxDepth 非 0 时校验）。</exception>
		/// <remarks>
		/// 宽松解析：括号不匹配 / 未闭合不会抛低级运行时异常（返回空容器、部分结果或 Null）；游离分隔符与悬空 key 会被清理；
		/// 尾随垃圾按首个完整值处理（如 "true xxx" 解析为 true）；非法数字解析为 Null 并记录 warning，非法转义按原文保留（不抛异常）。
		/// </remarks>
		public static JSONObject Create(string jsonString, int offset = 0, int endOffset = -1, int maxDepth = -1, bool storeExcessLevels = false) {
			var jsonObject = Create();
			Parse(jsonString, ref offset, endOffset, jsonObject, maxDepth, storeExcessLevels);
			return jsonObject;
		}

		/// <summary>创建节点并立即调用 <paramref name="content"/> 填充内容。</summary>
		/// <exception cref="ArgumentNullException"><paramref name="content"/> 为 null。</exception>
		public static JSONObject Create(AddJSONContents content) {
			if (content == null)
				throw new ArgumentNullException(nameof(content));

			var jsonObject = Create();
			content.Invoke(jsonObject);
			return jsonObject;
		}

		/// <summary>创建 Array 节点，元素引用传入数组中的实例。</summary>
		public static JSONObject Create(JSONObject[] objects) {
			var jsonObject = Create();
			jsonObject.mType = Type.Array;
			var list = CreateJSONObjectList();
			list.AddRange(objects);
			jsonObject.List = list;

			return jsonObject;
		}

		/// <summary>创建 Array 节点，直接使用传入列表作为内部容器（不复制）。</summary>
		public static JSONObject Create(List<JSONObject> objects) {
			var jsonObject = Create();
			jsonObject.mType = Type.Array;
			jsonObject.List = objects;

			return jsonObject;
		}

		/// <summary>创建 Object 节点，每个值转为 String 节点。</summary>
		public static JSONObject Create(Dictionary<string, string> dictionary) {
			var jsonObject = Create();
			jsonObject.mType = Type.Object;
			var keys = CreateStringList();
			jsonObject.Keys = keys;
			var list = CreateJSONObjectList();
			jsonObject.List = list;
			foreach (var kvp in dictionary) {
				keys.Add(kvp.Key);
				list.Add(CreateStringObject(kvp.Value));
			}

			return jsonObject;
		}

		/// <summary>创建 Object 节点，值节点直接引用字典中的实例（不复制）。</summary>
		public static JSONObject Create(Dictionary<string, JSONObject> dictionary) {
			var jsonObject = Create();
			jsonObject.mType = Type.Object;
			var keys = CreateStringList();
			jsonObject.Keys = keys;
			var list = CreateJSONObjectList();
			jsonObject.List = list;
			foreach (var kvp in dictionary) {
				keys.Add(kvp.Key);
				list.Add(kvp.Value);
			}

			return jsonObject;
		}

		/// <summary>
		/// 分步解析 JSON 字符串（yield 迭代器，不是 Task/线程异步）：迭代过程中每次超出分帧预算
		/// （约 8ms）产出一次 Pause=true 的暂停结果，最终产出 Pause=false 的结果。
		/// </summary>
		/// <param name="jsonString">待解析的 JSON 字符串；null 或空串时直接产出 Null 结果。</param>
		/// <param name="offset">解析起点（字符串索引）。</param>
		/// <param name="endOffset">解析终点（含）；-1 表示直到字符串末尾。</param>
		/// <param name="maxDepth">最大展开深度；0 表示完全不解析，负数表示不限制（含义同 <see cref="Create(string, int, int, int, bool)"/>）。</param>
		/// <param name="storeExcessLevels">超出 maxDepth 的层级是否按 Baked 保留原始片段。</param>
		/// <returns>产出序列：若干 Pause=true 的暂停项，最后一项 Pause=false 且 Result 为解析结果。</returns>
		/// <exception cref="ArgumentException">offset 越界或 endOffset ≥ 字符串长度（仅输入非空且 maxDepth 非 0 时校验）。</exception>
		public static IEnumerable<ParseResult> CreateAsync(string jsonString, int offset = 0, int endOffset = -1, int maxDepth = -1, bool storeExcessLevels = false) {
			var jsonObject = Create();
			// 解析器用共享 Stopwatch 衡量单帧预算，超过 MaxFrameTime 时通过 Pause 让出
			PrintWatch.Reset();
			PrintWatch.Start();
			foreach (var e in ParseAsync(jsonString, offset, endOffset, jsonObject, maxDepth, storeExcessLevels)) {
				if (e.Pause)
					yield return e;

				offset = e.Offset;
			}

			yield return new ParseResult(jsonObject, offset, false);
		}

		/// <summary>创建 Null 节点（等价于 <see cref="Create()"/>）。</summary>
		public JSONObject() { }

		/// <summary>
		/// 解析 JSON 字符串填充当前实例（解析参数与容错行为同 <see cref="Create(string, int, int, int, bool)"/>）。
		/// </summary>
		/// <param name="jsonString">待解析的 JSON 字符串；null 或空串时保持 Null 节点。</param>
		/// <param name="offset">解析起点（字符串索引）。</param>
		/// <param name="endOffset">解析终点（含）；-1 表示直到字符串末尾。</param>
		/// <param name="maxDepth">最大展开深度；0 表示完全不解析，负数表示不限制。</param>
		/// <param name="storeExcessLevels">超出 maxDepth 的层级是否按 Baked 保留原始片段。</param>
		/// <exception cref="ArgumentException">offset 越界或 endOffset ≥ 字符串长度（仅输入非空且 maxDepth 非 0 时校验）。</exception>
		public JSONObject(string jsonString, int offset = 0, int endOffset = -1, int maxDepth = -1, bool storeExcessLevels = false) {
			Parse(jsonString, ref offset, endOffset, this, maxDepth, storeExcessLevels);
		}

		// ReSharper disable UseNameofExpression
		static bool BeginParse(string inputString, int offset, ref int endOffset, JSONObject container, int maxDepth, bool storeExcessLevels) {
			if (container == null)
				throw new ArgumentNullException("container");

			if (maxDepth == 0) {
				if (storeExcessLevels) {
					container.StringValue = inputString;
					container.mType = Type.Baked;
				} else {
					container.mType = Type.Null;
				}

				return false;
			}

			// null 与空串都视为无内容，保持根节点为 Null（不抛异常）
			if (string.IsNullOrEmpty(inputString)) {
				return false;
			}

			var stringLength = inputString.Length;
			if (endOffset == -1)
				endOffset = stringLength - 1;

			if (endOffset >= stringLength)
				throw new ArgumentException("Cannot parse if end offset is greater than or equal to string length", "endOffset");

			if (offset < 0 || offset > endOffset)
				throw new ArgumentException("Cannot parse if offset is out of range of the input", "offset");

			return true;
		}
		// ReSharper restore UseNameofExpression

		/// <summary>
		/// 判断字符是否属于解析器认可的空白字符（与公开的 <see cref="Whitespace"/> 集合一致）。
		/// </summary>
		static bool IsWhitespace(char character) {
			switch (character) {
				case ' ':
				case '\r':
				case '\n':
				case '\t':
				case '\uFEFF':
					return true;
			}

			return false;
		}

		static void Parse(string inputString, ref int offset, int endOffset, JSONObject container, int maxDepth,
			bool storeExcessLevels, int depth = 0, bool isRoot = true) {
			if (!BeginParse(inputString, offset, ref endOffset, container, maxDepth, storeExcessLevels))
				return;

			// 解析状态：offset 为扫描位置；startOffset 记录当前值起点；
			// openQuote 跟踪是否位于字符串内部（跳过其中的结构字符）；
			// lastValidOffset 记录最后一个有效字符；bakeDepth 用于 maxDepth 裁剪时收集被跳过的层级。
			var startOffset = offset;
			var quoteStart = 0;
			var quoteEnd = 0;
			var lastValidOffset = offset;
			var openQuote = false;
			var bakeDepth = 0;

			while (offset <= endOffset) {
				var currentCharacter = inputString[offset++];
				if (IsWhitespace(currentCharacter))
					continue;

				JSONObject newContainer;
				switch (currentCharacter) {
					case '\\':
						offset++;
						break;
					case '{':
						if (openQuote)
							break;

						if (maxDepth >= 0 && depth >= maxDepth) {
							bakeDepth++;
							break;
						}

						newContainer = container;
						if (!isRoot) {
							newContainer = Create();
							SafeAddChild(container, newContainer);
						}

						newContainer.mType = Type.Object;
						// 开括号位于输入末尾时没有内容可解析，直接保留空容器，避免递归 offset 越界
						if (offset > endOffset)
							return;

						Parse(inputString, ref offset, endOffset, newContainer, maxDepth, storeExcessLevels, depth + 1, false);

						break;
					case '[':
						if (openQuote)
							break;

						if (maxDepth >= 0 && depth >= maxDepth) {
							bakeDepth++;
							break;
						}

						newContainer = container;
						if (!isRoot) {
							newContainer = Create();
							SafeAddChild(container, newContainer);
						}

						newContainer.mType = Type.Array;
						// 同上：开括号位于输入末尾时保留空数组
						if (offset > endOffset)
							return;

						Parse(inputString, ref offset, endOffset, newContainer, maxDepth, storeExcessLevels, depth + 1, false);

						break;
					case '}':
						if (!ParseObjectEnd(inputString, offset, openQuote, container, startOffset, lastValidOffset, maxDepth, storeExcessLevels, depth, ref bakeDepth))
							return;

						break;
					case ']':
						if (!ParseArrayEnd(inputString, offset, openQuote, container, startOffset, lastValidOffset, maxDepth, storeExcessLevels, depth, ref bakeDepth))
							return;

						break;
					case '"':
						ParseQuote(ref openQuote, offset, ref quoteStart, ref quoteEnd);
						break;
					case ':':
						if (!ParseColon(inputString, openQuote, container, ref startOffset, offset, quoteStart, quoteEnd, bakeDepth))
							return;

						break;
					case ',':
						if (!ParseComma(inputString, openQuote, container, ref startOffset, offset, lastValidOffset, bakeDepth))
							return;

						break;
				}

				lastValidOffset = offset - 1;
			}

			if (isRoot && container != null && container.mType == Type.Null && container.List == null &&
				!string.IsNullOrWhiteSpace(inputString)) {
				//根级原始值(字符串/数字/布尔)没有子容器，直接在根容器上解析
				container.ParseValue(inputString, startOffset, lastValidOffset);
			}

			TrimExcessKeys(container);
		}

		static IEnumerable<ParseResult> ParseAsync(string inputString, int offset, int endOffset, JSONObject container,
			int maxDepth, bool storeExcessLevels, int depth = 0, bool isRoot = true) {
			if (!BeginParse(inputString, offset, ref endOffset, container, maxDepth, storeExcessLevels))
				yield break;

			var startOffset = offset;
			var quoteStart = 0;
			var quoteEnd = 0;
			var lastValidOffset = offset;
			var openQuote = false;
			var bakeDepth = 0;
			while (offset <= endOffset) {
				if (PrintWatch.Elapsed.TotalSeconds > MaxFrameTime) {
					PrintWatch.Reset();
					yield return new ParseResult(container, offset, true);
					PrintWatch.Start();
				}

				var currentCharacter = inputString[offset++];
				if (IsWhitespace(currentCharacter))
					continue;

				JSONObject newContainer;
				switch (currentCharacter) {
					case '\\':
						offset++;
						break;
					case '{':
						if (openQuote)
							break;

						if (maxDepth >= 0 && depth >= maxDepth) {
							bakeDepth++;
							break;
						}

						newContainer = container;
						if (!isRoot) {
							newContainer = Create();
							SafeAddChild(container, newContainer);
						}

						newContainer.mType = Type.Object;
						// 开括号位于输入末尾时没有内容可解析，产出最终结果并结束，避免递归 offset 越界
						if (offset > endOffset) {
							yield return new ParseResult(container, offset, false);
							yield break;
						}

						foreach (var e in ParseAsync(inputString, offset, endOffset, newContainer, maxDepth, storeExcessLevels, depth + 1, false)) {
							if (e.Pause)
								yield return e;

							offset = e.Offset;
						}

						break;
					case '[':
						if (openQuote)
							break;

						if (maxDepth >= 0 && depth >= maxDepth) {
							bakeDepth++;
							break;
						}

						newContainer = container;
						if (!isRoot) {
							newContainer = Create();
							SafeAddChild(container, newContainer);
						}

						newContainer.mType = Type.Array;
						// 同上：开括号位于输入末尾时保留空数组
						if (offset > endOffset) {
							yield return new ParseResult(container, offset, false);
							yield break;
						}

						foreach (var e in ParseAsync(inputString, offset, endOffset, newContainer, maxDepth, storeExcessLevels, depth + 1, false)) {
							if (e.Pause)
								yield return e;

							offset = e.Offset;
						}

						break;
					case '}':
						if (!ParseObjectEnd(inputString, offset, openQuote, container, startOffset, lastValidOffset, maxDepth, storeExcessLevels, depth, ref bakeDepth)) {
							yield return new ParseResult(container, offset, false);
							yield break;
						}

						break;
					case ']':
						if (!ParseArrayEnd(inputString, offset, openQuote, container, startOffset, lastValidOffset, maxDepth, storeExcessLevels, depth, ref bakeDepth)) {
							yield return new ParseResult(container, offset, false);
							yield break;
						}

						break;
					case '"':
						ParseQuote(ref openQuote, offset, ref quoteStart, ref quoteEnd);
						break;
					case ':':
						if (!ParseColon(inputString, openQuote, container, ref startOffset, offset, quoteStart, quoteEnd, bakeDepth)) {
							yield return new ParseResult(container, offset, false);
							yield break;
						}

						break;
					case ',':
						if (!ParseComma(inputString, openQuote, container, ref startOffset, offset, lastValidOffset, bakeDepth)) {
							yield return new ParseResult(container, offset, false);
							yield break;
						}

						break;
				}

				lastValidOffset = offset - 1;
			}

			if (isRoot && container != null && container.mType == Type.Null && container.List == null &&
				!string.IsNullOrWhiteSpace(inputString)) {
				//根级原始值(字符串/数字/布尔)没有子容器，直接在根容器上解析
				container.ParseValue(inputString, startOffset, lastValidOffset);
			}

			// 输入在容器闭合前结束（或截断）时对齐 Keys/List，避免悬空 key
			TrimExcessKeys(container);
			yield return new ParseResult(container, offset, false);
		}

		static void SafeAddChild(JSONObject container, JSONObject child) {
			var list = container.List;
			if (list == null) {
				list = CreateJSONObjectList();
				container.List = list;
			}

			list.Add(child);
		}

		void ParseValue(string inputString, int startOffset, int lastValidOffset) {
			if (string.IsNullOrEmpty(inputString) || startOffset < 0 || startOffset > lastValidOffset)
				return;

			var firstCharacter = inputString[startOffset];
			while (IsWhitespace(firstCharacter)) {
				// 跳过值前面的空白；越界说明没有实际值，保持 Null
				if (startOffset >= lastValidOffset)
					return;

				firstCharacter = inputString[++startOffset];
			}

			// Use character comparison instead of string compare as performance optimization
			switch (firstCharacter)
			{
				case '"':
					mType = Type.String;

					// 闭合引号本身位于 lastValidOffset；未闭合时把最后一个有效字符也算进内容
					var contentStart = startOffset + 1;
					var contentEnd = lastValidOffset;
					if (contentEnd < inputString.Length && inputString[contentEnd] != '"')
						contentEnd++;

					var contentLength = contentEnd - contentStart;
					if (contentLength < 0)
						contentLength = 0;

					StringValue = UnEscapeString(inputString.Substring(contentStart, contentLength));
					return;
				case 't':
					mType = Type.Bool;
					BoolValue = true;
					return;
				case 'f':
					mType = Type.Bool;
					BoolValue = false;
					return;
				case 'n':
					mType = Type.Null;
					return;
				case 'I':
					mType = Type.Number;

#if JSONOBJECT_USE_FLOAT
					floatValue = float.PositiveInfinity;
#else
					DoubleValue = double.PositiveInfinity;
#endif

					return;
				case 'N':
					mType = Type.Number;

#if JSONOBJECT_USE_FLOAT
					floatValue = float.NaN;
#else
					DoubleValue = double.NaN;
#endif
					return;
				case '-':
					if (startOffset + 1 <= lastValidOffset && inputString[startOffset + 1] == 'I') {
						mType = Type.Number;
#if JSONOBJECT_USE_FLOAT
						floatValue = float.NegativeInfinity;
#else
						DoubleValue = double.NegativeInfinity;
#endif
						return;
					}

					break;
			}

			var numericString = inputString.Substring(startOffset, lastValidOffset - startOffset + 1);
			try {
				// 含小数点或指数的按浮点解析；始终使用 InvariantCulture，避免跟随系统区域设置
				if (numericString.IndexOf('.') >= 0 || numericString.IndexOf('e') >= 0 || numericString.IndexOf('E') >= 0) {
#if JSONOBJECT_USE_FLOAT
					floatValue = Convert.ToSingle(numericString, CultureInfo.InvariantCulture);
#else
					DoubleValue = Convert.ToDouble(numericString, CultureInfo.InvariantCulture);
#endif
					IsInteger = false;
				} else {
					LongValue = Convert.ToInt64(numericString, CultureInfo.InvariantCulture);
					IsInteger = true;
#if JSONOBJECT_USE_FLOAT
					floatValue = longValue;
#else
					DoubleValue = LongValue;
#endif
				}

				mType = Type.Number;
			} catch (OverflowException) {
				mType = Type.Number;
				IsInteger = false;
#if JSONOBJECT_USE_FLOAT
				floatValue = numericString.StartsWith("-") ? float.NegativeInfinity : float.PositiveInfinity;
#else
				DoubleValue = numericString.StartsWith("-") ? double.NegativeInfinity : double.PositiveInfinity;
#endif
			} catch (FormatException) {
				mType = Type.Null;
#if USING_UNITY
				Debug.LogWarning
#else
				Debug.WriteLine
#endif
					(string.Format("Improper JSON formatting:{0}", numericString));
			}
		}

		static bool ParseObjectEnd(string inputString, int offset, bool openQuote, JSONObject container, int startOffset,
			int lastValidOffset, int maxDepth, bool storeExcessLevels, int depth, ref int bakeDepth) {
			if (openQuote)
				return true;

			if (container == null) {
				Debug.LogError("Parsing error: encountered `}` with no container object");
				return false;
			}

			if (maxDepth >= 0 && depth >= maxDepth) {
				bakeDepth--;
				if (bakeDepth == 0) {
					SafeAddChild(container,
						storeExcessLevels
							? CreateBakedObject(inputString.Substring(startOffset, offset - startOffset))
							: NullObject);
				}

				if (bakeDepth >= 0)
					return true;
			}

			ParseFinalObjectIfNeeded(inputString, container, startOffset, lastValidOffset);
			TrimExcessKeys(container);
			return false;
		}

		static bool ParseArrayEnd(string inputString, int offset, bool openQuote, JSONObject container,
			int startOffset, int lastValidOffset, int maxDepth, bool storeExcessLevels, int depth, ref int bakeDepth) {
			if (openQuote)
				return true;

			if (container == null) {
				Debug.LogError("Parsing error: encountered `]` with no container object");
				return false;
			}

			if (maxDepth >= 0 && depth >= maxDepth) {
				bakeDepth--;
				if (bakeDepth == 0) {
					SafeAddChild(container,
						storeExcessLevels
							? CreateBakedObject(inputString.Substring(startOffset, offset - startOffset))
							: NullObject);
				}

				if (bakeDepth >= 0)
					return true;
			}

			ParseFinalObjectIfNeeded(inputString, container, startOffset, lastValidOffset);
			TrimExcessKeys(container);
			return false;
		}

		static void ParseQuote(ref bool openQuote, int offset, ref int quoteStart, ref int quoteEnd) {
			if (openQuote) {
				quoteEnd = offset - 1;
				openQuote = false;
			} else {
				quoteStart = offset;
				openQuote = true;
			}
		}

		static bool ParseColon(string inputString, bool openQuote, JSONObject container,
			ref int startOffset,int offset, int quoteStart, int quoteEnd, int bakeDepth) {
			if (openQuote || bakeDepth > 0)
				return true;

			if (container == null) {
				Debug.LogError("Parsing error: encountered `:` with no container object");
				return false;
			}

			var keys = container.Keys;
			if (keys == null) {
				keys = CreateStringList();
				container.Keys = keys;
			}

			// 字段名去引号后再反转义，保证与值的转义语义一致（输出时会重新转义）
			var keyLength = quoteEnd - quoteStart;
			if (keyLength < 0)
				keyLength = 0;

			container.Keys.Add(UnEscapeString(inputString.Substring(quoteStart, keyLength)));
			startOffset = offset;

			return true;
		}

		static bool ParseComma(string inputString, bool openQuote, JSONObject container,
			ref int startOffset, int offset, int lastValidOffset, int bakeDepth) {
			if (openQuote || bakeDepth > 0)
				return true;

			if (container == null) {
				Debug.LogError("Parsing error: encountered `,` with no container object");
				return false;
			}

			ParseFinalObjectIfNeeded(inputString, container, startOffset, lastValidOffset);

			startOffset = offset;
			return true;
		}

		static void ParseFinalObjectIfNeeded(string inputString, JSONObject container, int startOffset, int lastValidOffset) {
			if (startOffset > lastValidOffset)
				return;

			// 区间末尾是闭合符或分隔符（如 "[,1]"、"{,\"a\":1}"）时没有实际值，跳过以避免幽灵节点
			var lastCharacter = inputString[lastValidOffset];
			if (IsClosingCharacter(lastCharacter) || lastCharacter == ',' || lastCharacter == ':')
				return;

			// 区间内只有空白（例如 "{ }"、"[ ]" 或尾随逗号后的空格）时没有实际值，跳过以避免生成幽灵子节点
			var hasValue = false;
			for (var index = startOffset; index <= lastValidOffset; index++) {
				if (!IsWhitespace(inputString[index])) {
					hasValue = true;
					break;
				}
			}

			if (!hasValue)
				return;

			var child = Create();
			child.ParseValue(inputString, startOffset, lastValidOffset);
			SafeAddChild(container, child);
		}

		/// <summary>
		/// 解析收尾：移除没有对应值的悬空 key，保证 Keys 数量不超过 List，避免 malformed 输入留下不一致状态。
		/// </summary>
		static void TrimExcessKeys(JSONObject container) {
			if (container == null || container.Keys == null)
				return;

			var valueCount = container.List == null ? 0 : container.List.Count;
			while (container.Keys.Count > valueCount)
				container.Keys.RemoveAt(container.Keys.Count - 1);
		}

		static bool IsClosingCharacter(char character) {
			switch (character) {
				case '}':
				case ']':
					return true;
			}

			return false;
		}

		/// <summary>是否为 Number。</summary>
		public bool IsNumber {
			get { return mType == Type.Number; }
		}

		/// <summary>是否为 Null。</summary>
		public bool IsNull {
			get { return mType == Type.Null; }
		}

		/// <summary>是否为 String。</summary>
		public bool IsString {
			get { return mType == Type.String; }
		}

		/// <summary>是否为 Bool。</summary>
		public bool IsBool {
			get { return mType == Type.Bool; }
		}

		/// <summary>是否为 Array。</summary>
		public bool IsArray {
			get { return mType == Type.Array; }
		}

		/// <summary>是否为 Object。</summary>
		public bool IsObject {
			get { return mType == Type.Object; }
		}

		/// <summary>是否为 Baked。</summary>
		public bool IsBaked {
			get { return mType == Type.Baked; }
		}

		/// <summary>清空字符串/数字/布尔等标量状态；容器 List/Keys 保留，供类型切换时复用。</summary>
		void ClearValueState() {
			StringValue = null;
			LongValue = 0;
			BoolValue = false;
			IsInteger = false;
#if JSONOBJECT_USE_FLOAT
			floatValue = 0;
#else
			DoubleValue = 0;
#endif
		}

		/// <summary>向数组追加一个 Bool 值（节点类型会转换为 Array）。</summary>
		public void Add(bool value) {
			Add(Create(value));
		}

		/// <summary>向数组追加一个浮点值（节点类型会转换为 Array）。</summary>
		public void Add(float value) {
			Add(Create(value));
		}

		/// <summary>向数组追加一个浮点值（节点类型会转换为 Array）。</summary>
		public void Add(double value) {
			Add(Create(value));
		}

		/// <summary>向数组追加一个整数值（节点类型会转换为 Array）。</summary>
		public void Add(long value) {
			Add(Create(value));
		}

		/// <summary>向数组追加一个整数值（节点类型会转换为 Array）。</summary>
		public void Add(int value) {
			Add(Create(value));
		}

		/// <summary>向数组追加一个字符串（节点类型会转换为 Array）。</summary>
		public void Add(string value) {
			Add(CreateStringObject(value));
		}

		/// <summary>向数组追加一个由委托构建的节点（节点类型会转换为 Array）。</summary>
		public void Add(AddJSONContents content) {
			Add(Create(content));
		}

		/// <summary>
		/// 向数组追加一个节点（节点类型会转换为 Array）；<paramref name="jsonObject"/> 为 null 时不处理。
		/// 已有 List/Keys 不会被清空，以保留数组与对象按索引复用内容的历史行为。
		/// </summary>
		public void Add(JSONObject jsonObject) {
			if (jsonObject == null)
				return;

			// Convert to array to support list
			if (mType != Type.Array)
				ClearValueState();

			mType = Type.Array;
			if (List == null)
				List = CreateJSONObjectList();

			List.Add(jsonObject);
		}

		/// <summary>添加 Bool 字段（节点类型会转换为 Object）。</summary>
		public void AddField(string name, bool value) {
			AddField(name, Create(value));
		}

		/// <summary>添加浮点字段（节点类型会转换为 Object）。</summary>
		public void AddField(string name, float value) {
			AddField(name, Create(value));
		}

		/// <summary>添加浮点字段（节点类型会转换为 Object）。</summary>
		public void AddField(string name, double value) {
			AddField(name, Create(value));
		}

		/// <summary>添加整数字段（节点类型会转换为 Object）。</summary>
		public void AddField(string name, int value) {
			AddField(name, Create(value));
		}

		/// <summary>添加整数字段（节点类型会转换为 Object）。</summary>
		public void AddField(string name, long value) {
			AddField(name, Create(value));
		}

		/// <summary>添加由委托构建的字段（节点类型会转换为 Object）。</summary>
		public void AddField(string name, AddJSONContents content) {
			AddField(name, Create(content));
		}

		/// <summary>添加字符串字段（节点类型会转换为 Object）。</summary>
		public void AddField(string name, string value) {
			AddField(name, CreateStringObject(value));
		}

		/// <summary>
		/// 添加字段（节点类型会转换为 Object）；<paramref name="jsonObject"/> 为 null 时不处理。
		/// 允许同名 key 重复添加，查找类 API 返回第一个匹配项。
		/// </summary>
		public void AddField(string name, JSONObject jsonObject) {
			if (jsonObject == null)
				return;

			// Convert to object if needed to support fields
			if (mType != Type.Object)
				ClearValueState();

			mType = Type.Object;
			if (List == null)
				List = CreateJSONObjectList();

			if (Keys == null)
				Keys = CreateStringList();

			// 从数组转为对象时，为已有元素补上数字 key，保证 Keys 与 List 按索引对齐
			while (Keys.Count < List.Count) {
				Keys.Add(Keys.Count.ToString(CultureInfo.InvariantCulture));
			}

			Keys.Add(name);
			List.Add(jsonObject);
		}

		/// <summary>设置字符串字段（已存在则替换，不存在则添加）。</summary>
		public void SetField(string name, string value) {
			SetField(name, CreateStringObject(value));
		}

		/// <summary>设置 Bool 字段（已存在则替换，不存在则添加）。</summary>
		public void SetField(string name, bool value) {
			SetField(name, Create(value));
		}

		/// <summary>设置浮点字段（已存在则替换，不存在则添加）。</summary>
		public void SetField(string name, float value) {
			SetField(name, Create(value));
		}

		/// <summary>设置浮点字段（已存在则替换，不存在则添加）。</summary>
		public void SetField(string name, double value) {
			SetField(name, Create(value));
		}

		/// <summary>设置整数字段（已存在则替换，不存在则添加）。</summary>
		public void SetField(string name, long value) {
			SetField(name, Create(value));
		}

		/// <summary>设置整数字段（已存在则替换，不存在则添加）。</summary>
		public void SetField(string name, int value) {
			SetField(name, Create(value));
		}

		/// <summary>
		/// 设置字段：已存在则替换，不存在则添加；<paramref name="jsonObject"/> 为 null 时仅删除已有同名字段。
		/// 存在多个同名 key 时只替换第一个匹配项。
		/// </summary>
		public void SetField(string name, JSONObject jsonObject) {
			if (mType == Type.Object && Keys != null && List != null) {
				var index = Keys.IndexOf(name);
				if (index >= 0) {
					List.RemoveAt(index);
					Keys.RemoveAt(index);
				}
			}

			AddField(name, jsonObject);
		}

		/// <summary>删除字段；不存在或节点不是字段容器时不处理。</summary>
		public void RemoveField(string name) {
			if (Keys == null || List == null)
				return;

			var index = Keys.IndexOf(name);
			if (index < 0)
				return;

			List.RemoveAt(index);
			Keys.RemoveAt(index);
		}

		/// <summary>按字段名读取 Bool 值；字段不存在时保持 fallback 并返回 false。</summary>
		public bool GetField(out bool field, string name, bool fallback) {
			field = fallback;
			return GetField(ref field, name);
		}

		/// <summary>按字段名读取 Bool 值；字段存在时返回 true，否则调用 <paramref name="fail"/> 并返回 false。</summary>
		public bool GetField(ref bool field, string name, FieldNotFound fail = null) {
			if (mType == Type.Object && Keys != null && List != null) {
				var index = Keys.IndexOf(name);
				if (index >= 0) {
					field = List[index].BoolValue;
					return true;
				}
			}

			if (fail != null) fail.Invoke(name);
			return false;
		}

		/// <summary>按字段名读取 double 值；字段不存在时保持 fallback 并返回 false。</summary>
		public bool GetField(out double field, string name, double fallback) {
			field = fallback;
			return GetField(ref field, name);
		}

		/// <summary>按字段名读取 double 值；字段存在时返回 true，否则调用 <paramref name="fail"/> 并返回 false。</summary>
		public bool GetField(ref double field, string name, FieldNotFound fail = null) {
			if (mType == Type.Object && Keys != null && List != null) {
				var index = Keys.IndexOf(name);
				if (index >= 0) {
#if JSONOBJECT_USE_FLOAT
					field = list[index].floatValue;
#else
					field = List[index].DoubleValue;
#endif
					return true;
				}
			}

			if (fail != null) fail.Invoke(name);
			return false;
		}

		/// <summary>按字段名读取 float 值；字段不存在时保持 fallback 并返回 false。</summary>
		public bool GetField(out float field, string name, float fallback) {
			field = fallback;
			return GetField(ref field, name);
		}

		/// <summary>按字段名读取 float 值；字段存在时返回 true，否则调用 <paramref name="fail"/> 并返回 false。</summary>
		public bool GetField(ref float field, string name, FieldNotFound fail = null) {
			if (mType == Type.Object && Keys != null && List != null) {
				var index = Keys.IndexOf(name);
				if (index >= 0) {
#if JSONOBJECT_USE_FLOAT
					field = list[index].floatValue;
#else
					field = (float) List[index].DoubleValue;
#endif
					return true;
				}
			}

			if (fail != null) fail.Invoke(name);
			return false;
		}

		/// <summary>按字段名读取 int 值；字段不存在时保持 fallback 并返回 false。</summary>
		public bool GetField(out int field, string name, int fallback) {
			field = fallback;
			return GetField(ref field, name);
		}

		/// <summary>按字段名读取 int 值；字段存在时返回 true，否则调用 <paramref name="fail"/> 并返回 false。</summary>
		public bool GetField(ref int field, string name, FieldNotFound fail = null) {
			if (mType == Type.Object && Keys != null && List != null) {
				var index = Keys.IndexOf(name);
				if (index >= 0) {
					field = (int) List[index].LongValue;
					return true;
				}
			}

			if (fail != null) fail.Invoke(name);
			return false;
		}

		/// <summary>按字段名读取 long 值；字段不存在时保持 fallback 并返回 false。</summary>
		public bool GetField(out long field, string name, long fallback) {
			field = fallback;
			return GetField(ref field, name);
		}

		/// <summary>按字段名读取 long 值；字段存在时返回 true，否则调用 <paramref name="fail"/> 并返回 false。</summary>
		public bool GetField(ref long field, string name, FieldNotFound fail = null) {
			if (mType == Type.Object && Keys != null && List != null) {
				var index = Keys.IndexOf(name);
				if (index >= 0) {
					field = List[index].LongValue;
					return true;
				}
			}

			if (fail != null) fail.Invoke(name);
			return false;
		}

		/// <summary>按字段名读取 uint 值；字段不存在时保持 fallback 并返回 false。</summary>
		public bool GetField(out uint field, string name, uint fallback) {
			field = fallback;
			return GetField(ref field, name);
		}

		/// <summary>按字段名读取 uint 值；字段存在时返回 true，否则调用 <paramref name="fail"/> 并返回 false。</summary>
		public bool GetField(ref uint field, string name, FieldNotFound fail = null) {
			if (mType == Type.Object && Keys != null && List != null) {
				var index = Keys.IndexOf(name);
				if (index >= 0) {
					field = (uint) List[index].LongValue;
					return true;
				}
			}

			if (fail != null) fail.Invoke(name);
			return false;
		}

		/// <summary>按字段名读取字符串值；字段不存在时保持 fallback 并返回 false。</summary>
		public bool GetField(out string field, string name, string fallback) {
			field = fallback;
			return GetField(ref field, name);
		}

		/// <summary>按字段名读取字符串值；字段存在时返回 true，否则调用 <paramref name="fail"/> 并返回 false。</summary>
		public bool GetField(ref string field, string name, FieldNotFound fail = null) {
			if (mType == Type.Object && Keys != null && List != null) {
				var index = Keys.IndexOf(name);
				if (index >= 0) {
					field = List[index].StringValue;
					return true;
				}
			}

			if (fail != null) fail.Invoke(name);
			return false;
		}

		/// <summary>按字段名取值并回调；字段不存在时调用 <paramref name="fail"/>（若提供）。</summary>
		public void GetField(string name, GetFieldResponse response, FieldNotFound fail = null) {
			if (response != null && mType == Type.Object && Keys != null && List != null) {
				var index = Keys.IndexOf(name);
				if (index >= 0) {
					response.Invoke(List[index]);
					return;
				}
			}

			if (fail != null)
				fail.Invoke(name);
		}

		/// <summary>按字段名获取成员节点；不存在或节点不是 Object 时返回 null。</summary>
		public JSONObject GetField(string name) {
			if (mType == Type.Object && Keys != null && List != null) {
				var index = Keys.IndexOf(name);
				if (index >= 0)
					return List[index];
			}

			return null;
		}

		/// <summary>尝试按字段名获取成员节点；成功时返回 true，失败时 <paramref name="value"/> 为 null。</summary>
		public bool TryGetField(string name, out JSONObject value) {
			value = GetField(name);
			return value != null;
		}

		/// <summary>尝试按字段名读取字符串；字段存在即成功，值取该节点的 <see cref="StringValue"/>。</summary>
		public bool TryGetString(string name, out string value) {
			if (mType == Type.Object && Keys != null && List != null) {
				var index = Keys.IndexOf(name);
				if (index >= 0) {
					value = List[index].StringValue;
					return true;
				}
			}

			value = null;
			return false;
		}

		/// <summary>尝试按字段名读取 int；字段不存在时返回 false 且 <paramref name="value"/> 为 0。</summary>
		public bool TryGetInt(string name, out int value) {
			if (mType == Type.Object && Keys != null && List != null) {
				var index = Keys.IndexOf(name);
				if (index >= 0) {
					value = (int) List[index].LongValue;
					return true;
				}
			}

			value = 0;
			return false;
		}

		/// <summary>尝试按字段名读取 float；字段不存在时返回 false 且 <paramref name="value"/> 为 0。</summary>
		public bool TryGetFloat(string name, out float value) {
			if (mType == Type.Object && Keys != null && List != null) {
				var index = Keys.IndexOf(name);
				if (index >= 0) {
#if JSONOBJECT_USE_FLOAT
					value = List[index].floatValue;
#else
					value = List[index].FloatValue;
#endif
					return true;
				}
			}

			value = 0;
			return false;
		}

		/// <summary>尝试按字段名读取 bool；字段不存在时返回 false 且 <paramref name="value"/> 为 false。</summary>
		public bool TryGetBool(string name, out bool value) {
			if (mType == Type.Object && Keys != null && List != null) {
				var index = Keys.IndexOf(name);
				if (index >= 0) {
					value = List[index].BoolValue;
					return true;
				}
			}

			value = false;
			return false;
		}

		/// <summary>检查是否包含全部指定字段；节点不是 Object 或 <paramref name="names"/> 为 null 时返回 false。</summary>
		public bool HasFields(string[] names) {
			if (mType != Type.Object || Keys == null || List == null || names == null)
				return false;

			foreach (var name in names)
				if (!Keys.Contains(name))
					return false;

			return true;
		}

		/// <summary>检查是否存在指定字段；节点不是 Object 时返回 false。</summary>
		public bool HasField(string name) {
			if (mType != Type.Object || Keys == null || List == null)
				return false;

			return Keys.IndexOf(name) >= 0;
		}

		/// <summary>
		/// 重置为 Null 节点：清空 List/Keys 与所有标量状态，之后可继续复用。
		/// </summary>
		public void Clear() {
#if JSONOBJECT_POOLING
			if (list != null) {
				lock (JSONObjectListPool) {
					if (poolingEnabled && JSONObjectListPool.Count < MaxPoolSize) {
						list.Clear();
						JSONObjectListPool.Enqueue(list);
					}
				}
			}

			if (keys != null) {
				lock (StringListPool) {
					if (poolingEnabled && StringListPool.Count < MaxPoolSize) {
						keys.Clear();
						StringListPool.Enqueue(keys);
					}
				}
			}
#endif

			mType = Type.Null;
			List = null;
			Keys = null;
			ClearValueState();
		}

		/// <summary>
		/// 深拷贝：通过 Print + Parse 重建整棵树，副本与原对象完全独立（子节点也是新实例）。
		/// 注意：Baked 节点会在拷贝后还原为解析出的普通结构。
		/// </summary>
		/// <returns>新的 JSONObject。</returns>
		public JSONObject Copy() {
			return Create(Print());
		}

		/*
		 * The Merge function is experimental. Use at your own risk.
		 */
		/// <summary>
		/// 将 <paramref name="jsonObject"/> 合并进当前节点（实验性 API，保留上游语义）：
		/// Object 递归合并、Array 同类型按索引覆盖、Null 直接 Absorb、类型不匹配不处理。
		/// 被合并的容器/值节点为引用共享，不是深拷贝。传入 null 时不处理。
		/// </summary>
		public void Merge(JSONObject jsonObject) {
			if (jsonObject == null)
				return;

			MergeRecur(this, jsonObject);
		}

		/// <summary>
		/// 将 right 递归合并进 left：Object 按键递归、Array 按索引覆盖、Null 直接吸收。
		/// </summary>
		/// <param name="left">被合并的基对象。</param>
		/// <param name="right">提供新值的对象。</param>
		static void MergeRecur(JSONObject left, JSONObject right) {
			if (left.mType == Type.Null) {
				left.Absorb(right);
			} else if (left.mType == Type.Object && right.mType == Type.Object && right.List != null && right.Keys != null) {
				// 同名容器递归合并，其余字段右侧直接覆盖或新增（节点引用共享）
				for (var i = 0; i < right.List.Count; i++) {
					var key = right.Keys[i];
					if (right[i].IsContainer) {
						if (left.HasField(key))
							MergeRecur(left[key], right[i]);
						else
							left.AddField(key, right[i]);
					} else {
						if (left.HasField(key))
							left.SetField(key, right[i]);
						else
							left.AddField(key, right[i]);
					}
				}
			} else if (left.mType == Type.Array && right.mType == Type.Array && right.List != null) {
				// 数组要求右侧不长于左侧；仅当同索引元素类型相同时覆盖，容器递归合并
				if (right.Count > left.Count) {
#if USING_UNITY
					Debug.LogError
#else
					Debug.WriteLine
#endif
						("Cannot merge arrays when right object has more elements");
					return;
				}

				for (var i = 0; i < right.List.Count; i++) {
					if (left[i].mType == right[i].mType) {
						//Only overwrite with the same type
						if (left[i].IsContainer)
							MergeRecur(left[i], right[i]);
						else {
							left[i] = right[i];
						}
					}
				}
			}
		}

		/// <summary>
		/// 将当前节点序列化结果固化到 <see cref="StringValue"/> 并转为 Baked（冻结）：之后修改子节点不会影响输出。
		/// 已是 Baked 时直接返回。
		/// </summary>
		public void Bake() {
			if (mType == Type.Baked)
				return;

			StringValue = Print();
			mType = Type.Baked;
		}

		/// <summary>分步执行 <see cref="Bake"/>（yield 迭代器，非 Task 异步）。</summary>
		public IEnumerable BakeAsync() {
			if (mType == Type.Baked)
				yield break;

			var builder = new StringBuilder();
			using (var enumerator = PrintAsync(builder).GetEnumerator()) {
				while (enumerator.MoveNext()) {
					if (enumerator.Current)
						yield return null;
				}

				StringValue = builder.ToString();
				mType = Type.Baked;
			}
		}

		/// <summary>序列化为 JSON 字符串；<paramref name="pretty"/> 为 true 时输出换行与缩进。</summary>
		/// <remarks>
		/// 历史扩展：Number 为 NaN / ±Infinity 时输出为 NaN / Infinity / -Infinity 文本（非严格 JSON）。
		/// null 子节点与缺少对应 key 的条目会被跳过（输出可能少于容器实际内容），容器结构与分隔符始终合法。
		/// </remarks>
		public string Print(bool pretty = false) {
			var builder = new StringBuilder();
			Print(builder, pretty);
			return builder.ToString();
		}

		/// <summary>将 JSON 字符串追加写入 <paramref name="builder"/>。</summary>
		public void Print(StringBuilder builder, bool pretty = false) {
			Stringify(0, builder, pretty);
		}

		/// <summary>输出字符串转义：反斜杠、引号、控制字符；数字等其它字符原样保留（含中文/emoji）。</summary>
		static string EscapeString(string input) {
			if (string.IsNullOrEmpty(input))
				return input ?? string.Empty;

			var builder = new StringBuilder(input.Length + 8);
			for (var index = 0; index < input.Length; index++) {
				var character = input[index];
				switch (character) {
					case '\\': builder.Append("\\\\"); break;
					case '"': builder.Append("\\\""); break;
					case '\b': builder.Append("\\b"); break;
					case '\f': builder.Append("\\f"); break;
					case '\n': builder.Append("\\n"); break;
					case '\r': builder.Append("\\r"); break;
					case '\t': builder.Append("\\t"); break;
					default:
						// 其余控制字符编码为 \\uXXXX；可见 Unicode（中文/emoji）保持原样
						if (character < ' ')
							builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
						else
							builder.Append(character);
						break;
				}
			}

			return builder.ToString();
		}

		/// <summary>
		/// 解析字符串转义：支持 \\ \" \/ \b \f \n \r \t 与 \uXXXX；
		/// 非法转义按原样保留，保证无异常。单次扫描，避免多轮 Replace 的次序问题。
		/// </summary>
		static string UnEscapeString(string input) {
			if (string.IsNullOrEmpty(input))
				return input;

			StringBuilder builder = null;
			var segmentStart = 0;
			for (var index = 0; index < input.Length; index++) {
				if (input[index] != '\\')
					continue;

				if (index + 1 >= input.Length) {
					// 末尾孤立反斜杠：原样保留
					if (builder == null)
						builder = new StringBuilder(input.Length);
					builder.Append(input, segmentStart, index - segmentStart);
					builder.Append('\\');
					segmentStart = input.Length;
					break;
				}

				if (builder == null)
					builder = new StringBuilder(input.Length);

				builder.Append(input, segmentStart, index - segmentStart);
				index++;
				var escape = input[index];
				switch (escape) {
					case '"': builder.Append('"'); break;
					case '\\': builder.Append('\\'); break;
					case '/': builder.Append('/'); break;
					case 'b': builder.Append('\b'); break;
					case 'f': builder.Append('\f'); break;
					case 'n': builder.Append('\n'); break;
					case 'r': builder.Append('\r'); break;
					case 't': builder.Append('\t'); break;
					case 'u':
						if (index + 4 < input.Length && ushort.TryParse(input.Substring(index + 1, 4),
							    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) {
							builder.Append((char)code);
							index += 4;
						} else {
							// 非法的 \u 转义：按原样保留
							builder.Append("\\u");
						}

						break;
					default:
						// 未知转义：保留反斜杠与原字符
						builder.Append('\\').Append(escape);
						break;
				}

				segmentStart = index + 1;
			}

			if (builder == null)
				return input;

			builder.Append(input, segmentStart, input.Length - segmentStart);
			return builder.ToString();
		}

		/// <summary>
		/// 分步序列化：序列化完成时产出最终字符串；中途暂停时产出 null。
		/// 这是 yield 迭代器（分帧），不是 Task/线程异步。
		/// </summary>
		public IEnumerable<string> PrintAsync(bool pretty = false) {
			var builder = new StringBuilder();
			foreach (var pause in PrintAsync(builder, pretty)) {
				if (pause)
					yield return null;
			}

			yield return builder.ToString();
		}

		/// <summary>分步序列化到 <paramref name="builder"/>；每轮超过分帧预算时产出 true（yield 迭代器，非 Task 异步）。</summary>
		public IEnumerable<bool> PrintAsync(StringBuilder builder, bool pretty = false) {
			PrintWatch.Reset();
			PrintWatch.Start();
			using (var enumerator = StringifyAsync(0, builder, pretty).GetEnumerator()) {
				while (enumerator.MoveNext()) {
					if (enumerator.Current)
						yield return true;
				}
			}
		}

		/// <summary>
		/// 分步把当前节点序列化进 <paramref name="builder"/>；超过分帧预算时产出 true。
		/// </summary>
		/// <param name="depth">当前已进入的容器层数（进入节点时递增）。</param>
		/// <param name="builder">输出目标。</param>
		/// <param name="pretty">是否输出换行与缩进。</param>
		/// <returns>产出序列：true 表示本轮暂停。</returns>
		/// <remarks>深度规则与同步 Stringify 对齐，保证 pretty 输出与 <see cref="Print(bool)"/> 完全一致。</remarks>
		IEnumerable<bool> StringifyAsync(int depth, StringBuilder builder, bool pretty = false) {
			if (PrintWatch.Elapsed.TotalSeconds > MaxFrameTime) {
				PrintWatch.Reset();
				yield return true;
				PrintWatch.Start();
			}

			depth++;
			switch (mType) {
				case Type.Baked:
					builder.Append(StringValue);
					break;
				case Type.String:
					StringifyString(builder);
					break;
				case Type.Number:
					StringifyNumber(builder);
					break;
				case Type.Object:
					var fieldCount = Count;
					if (fieldCount <= 0) {
						StringifyEmptyObject(builder);
						break;
					}

					BeginStringifyObjectContainer(builder, pretty);
					for (var index = 0; index < fieldCount; index++) {
						var jsonObject = List[index];
						if (jsonObject == null)
							continue;

						if (Keys == null || index >= Keys.Count)
							break;

						var key = Keys[index];
						BeginStringifyObjectField(builder, pretty, depth, key);
						foreach (var pause in jsonObject.StringifyAsync(depth, builder, pretty)) {
							if (pause)
								yield return true;
						}

						EndStringifyObjectField(builder, pretty);
					}

					EndStringifyObjectContainer(builder, pretty, depth);
					break;
				case Type.Array:
					var arraySize = Count;
					if (arraySize <= 0) {
						StringifyEmptyArray(builder);
						break;
					}

					BeginStringifyArrayContainer(builder, pretty);
					for (var index = 0; index < arraySize; index++) {
						var jsonObject = List[index];
						if (jsonObject == null)
							continue;

						BeginStringifyArrayElement(builder, pretty, depth);
						foreach (var pause in List[index].StringifyAsync(depth, builder, pretty)) {
							if (pause)
								yield return true;
						}

						EndStringifyArrayElement(builder, pretty);
					}

					EndStringifyArrayContainer(builder, pretty, depth);
					break;
				case Type.Bool:
					StringifyBool(builder);
					break;
				case Type.Null:
					StringifyNull(builder);
					break;
			}
		}

		/// <summary>
		/// 同步把当前节点序列化进 <paramref name="builder"/>。
		/// </summary>
		/// <param name="depth">当前已进入的容器层数（进入节点时递增）。</param>
		/// <param name="builder">输出目标。</param>
		/// <param name="pretty">是否输出换行与缩进。</param>
		void Stringify(int depth, StringBuilder builder, bool pretty = false) {
			// 深度在进入节点时递增，与 StringifyAsync 的规则保持一致
			depth++;
			switch (mType) {
				case Type.Baked:
					builder.Append(StringValue);
					break;
				case Type.String:
					StringifyString(builder);
					break;
				case Type.Number:
					StringifyNumber(builder);
					break;
				case Type.Object:
					var fieldCount = Count;
					if (fieldCount <= 0) {
						StringifyEmptyObject(builder);
						break;
					}

					BeginStringifyObjectContainer(builder, pretty);
					for (var index = 0; index < fieldCount; index++) {
						var jsonObject = List[index];
						if (jsonObject == null)
							continue;

						if (Keys == null || index >= Keys.Count)
							break;

						var key = Keys[index];
						BeginStringifyObjectField(builder, pretty, depth, key);
						jsonObject.Stringify(depth, builder, pretty);
						EndStringifyObjectField(builder, pretty);
					}

					EndStringifyObjectContainer(builder, pretty, depth);
					break;
				case Type.Array:
					if (Count <= 0) {
						StringifyEmptyArray(builder);
						break;
					}

					BeginStringifyArrayContainer(builder, pretty);
					foreach (var jsonObject in List) {
						if (jsonObject == null)
							continue;

						BeginStringifyArrayElement(builder, pretty, depth);
						jsonObject.Stringify(depth, builder, pretty);
						EndStringifyArrayElement(builder, pretty);
					}

					EndStringifyArrayContainer(builder, pretty, depth);
					break;
				case Type.Bool:
					StringifyBool(builder);
					break;
				case Type.Null:
					StringifyNull(builder);
					break;
			}
		}

		void StringifyString(StringBuilder builder) {
			builder.Append('"').Append(EscapeString(StringValue)).Append('"');
		}

		void BeginStringifyObjectContainer(StringBuilder builder, bool pretty) {
			builder.Append("{");

#if !JSONOBJECT_DISABLE_PRETTY_PRINT
			if (pretty)
				builder.Append(Newline);
#endif
		}

		static void StringifyEmptyObject(StringBuilder builder) {
			builder.Append("{}");
		}

		void BeginStringifyObjectField(StringBuilder builder, bool pretty, int depth, string key) {
#if !JSONOBJECT_DISABLE_PRETTY_PRINT
			if (pretty)
				for (var j = 0; j < depth; j++)
					builder.Append(Tab); //for a bit more readability
#endif

			// 字段名与字符串值使用同一套转义规则，保证 Print → Parse 后 key 一致
			builder.Append('"').Append(EscapeString(key)).Append('"').Append(':');
		}

		void EndStringifyObjectField(StringBuilder builder, bool pretty) {
			builder.Append(",");
#if !JSONOBJECT_DISABLE_PRETTY_PRINT
			if (pretty)
				builder.Append(Newline);
#endif
		}

		void EndStringifyObjectContainer(StringBuilder builder, bool pretty, int depth) {
			// 仅在确实写入过字段（末尾为 ",\r\n"/","）时移除分隔符；空写或键缺失时直接闭合，避免输出非法 JSON
#if !JSONOBJECT_DISABLE_PRETTY_PRINT
			if (pretty) {
				var separatorLength = Newline.Length + 1;
				if (builder.Length >= separatorLength && builder[builder.Length - separatorLength] == ',')
					builder.Length -= separatorLength;
			} else
#endif
			{
				if (builder.Length > 0 && builder[builder.Length - 1] == ',')
					builder.Length--;
			}

#if !JSONOBJECT_DISABLE_PRETTY_PRINT
			if (pretty && Count > 0) {
				builder.Append(Newline);
				for (var j = 0; j < depth - 1; j++)
					builder.Append(Tab);
			}
#endif

			builder.Append("}");
		}

		static void StringifyEmptyArray(StringBuilder builder) {
			builder.Append("[]");
		}

		void BeginStringifyArrayContainer(StringBuilder builder, bool pretty) {
			builder.Append("[");
#if !JSONOBJECT_DISABLE_PRETTY_PRINT
			if (pretty)
				builder.Append(Newline);
#endif

		}

		void BeginStringifyArrayElement(StringBuilder builder, bool pretty, int depth) {
#if !JSONOBJECT_DISABLE_PRETTY_PRINT
			if (pretty)
				for (var j = 0; j < depth; j++)
					builder.Append(Tab); //for a bit more readability
#endif
		}

		void EndStringifyArrayElement(StringBuilder builder, bool pretty) {
			builder.Append(",");
#if !JSONOBJECT_DISABLE_PRETTY_PRINT
			if (pretty)
				builder.Append(Newline);
#endif
		}

		void EndStringifyArrayContainer(StringBuilder builder, bool pretty, int depth) {
			// 同上：仅在确实写入过元素时移除末尾分隔符
#if !JSONOBJECT_DISABLE_PRETTY_PRINT
			if (pretty) {
				var separatorLength = Newline.Length + 1;
				if (builder.Length >= separatorLength && builder[builder.Length - separatorLength] == ',')
					builder.Length -= separatorLength;
			} else
#endif
			{
				if (builder.Length > 0 && builder[builder.Length - 1] == ',')
					builder.Length--;
			}

#if !JSONOBJECT_DISABLE_PRETTY_PRINT
			if (pretty && Count > 0) {
				builder.Append(Newline);
				for (var j = 0; j < depth - 1; j++)
					builder.Append(Tab);
			}
#endif

			builder.Append("]");
		}

		void StringifyNumber(StringBuilder builder) {
			if (IsInteger) {
				builder.Append(LongValue.ToString(CultureInfo.InvariantCulture));
			} else {
#if JSONOBJECT_USE_FLOAT
				if (float.IsNegativeInfinity(floatValue))
					builder.Append(NegativeInfinity);
				else if (float.IsInfinity(floatValue))
					builder.Append(Infinity);
				else if (float.IsNaN(floatValue))
					builder.Append(NaN);
				else
					builder.Append(floatValue.ToString("R", CultureInfo.InvariantCulture));
#else
				if (double.IsNegativeInfinity(DoubleValue))
					builder.Append(NegativeInfinity);
				else if (double.IsInfinity(DoubleValue))
					builder.Append(Infinity);
				else if (double.IsNaN(DoubleValue))
					builder.Append(NaN);
				else
					builder.Append(DoubleValue.ToString("R", CultureInfo.InvariantCulture));
#endif
			}
		}

		void StringifyBool(StringBuilder builder) {
			builder.Append(BoolValue ? True : False);
		}

		static void StringifyNull(StringBuilder builder) {
			builder.Append(Null);
		}

#if USING_UNITY
		/// <summary>
		/// 将 Object/Array 节点转换为 WWWForm 表单；字段名优先取 <see cref="Keys"/>，
		/// 非 Object 或 key 数量不足时回退为数字索引。字段值取子节点的 JSON 文本，String 值会移除全部双引号字符。
		/// </summary>
		public static implicit operator WWWForm(JSONObject jsonObject) {
			var form = new WWWForm();
			var count = jsonObject.Count;
			var list = jsonObject.List;
			var keys = jsonObject.Keys;
			var hasKeys = jsonObject.mType == Type.Object && keys != null && keys.Count >= count;

			for (var i = 0; i < count; i++) {
				var key = hasKeys ? keys[i] : i.ToString(CultureInfo.InvariantCulture);
				var element = list[i];
				var val = element.ToString();
				if (element.mType == Type.String)
					val = val.Replace("\"", "");

				form.AddField(key, val);
			}

			return form;
		}
#endif
		/// <summary>
		/// 按索引访问容器子节点：越界（含负数）返回 null；对非容器或 null List 同样返回 null。
		/// 赋值越界时静默忽略。
		/// </summary>
		public JSONObject this[int index] {
			get {
				return index >= 0 && index < Count ? List[index] : null;
			}
			set {
				if (index >= 0 && index < Count)
					List[index] = value;
			}
		}

		/// <summary>按字段名访问成员：读取等价于 <see cref="GetField(string)"/>，写入等价于 <see cref="SetField(string, JSONObject)"/>。</summary>
		public JSONObject this[string index] {
			get { return GetField(index); }
			set { SetField(index, value); }
		}

		/// <summary>返回紧凑 JSON 字符串（等价于 <see cref="Print(bool)"/>）。</summary>
		public override string ToString() {
			return Print();
		}

		/// <summary>返回 JSON 字符串，可指定是否 pretty。</summary>
		public string ToString(bool pretty) {
			return Print(pretty);
		}

		/// <summary>
		/// 将 Object 节点转为扁平字符串字典；非 Object 返回 null。
		/// 数字优先使用整数形式以避免精度损失，重复 key 以最后一个为准，容器值会被跳过。
		/// </summary>
		public Dictionary<string, string> ToDictionary() {
			if (mType != Type.Object) {
#if USING_UNITY
				Debug.Log
#else
				Debug.WriteLine
#endif
					("Tried to turn non-Object JSONObject into a dictionary");

				return null;
			}

			var result = new Dictionary<string, string>();
			var listCount = Count;
			if (Keys == null || Keys.Count != listCount)
				return result;

			for (var index = 0; index < listCount; index++) {
				var element = List[index];
				if (element == null)
					continue;

				var key = Keys[index];
				switch (element.mType) {
					case Type.String:
						result[key] = element.StringValue;
						break;
					case Type.Number:
						// 整数直接用 LongValue，避免超出 double 精度时丢位；浮点保持 InvariantCulture
						if (element.IsInteger) {
							result[key] = element.LongValue.ToString(CultureInfo.InvariantCulture);
						} else {
#if JSONOBJECT_USE_FLOAT
							result[key] = element.floatValue.ToString(CultureInfo.InvariantCulture);
#else
							result[key] = element.DoubleValue.ToString(CultureInfo.InvariantCulture);
#endif
						}

						break;
					case Type.Bool:
						result[key] = element.BoolValue.ToString(CultureInfo.InvariantCulture);
						break;
					default:
#if USING_UNITY
						Debug.LogWarning
#else
						Debug.WriteLine
#endif
							(string.Format("Omitting object: {0} in dictionary conversion", key));
						break;
				}
			}

			return result;
		}

		/// <summary>非 null 即视为 true（用于判空）。</summary>
		public static implicit operator bool(JSONObject jsonObject) {
			return jsonObject != null;
		}

#if JSONOBJECT_POOLING
		public static void ClearPool() {
			poolingEnabled = false;
			poolingEnabled = true;
			lock (JSONObjectPool) {
				JSONObjectPool.Clear();
			}
		}

		~JSONObject() {
			lock (JSONObjectPool) {
				if (!poolingEnabled || isPooled || JSONObjectPool.Count >= MaxPoolSize)
					return;

				Clear();
				isPooled = true;
				JSONObjectPool.Enqueue(this);
				GC.ReRegisterForFinalize(this);
			}
		}

		public static int GetJSONObjectPoolSize() {
			return JSONObjectPool.Count;
		}

		public static int GetJSONObjectListPoolSize() {
			return JSONObjectListPool.Count;
		}

		public static int GetStringListPoolSize() {
			return StringListPool.Count;
		}
#endif

		/// <summary>创建子节点列表（启用池化时可能来自池）。</summary>
		public static List<JSONObject> CreateJSONObjectList() {
#if JSONOBJECT_POOLING
			lock (JSONObjectListPool) {
				if (JSONObjectListPool.Count > 0)
					return JSONObjectListPool.Dequeue();
			}
#endif

			return new List<JSONObject>();
		}

		/// <summary>创建字段名列表（启用池化时可能来自池）。</summary>
		public static List<string> CreateStringList() {
#if JSONOBJECT_POOLING
			lock (StringListPool) {
				if (StringListPool.Count > 0)
					return StringListPool.Dequeue();
			}
#endif

			return new List<string>();
		}

		IEnumerator IEnumerable.GetEnumerator() {
			return GetEnumerator();
		}

		/// <summary>
		/// 获取枚举器：Array 枚举元素，Object 枚举字段值（不含 key）。
		/// 非 Array/Object 节点不支持枚举，会抛 <see cref="InvalidOperationException"/>。
		/// </summary>
		public JSONObjectEnumerator GetEnumerator() {
			return new JSONObjectEnumerator(this);
		}
	}

}