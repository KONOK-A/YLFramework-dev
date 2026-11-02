using UnityEngine;

namespace YLFramework.Utilities.Json {
	// ReSharper disable once PartialTypeWithSinglePart
	/// <summary>
	/// Unity 常用类型与 JSONObject 的互转扩展（字段名：x/y/z/w、m00…m33、r/g/b/a 等）。
	/// </summary>
	public static partial class JSONTemplates {
		/*
		 * Vector2
		 */
		/// <summary>读取 Vector2：字段 x/y，缺失时为 0；jsonObject 为 null 时返回零向量。</summary>
		public static Vector2 ToVector2(this JSONObject jsonObject) {
			if (jsonObject == null)
				return default;

			var x = jsonObject["x"] ? jsonObject["x"].FloatValue : 0;
			var y = jsonObject["y"] ? jsonObject["y"].FloatValue : 0;
			return new Vector2(x, y);
		}

		/// <summary>写入 Vector2：字段 x/y，值为 0 的分量不写出。</summary>
		public static JSONObject FromVector2(this Vector2 vector) {
			var jsonObject = JSONObject.EmptyObject;
			if (vector.x != 0) jsonObject.AddField("x", vector.x);
			if (vector.y != 0) jsonObject.AddField("y", vector.y);
			return jsonObject;
		}

		/// <summary>等价于 <see cref="FromVector2"/>。</summary>
		public static JSONObject ToJson(this Vector2 vector) {
			return vector.FromVector2();
		}

		/*
		 * Vector3
		 */
		/// <summary>写入 Vector3：字段 x/y/z，值为 0 的分量不写出。</summary>
		public static JSONObject FromVector3(this Vector3 vector) {
			var jsonObject = JSONObject.EmptyObject;
			if (vector.x != 0) jsonObject.AddField("x", vector.x);
			if (vector.y != 0) jsonObject.AddField("y", vector.y);
			if (vector.z != 0) jsonObject.AddField("z", vector.z);
			return jsonObject;
		}

		/// <summary>读取 Vector3：字段 x/y/z，缺失时为 0；jsonObject 为 null 时返回零向量。</summary>
		public static Vector3 ToVector3(this JSONObject jsonObject) {
			if (jsonObject == null)
				return default;

			var x = jsonObject["x"] ? jsonObject["x"].FloatValue : 0;
			var y = jsonObject["y"] ? jsonObject["y"].FloatValue : 0;
			var z = jsonObject["z"] ? jsonObject["z"].FloatValue : 0;
			return new Vector3(x, y, z);
		}

		/// <summary>等价于 <see cref="FromVector3"/>。</summary>
		public static JSONObject ToJson(this Vector3 vector) {
			return vector.FromVector3();
		}

		/*
		 * Vector4
		 */
		/// <summary>写入 Vector4：字段 x/y/z/w，值为 0 的分量不写出。</summary>
		public static JSONObject FromVector4(this Vector4 vector) {
			var jsonObject = JSONObject.EmptyObject;
			if (vector.x != 0) jsonObject.AddField("x", vector.x);
			if (vector.y != 0) jsonObject.AddField("y", vector.y);
			if (vector.z != 0) jsonObject.AddField("z", vector.z);
			if (vector.w != 0) jsonObject.AddField("w", vector.w);
			return jsonObject;
		}

		/// <summary>读取 Vector4：字段 x/y/z/w，缺失时为 0；jsonObject 为 null 时返回零向量。</summary>
		public static Vector4 ToVector4(this JSONObject jsonObject) {
			if (jsonObject == null)
				return default;

			var x = jsonObject["x"] ? jsonObject["x"].FloatValue : 0;
			var y = jsonObject["y"] ? jsonObject["y"].FloatValue : 0;
			var z = jsonObject["z"] ? jsonObject["z"].FloatValue : 0;
			var w = jsonObject["w"] ? jsonObject["w"].FloatValue : 0;
			return new Vector4(x, y, z, w);
		}

		/// <summary>等价于 <see cref="FromVector4"/>。</summary>
		public static JSONObject ToJson(this Vector4 vector) {
			return vector.FromVector4();
		}

