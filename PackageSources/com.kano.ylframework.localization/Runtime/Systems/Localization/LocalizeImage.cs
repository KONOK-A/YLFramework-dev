using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Localization
{
    /// <summary>将当前本地化图片条目交给同物体的 ResourceImage，由后者持有图片请求和租约。</summary>
    [RequireComponent(typeof(ResourceImage))]
    [AddComponentMenu("YLFramework/Localization/Localize Image")]
    public class LocalizeImage : LocalizeBase
    {
        private ResourceImage _loader;
        /// <summary>按条目类型传入目录地址或图片 URL，缺失及非图片条目则清空加载器输入与显示责任。</summary>
        protected override void OnLanguageValueChange()
        {
            if (!_loader)
            {
                _loader = GetComponent<ResourceImage>();
            }

            if (!_loader)
            {
                return;
            }

            var entry = LocalizationManager.GetLanguageInfo(Id);
            if (entry == null)
            {
                _loader.Clear();
                return;
            }
            if (entry.type == MultiLanguageType.Image && entry.image != null)
            {
                _loader.SetImage(new ResourceAddress(entry.image.projectName, entry.image.imageName,
                    string.IsNullOrEmpty(entry.image.subAsset) ? null : entry.image.subAsset));
            }
            else if (entry.type == MultiLanguageType.NetworkImage)
            {
                _loader.SetImage(entry.url);
            }
            else
            {
                _loader.Clear();
            }
        }

        /// <summary>解除本地化订阅，并清空已缓存的图片加载器以归还其显示责任。</summary>
        protected override void OnDisable()
        {
            base.OnDisable();
            if (_loader)
            {
                _loader.Clear();
            }
        }
    }
}
