using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.Help
{
    /// <summary>帮助窗口的轻量统一配色；仅服务本窗口，不构成可扩展主题系统。</summary>
    internal sealed class HelpPalette
    {
        internal readonly Color HeaderBg;
        internal readonly Color HeaderRule;
        internal readonly Color NavBg;
        internal readonly Color NavRule;
        internal readonly Color NavGroupBg;
        internal readonly Color NavGroupRule;
        internal readonly Color NavStripeA;
        internal readonly Color NavStripeB;
        internal readonly Color NavHover;
        internal readonly Color NavSelected;
        internal readonly Color NavSelectedAccent;
        internal readonly Color TabBarBg;
        internal readonly Color TabHoverBg;
        internal readonly Color TabSelectedBg;
        internal readonly Color TabAccent;
        internal readonly Color Separator;
        internal readonly Color NoticeBg;
        internal readonly Color NoticeBorder;
        internal readonly Color NoticeAccent;
        internal readonly Color CodeBg;
        internal readonly Color CodeHeaderBg;
        internal readonly Color CodeBorder;
        internal readonly Color CodeTagBg;
        internal readonly Color Muted;
        internal readonly Color MemberBg;
        internal readonly Color MemberBorder;
        internal readonly Color MemberAccent;
        internal readonly Color MemberHoverBg;
        internal readonly Color MemberHoverBorder;
        internal readonly Color MemberHoverAccent;
        internal readonly Color MemberExpandedBg;
        internal readonly Color MemberExpandedBorder;
        internal readonly Color MemberExpandedAccent;
        internal readonly Color MemberDivider;
        internal readonly Color DetailInsetBg;
        internal readonly Color DetailInsetBorder;
        internal readonly Color ChipBg;
        internal readonly Color ChipHoverBg;
        internal readonly Color ChipSelectedBg;
        internal readonly Color ChipBorder;
        internal readonly Color BadgeBg;

        /// <summary>按当前 Editor 深浅皮肤建立窗口专用配色值。</summary>
        internal HelpPalette(bool dark)
        {
            if (dark)
            {
                HeaderBg = new Color(0.207f, 0.207f, 0.207f);
                HeaderRule = new Color(0.125f, 0.125f, 0.125f);
                NavBg = new Color(0.182f, 0.182f, 0.182f);
                NavRule = new Color(0.130f, 0.130f, 0.130f);
                NavGroupBg = new Color(0.243f, 0.243f, 0.243f);
                NavGroupRule = new Color(0.140f, 0.140f, 0.140f);
                NavStripeA = new Color(0.182f, 0.182f, 0.182f);
                NavStripeB = new Color(0.214f, 0.214f, 0.214f);
                NavHover = new Color(0.270f, 0.270f, 0.270f);
                NavSelected = new Color(0.198f, 0.340f, 0.478f);
                NavSelectedAccent = new Color(0.420f, 0.655f, 0.920f);
                TabBarBg = new Color(0.182f, 0.182f, 0.182f);
                TabHoverBg = new Color(0.262f, 0.262f, 0.262f);
                TabSelectedBg = new Color(0.238f, 0.238f, 0.238f);
                TabAccent = new Color(0.380f, 0.620f, 0.880f);
                Separator = new Color(0.280f, 0.280f, 0.280f);
                NoticeBg = new Color(0.206f, 0.228f, 0.258f);
                NoticeBorder = new Color(0.300f, 0.420f, 0.550f);
                NoticeAccent = new Color(0.380f, 0.620f, 0.880f);
                CodeBg = new Color(0.125f, 0.135f, 0.145f);
                CodeHeaderBg = new Color(0.175f, 0.185f, 0.196f);
                CodeBorder = new Color(0.270f, 0.280f, 0.290f);
                CodeTagBg = new Color(0.252f, 0.266f, 0.280f);
                Muted = new Color(0.630f, 0.640f, 0.660f);
                MemberBg = new Color(0.232f, 0.235f, 0.242f);
                MemberBorder = new Color(0.300f, 0.304f, 0.314f);
                MemberAccent = new Color(0.400f, 0.430f, 0.470f);
                MemberHoverBg = new Color(0.252f, 0.256f, 0.264f);
                MemberHoverBorder = new Color(0.360f, 0.368f, 0.382f);
                MemberHoverAccent = new Color(0.470f, 0.520f, 0.590f);
                MemberExpandedBg = new Color(0.246f, 0.256f, 0.278f);
                MemberExpandedBorder = new Color(0.390f, 0.430f, 0.490f);
                MemberExpandedAccent = new Color(0.430f, 0.580f, 0.780f);
                MemberDivider = new Color(0.370f, 0.378f, 0.395f);
                DetailInsetBg = new Color(0.192f, 0.198f, 0.214f);
                DetailInsetBorder = new Color(0.300f, 0.310f, 0.335f);
                ChipBg = new Color(0.248f, 0.248f, 0.248f);
                ChipHoverBg = new Color(0.282f, 0.282f, 0.282f);
                ChipSelectedBg = new Color(0.198f, 0.340f, 0.478f);
                ChipBorder = new Color(0.330f, 0.330f, 0.330f);
                BadgeBg = new Color(0.258f, 0.264f, 0.276f);
            }
            else
            {
                HeaderBg = new Color(0.855f, 0.855f, 0.855f);
                HeaderRule = new Color(0.720f, 0.720f, 0.720f);
                NavBg = new Color(0.900f, 0.900f, 0.900f);
                NavRule = new Color(0.780f, 0.780f, 0.780f);
                NavGroupBg = new Color(0.845f, 0.845f, 0.845f);
                NavGroupRule = new Color(0.755f, 0.755f, 0.755f);
                NavStripeA = new Color(0.900f, 0.900f, 0.900f);
                NavStripeB = new Color(0.868f, 0.868f, 0.868f);
                NavHover = new Color(0.855f, 0.855f, 0.855f);
                NavSelected = new Color(0.730f, 0.840f, 0.940f);
                NavSelectedAccent = new Color(0.220f, 0.470f, 0.780f);
                TabBarBg = new Color(0.860f, 0.860f, 0.860f);
                TabHoverBg = new Color(0.805f, 0.805f, 0.805f);
                TabSelectedBg = new Color(0.925f, 0.925f, 0.925f);
                TabAccent = new Color(0.240f, 0.480f, 0.780f);
                Separator = new Color(0.720f, 0.720f, 0.720f);
                NoticeBg = new Color(0.915f, 0.940f, 0.972f);
                NoticeBorder = new Color(0.550f, 0.680f, 0.800f);
                NoticeAccent = new Color(0.240f, 0.480f, 0.780f);
                CodeBg = new Color(0.955f, 0.965f, 0.975f);
                CodeHeaderBg = new Color(0.900f, 0.910f, 0.923f);
                CodeBorder = new Color(0.780f, 0.780f, 0.780f);
                CodeTagBg = new Color(0.845f, 0.860f, 0.877f);
                Muted = new Color(0.400f, 0.420f, 0.450f);
                MemberBg = new Color(0.822f, 0.824f, 0.830f);
                MemberBorder = new Color(0.700f, 0.703f, 0.712f);
                MemberAccent = new Color(0.600f, 0.625f, 0.660f);
                MemberHoverBg = new Color(0.845f, 0.848f, 0.855f);
                MemberHoverBorder = new Color(0.630f, 0.638f, 0.652f);
                MemberHoverAccent = new Color(0.520f, 0.560f, 0.620f);
                MemberExpandedBg = new Color(0.850f, 0.862f, 0.882f);
                MemberExpandedBorder = new Color(0.560f, 0.600f, 0.660f);
                MemberExpandedAccent = new Color(0.360f, 0.490f, 0.660f);
                MemberDivider = new Color(0.620f, 0.625f, 0.640f);
                DetailInsetBg = new Color(0.798f, 0.806f, 0.824f);
                DetailInsetBorder = new Color(0.640f, 0.645f, 0.665f);
                ChipBg = new Color(0.888f, 0.888f, 0.888f);
                ChipHoverBg = new Color(0.850f, 0.850f, 0.850f);
                ChipSelectedBg = new Color(0.730f, 0.840f, 0.940f);
                ChipBorder = new Color(0.790f, 0.790f, 0.790f);
                BadgeBg = new Color(0.888f, 0.892f, 0.898f);
            }
        }

        /// <summary>目录行的交替条纹底色；选中与悬停状态由调用方叠加。</summary>
        internal void DrawStripe(Rect rect, int index) =>
            EditorGUI.DrawRect(rect, (index & 1) == 0 ? NavStripeA : NavStripeB);
    }

    /// <summary>帮助窗口共享的少量绘制助手，避免各页面重复写边框与分隔线。</summary>
    internal static class HelpDraw
    {
        /// <summary>绘制 1 像素四边描边。</summary>
        internal static void Outline(Rect rect, Color color)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y + 1, 1, Mathf.Max(0, rect.height - 2)), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1, rect.y + 1, 1, Mathf.Max(0, rect.height - 2)), color);
        }

        /// <summary>绘制 1 像素水平分隔线。</summary>
        internal static void Rule(Rect rect, Color color) =>
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1), color);
    }

    /// <summary>九宫格小纹理生成器；只服务帮助窗口的成员信息块，不作为通用图片管线。</summary>
    internal static class HelpTexture
    {
        /// <summary>生成不带左侧强调线的圆角内嵌块纹理；用于成员详情区。</summary>
        /// <returns>由调用窗口持有并在重建样式或停用时销毁的临时纹理。</returns>
        internal static Texture2D RoundedBox(int size, int radius, Color fill, Color border) =>
            RoundedBox(size, radius, fill, border, fill);

        /// <summary>生成圆角信息块纹理：细边框、底色、左侧 2 像素低对比强调线；用 style.border 九宫格拉伸。</summary>
        /// <returns>每次调用新建的纹理；HideAndDontSave 不代替调用方的显式销毁责任。</returns>
        internal static Texture2D RoundedBox(int size, int radius, Color fill, Color border, Color accent)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp
            };
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // 圆角矩形的有符号距离：直边处约 1 像素，用 coverage 做外缘抗锯齿。
                    var centerX = Mathf.Clamp(x + 0.5f, radius, size - radius);
                    var centerY = Mathf.Clamp(y + 0.5f, radius, size - radius);
                    var dx = x + 0.5f - centerX;
                    var dy = y + 0.5f - centerY;
                    var distance = Mathf.Sqrt(dx * dx + dy * dy) - radius;
                    var coverage = Mathf.Clamp01(0.5f - distance);
                    if (coverage <= 0f)
                    {
                        texture.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                        continue;
                    }
                    var color = distance > -1f ? border : x >= 1 && x <= 2 ? accent : fill;
                    color.a *= coverage;
                    texture.SetPixel(x, y, color);
                }
            }

            texture.Apply();
            return texture;
        }
    }
}