		/*
		 * Matrix4x4
		 */
		// ReSharper disable once InconsistentNaming
		/// <summary>写入 Matrix4x4：字段 m00…m33，值为 0 的分量不写出。</summary>
		public static JSONObject FromMatrix4x4(this Matrix4x4 matrix) {
			var jsonObject = JSONObject.EmptyObject;
			if (matrix.m00 != 0) jsonObject.AddField("m00", matrix.m00);
			if (matrix.m01 != 0) jsonObject.AddField("m01", matrix.m01);
			if (matrix.m02 != 0) jsonObject.AddField("m02", matrix.m02);
			if (matrix.m03 != 0) jsonObject.AddField("m03", matrix.m03);
			if (matrix.m10 != 0) jsonObject.AddField("m10", matrix.m10);
			if (matrix.m11 != 0) jsonObject.AddField("m11", matrix.m11);
			if (matrix.m12 != 0) jsonObject.AddField("m12", matrix.m12);
			if (matrix.m13 != 0) jsonObject.AddField("m13", matrix.m13);
			if (matrix.m20 != 0) jsonObject.AddField("m20", matrix.m20);
			if (matrix.m21 != 0) jsonObject.AddField("m21", matrix.m21);
			if (matrix.m22 != 0) jsonObject.AddField("m22", matrix.m22);
			if (matrix.m23 != 0) jsonObject.AddField("m23", matrix.m23);
			if (matrix.m30 != 0) jsonObject.AddField("m30", matrix.m30);
			if (matrix.m31 != 0) jsonObject.AddField("m31", matrix.m31);
			if (matrix.m32 != 0) jsonObject.AddField("m32", matrix.m32);
			if (matrix.m33 != 0) jsonObject.AddField("m33", matrix.m33);
			return jsonObject;
		}

		// ReSharper disable once InconsistentNaming
		/// <summary>读取 Matrix4x4：字段 m00…m33，缺失时为 0；jsonObject 为 null 时返回零矩阵。</summary>
		public static Matrix4x4 ToMatrix4x4(this JSONObject jsonObject) {
			if (jsonObject == null)
				return default;

			var matrix = new Matrix4x4();
			if (jsonObject["m00"]) matrix.m00 = jsonObject["m00"].FloatValue;
			if (jsonObject["m01"]) matrix.m01 = jsonObject["m01"].FloatValue;
			if (jsonObject["m02"]) matrix.m02 = jsonObject["m02"].FloatValue;
			if (jsonObject["m03"]) matrix.m03 = jsonObject["m03"].FloatValue;
			if (jsonObject["m10"]) matrix.m10 = jsonObject["m10"].FloatValue;
			if (jsonObject["m11"]) matrix.m11 = jsonObject["m11"].FloatValue;
			if (jsonObject["m12"]) matrix.m12 = jsonObject["m12"].FloatValue;
			if (jsonObject["m13"]) matrix.m13 = jsonObject["m13"].FloatValue;
			if (jsonObject["m20"]) matrix.m20 = jsonObject["m20"].FloatValue;
			if (jsonObject["m21"]) matrix.m21 = jsonObject["m21"].FloatValue;
			if (jsonObject["m22"]) matrix.m22 = jsonObject["m22"].FloatValue;
			if (jsonObject["m23"]) matrix.m23 = jsonObject["m23"].FloatValue;
			if (jsonObject["m30"]) matrix.m30 = jsonObject["m30"].FloatValue;
			if (jsonObject["m31"]) matrix.m31 = jsonObject["m31"].FloatValue;
			if (jsonObject["m32"]) matrix.m32 = jsonObject["m32"].FloatValue;
			if (jsonObject["m33"]) matrix.m33 = jsonObject["m33"].FloatValue;
			return matrix;
		}

		/// <summary>等价于 <see cref="FromMatrix4x4"/>。</summary>
		public static JSONObject ToJson(this Matrix4x4 matrix) {
			return matrix.FromMatrix4x4();
		}

		/*
		 * Quaternion
		 */
		/// <summary>写入 Quaternion：字段 x/y/z/w，值为 0 的分量不写出。</summary>
		public static JSONObject FromQuaternion(this Quaternion quaternion) {
			var jsonObject = JSONObject.EmptyObject;
			if (quaternion.w != 0) jsonObject.AddField("w", quaternion.w);
			if (quaternion.x != 0) jsonObject.AddField("x", quaternion.x);
			if (quaternion.y != 0) jsonObject.AddField("y", quaternion.y);
			if (quaternion.z != 0) jsonObject.AddField("z", quaternion.z);
			return jsonObject;
		}

		/// <summary>读取 Quaternion：字段 x/y/z/w，缺失时为 0；jsonObject 为 null 时返回 (0,0,0,0)。</summary>
		public static Quaternion ToQuaternion(this JSONObject jsonObject) {
			if (jsonObject == null)
				return default;

			var x = jsonObject["x"] ? jsonObject["x"].FloatValue : 0;
			var y = jsonObject["y"] ? jsonObject["y"].FloatValue : 0;
			var z = jsonObject["z"] ? jsonObject["z"].FloatValue : 0;
			var w = jsonObject["w"] ? jsonObject["w"].FloatValue : 0;
			return new Quaternion(x, y, z, w);
		}

		/// <summary>等价于 <see cref="FromQuaternion"/>。</summary>
		public static JSONObject ToJson(this Quaternion quaternion) {
			return quaternion.FromQuaternion();
		}

		/*
		 * Color
		 */
		/// <summary>写入 Color：字段 r/g/b/a，值为 0 的分量不写出。</summary>
		public static JSONObject FromColor(this Color color) {
			var jsonObject = JSONObject.EmptyObject;
			if (color.r != 0) jsonObject.AddField("r", color.r);
			if (color.g != 0) jsonObject.AddField("g", color.g);
			if (color.b != 0) jsonObject.AddField("b", color.b);
			if (color.a != 0) jsonObject.AddField("a", color.a);
			return jsonObject;
		}

