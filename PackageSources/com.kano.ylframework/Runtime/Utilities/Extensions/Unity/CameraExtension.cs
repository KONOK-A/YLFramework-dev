using UnityEngine;

namespace YLFramework.Extensions
{
    /// <summary>
    /// Camera 扩展：相机画面捕获。
    /// </summary>
    public static class CameraExtension
    {
        /// <summary>
        /// 按屏幕尺寸离屏渲染相机并读取 rect 区域，区域必须落在 <see cref="Screen.width"/> × <see cref="Screen.height"/> 范围内。
        /// 渲染前替换相机的 targetTexture，通过 finally 恢复原 targetTexture 与 <see cref="RenderTexture.active"/>。
        /// 临时 RenderTexture 也在 finally 中销毁。
        /// 已创建的 Texture2D 没有异常路径清理，成功返回后由调用方负责销毁。
        /// </summary>
        /// <param name="camera">目标相机。</param>
        /// <param name="rect">截取区域，单位为像素，原点位于渲染目标左下角；本方法不裁剪或校验区域。</param>
        /// <returns>RGB24、无 mipmap 的 Texture2D，归调用方所有，由调用方负责销毁。</returns>
        public static Texture2D CaptureCamera(this Camera camera, Rect rect)
        {
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;

            var renderTexture = new RenderTexture(Screen.width, Screen.height, 0);
            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();

                RenderTexture.active = renderTexture;

                var screenShot = new Texture2D((int)rect.width, (int)rect.height, TextureFormat.RGB24, false);
                screenShot.ReadPixels(rect, 0, 0);
                screenShot.Apply();
                return screenShot;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;

                if (Application.isPlaying)
                {
                    Object.Destroy(renderTexture);
                }
                else
                {
                    Object.DestroyImmediate(renderTexture);
                }
            }
        }
    }
}