		/// <summary>读取 Color：字段 r/g/b/a，缺失时为 0；jsonObject 为 null 时返回 (0,0,0,0)。</summary>
		public static Color ToColor(this JSONObject jsonObject) {
			if (jsonObject == null)
				return default;

			var color = new Color();
			var r = jsonObject["r"];
			if (r) color.r = r.FloatValue;
			var g = jsonObject["g"];
			if (g) color.g = g.FloatValue;
			var b = jsonObject["b"];
			if (b) color.b = b.FloatValue;
			var a = jsonObject["a"];
			if (a) color.a = a.FloatValue;
			return color;
		}

		/// <summary>等价于 <see cref="FromColor"/>。</summary>
		public static JSONObject ToJson(this Color color) {
			return color.FromColor();
		}

		/*
		 * Layer Mask
		 */
		/// <summary>写入 LayerMask：字段 value（始终写出，包括 0）。</summary>
		public static JSONObject FromLayerMask(this LayerMask layerMask) {
			var jsonObject = JSONObject.EmptyObject;
			jsonObject.AddField("value", layerMask.value);
			return jsonObject;
		}

		/// <summary>读取 LayerMask：字段 value，缺失时为 0；jsonObject 为 null 时返回空 mask。</summary>
		public static LayerMask ToLayerMask(this JSONObject jsonObject) {
			if (jsonObject == null)
				return default;

			var value = jsonObject["value"];
			return new LayerMask { value = value ? value.IntValue : 0 };
		}

		/// <summary>等价于 <see cref="FromLayerMask"/>。</summary>
		public static JSONObject ToJson(this LayerMask layerMask) {
			return layerMask.FromLayerMask();
		}

		/*
		 * Rect
		 */
		/// <summary>写入 Rect：字段 x/y/height/width，值为 0 的分量不写出。</summary>
		public static JSONObject FromRect(this Rect rect) {
			var jsonObject = JSONObject.EmptyObject;
			if (rect.x != 0) jsonObject.AddField("x", rect.x);
			if (rect.y != 0) jsonObject.AddField("y", rect.y);
			if (rect.height != 0) jsonObject.AddField("height", rect.height);
			if (rect.width != 0) jsonObject.AddField("width", rect.width);
			return jsonObject;
		}

		/// <summary>读取 Rect：字段 x/y/height/width，缺失时为 0；jsonObject 为 null 时返回零 Rect。</summary>
		public static Rect ToRect(this JSONObject jsonObject) {
			if (jsonObject == null)
				return default;

			var rect = new Rect();
			var x = jsonObject["x"];
			if (x) rect.x = x.FloatValue;
			var y = jsonObject["y"];
			if (y) rect.y = y.FloatValue;
			var height = jsonObject["height"];
			if (height) rect.height = height.FloatValue;
			var width = jsonObject["width"];
			if (width) rect.width = width.FloatValue;
			return rect;
		}

		/// <summary>等价于 <see cref="FromRect"/>。</summary>
		public static JSONObject ToJson(this Rect rect) {
			return rect.FromRect();
		}

		/*
		* Rect Offset
		 */
		/// <summary>写入 RectOffset：字段 bottom/left/right/top，值为 0 的分量不写出。</summary>
		public static JSONObject FromRectOffset(this RectOffset rectOffset) {
			var jsonObject = JSONObject.EmptyObject;
			if (rectOffset.bottom != 0) jsonObject.AddField("bottom", rectOffset.bottom);
			if (rectOffset.left != 0) jsonObject.AddField("left", rectOffset.left);
			if (rectOffset.right != 0) jsonObject.AddField("right", rectOffset.right);
			if (rectOffset.top != 0) jsonObject.AddField("top", rectOffset.top);
			return jsonObject;
		}

		/// <summary>读取 RectOffset：字段 bottom/left/right/top，缺失时为 0；jsonObject 为 null 时返回零偏移。</summary>
		public static RectOffset ToRectOffset(this JSONObject jsonObject) {
			// RectOffset 是 class，default 为 null，这里统一返回零值实例
			var rectOffset = new RectOffset();
			if (jsonObject == null)
				return rectOffset;

			var bottom = jsonObject["bottom"];
			if (bottom) rectOffset.bottom = bottom.IntValue;
			var left = jsonObject["left"];
			if (left) rectOffset.left = left.IntValue;
			var right = jsonObject["right"];
			if (right) rectOffset.right = right.IntValue;
			var top = jsonObject["top"];
			if (top) rectOffset.top = top.IntValue;
			return rectOffset;
		}

		/// <summary>等价于 <see cref="FromRectOffset"/>。</summary>
		public static JSONObject ToJson(this RectOffset rectOffset) {
			return rectOffset.FromRectOffset();
		}
	}
}
