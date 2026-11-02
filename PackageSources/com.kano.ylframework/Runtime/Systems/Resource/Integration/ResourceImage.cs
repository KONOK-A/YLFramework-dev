using System;
using UnityEngine;
using UnityEngine.UI;

namespace YLFramework.Resource
{
    /// <summary>Unity UI 图片消费者。只拥有自己的请求、Lease 与临时 Sprite，不拥有目标上原有的 Texture/Sprite。</summary>
    [AddComponentMenu("YLFramework/Resource/Resource Image")]
    [DisallowMultipleComponent]
    public sealed class ResourceImage : MonoBehaviour
    {
        /// <summary>组件输入的解释方式，明确区分直接图片定位、目录资产与图集克隆。</summary>
        public enum ImageSource
        {
            /// <summary>完整本地路径或 HTTP(S) URL，由图片来源解码为 Texture2D。</summary>
            Location,
            /// <summary>项目目录中的 Texture2D 或 Sprite 逻辑地址，可携带显式子资产键。</summary>
            Catalog,
            /// <summary>先取得主 SpriteAtlas 租约，再按精确名称取得组件自有的 Sprite 克隆。</summary>
            Atlas
        }

        [SerializeField, Tooltip("支持 Image 或 RawImage；留空时使用同一 GameObject 的组件。")]
        private Graphic target;
        [SerializeField] private ImageSource source;
        [SerializeField, Tooltip("完整 HTTP(S) URL 或绝对本地 PNG/JPEG 路径；保留大小写和 query。")]
        private string location;
        [SerializeField] private string projectId;
        [SerializeField] private string address;
        [SerializeField] private string subAsset;
        [SerializeField, Tooltip("显式SpriteAtlas中唯一的Sprite名称；克隆由此组件拥有，Atlas仍由原AssetLease保活。")]
        private string spriteName;
        [SerializeField] private bool loadOnEnable = true;
        [SerializeField] private bool setNativeSize;

        private ResourceRequest<AssetLease<Texture2D>> _imageRequest;
        private ResourceRequest<AssetLease<UnityEngine.Object>> _catalogRequest;
        private IDisposable _lease;
        private Graphic _displayTarget;
        private UnityEngine.Object _previous;
        private UnityEngine.Object _displayed;
        private UnityEngine.Object _leasedAsset;
        private Sprite _ownedSprite;
        private int _generation;
        private bool _hasDisplay;

        /// <summary>最近一次加载或显示检查的状态；状态成功不替代后续 Unity 对象存活检查。</summary>
        public ResourceRequestStatus Status { get; private set; } = ResourceRequestStatus.Canceled;
        /// <summary>最近一次加载或显示失败原因；刷新开始或清空后清除。</summary>
        public ResourceException Error
        {
            get; private set;
        }
        /// <summary>当前是否仍持有尚未完成的图片或目录请求。</summary>
        public bool IsLoading => _imageRequest != null && !_imageRequest.IsDone || _catalogRequest != null && !_catalogRequest.IsDone;
        /// <summary>当前加载或显示结果的通知；Clear 与停用不主动通知，观察者异常分别记录。</summary>
        public event Action<ResourceImage> Completed;

        /// <summary>目标 Image 或 RawImage；变更会先归还原显示责任并刷新，空值时刷新尝试同物体组件。</summary>
        public Graphic Target
        {
            get => target;
            set
            {
                if (value && !(value is Image) && !(value is RawImage))
                {
                    throw new ArgumentException("ResourceImage requires an Image or RawImage.", nameof(value));
                }

                if (target == value)
                {
                    return;
                }

                target = value;
                Refresh();
            }
        }

        /// <summary>是否在下次成功应用图片时同步原始尺寸；设置属性本身不立即修改目标。</summary>
        public bool SetNativeSize
        {
            get => setNativeSize; set => setNativeSize = value;
        }
        private bool HasInput => source == ImageSource.Location ? !string.IsNullOrEmpty(location) : !string.IsNullOrEmpty(address);

        /// <summary>修改目标输入并发起新一代请求；停用时只记录输入，由 Load On Enable 或 Refresh 取得。</summary>
        public void SetImage(string imageLocation)
        {
            source = ImageSource.Location;
            location = imageLocation;
            Refresh();
        }

        /// <summary>正式 Catalog 的 Texture2D 或 Sprite 地址；借用资产仅由对应 Lease 保活。</summary>
        public void SetImage(ResourceAddress resourceAddress)
        {
            if (!resourceAddress.IsValid)
            {
                throw new ArgumentException("A valid image resource address is required.", nameof(resourceAddress));
            }

            source = ImageSource.Catalog;
            projectId = resourceAddress.ProjectId;
            address = resourceAddress.Path;
            subAsset = resourceAddress.SubAsset;
            Refresh();
        }

        /// <summary>显式取得Atlas中的Sprite克隆，仅供Image使用；不订阅全局atlasRequested或借用其他消费者的克隆。</summary>
        public void SetAtlasSprite(ResourceAddress atlasAddress, string name)
        {
            if (!atlasAddress.IsValid || atlasAddress.SubAsset != null)
            {
                throw new ArgumentException("An explicit main SpriteAtlas address is required.", nameof(atlasAddress));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A unique Sprite name is required.", nameof(name));
            }

            source = ImageSource.Atlas;
            projectId = atlasAddress.ProjectId;
            address = atlasAddress.Path;
            subAsset = null;
            spriteName = name;
            Refresh();
        }

        /// <summary>重新取得当前输入。远端内容换代使用不同版本 URL，不在组件内建立下载缓存或第二资源权威。</summary>
        public void Refresh()
        {
            var accepted = _generation + 1;
            try
            {
                ReleaseDisplay();
            }
            catch (Exception error)
            {
                if (_generation == accepted)
                {
                    Fail(new ResourceException(ResourceError.LoadFailed, "The previous image display could not be restored.", error));
                }

                return;
            }
            if (_generation != accepted)
            {
                return;
            }

            if (!isActiveAndEnabled || !HasInput)
            {
                Status = ResourceRequestStatus.Canceled;
                Error = null;
                return;
            }
            if (!target)
            {
                target = GetComponent<Graphic>();
            }

            if (!(target is Image) && !(target is RawImage))
            {
                Fail(new ResourceException(ResourceError.InvalidConfiguration, "ResourceImage requires an Image or RawImage target."));
                return;
            }
            if (source == ImageSource.Atlas && (!(target is Image) || string.IsNullOrWhiteSpace(spriteName)))
            {
                Fail(new ResourceException(ResourceError.InvalidConfiguration, "Atlas sprites require an Image target and a unique Sprite name."));
                return;
            }
            var system = ResourceSystem.Current;
            if (system == null || !system.IsAvailable)
            {
                Fail(new ResourceException(ResourceError.SessionUnavailable, "Initialize ResourceSystem before loading a ResourceImage."));
                return;
            }
            Status = ResourceRequestStatus.Pending;
            Error = null;
            var acceptedTarget = target;
            try
            {
                if (source == ImageSource.Location)
                {
                    _imageRequest = system.AcquireImageAsync(location);
                    _imageRequest.Completed += value => Apply(value, accepted, acceptedTarget);
                }
                else
                {
                    _catalogRequest = system.AcquireAsync<UnityEngine.Object>(new ResourceAddress(projectId, address,
                        source == ImageSource.Catalog && !string.IsNullOrEmpty(subAsset) ? subAsset : null));
                    _catalogRequest.Completed += value => Apply(value, accepted, acceptedTarget);
                }
            }
            catch (ArgumentException error)
            {
                Fail(new ResourceException(ResourceError.InvalidAddress, "Invalid image resource address.", error));
            }
        }

        /// <summary>清空输入并归还当前请求、租约和自有 Sprite；仅在目标仍显示本组件内容时恢复旧值。</summary>
        public void Clear()
        {
            location = null;
            address = null;
            var accepted = _generation + 1;
            try
            {
                ReleaseDisplay();
            }
            finally
            {
                if (_generation == accepted)
                {
                    Error = null;
                    Status = ResourceRequestStatus.Canceled;
                }
            }
        }

        /// <summary>只接受当前代且目标未变的完成结果，交割租约并应用图片；失配或失败时归还本次责任。</summary>
        private void Apply<T>(ResourceRequest<AssetLease<T>> request, int accepted, Graphic acceptedTarget) where T : UnityEngine.Object
        {
            if (accepted != _generation || !isActiveAndEnabled)
            {
                request.Dispose();
                return;
            }
            if (!acceptedTarget || target != acceptedTarget)
            {
                try
                {
                    FailDisplay(accepted, new ResourceException(ResourceError.LoadFailed, "The image target changed or was destroyed before completion."));
                }
                finally
                {
                    request.Dispose();
                }
                return;
            }
            if (ReferenceEquals(_imageRequest, request))
            {
                _imageRequest = null;
            }

            if (ReferenceEquals(_catalogRequest, request))
            {
                _catalogRequest = null;
            }

            AssetLease<T> acquired = null;
            try
            {
                if (request.Status != ResourceRequestStatus.Succeeded)
                {
                    Status = request.Status;
                    Error = request.Error;
                    Notify();
                    return;
                }
                acquired = request.TakeResult();
                acquired.OwnerLabel = "ResourceImage: " + acceptedTarget.name + " (" + acceptedTarget.GetInstanceID() + ")";
                var asset = acquired.Asset;
                _previous = ReadTarget(acceptedTarget);
                _displayTarget = acceptedTarget;
                if (source == ImageSource.Atlas)
                {
                    if (!(asset is UnityEngine.U2D.SpriteAtlas atlas))
                    {
                        throw new ResourceException(ResourceError.TypeMismatch, "The explicit Atlas address must declare a SpriteAtlas.");
                    }

                    _ownedSprite = atlas.GetSprite(spriteName);
                    if (!_ownedSprite)
                    {
                        throw new ResourceException(ResourceError.AddressNotFound, "SpriteAtlas does not contain Sprite: " + spriteName);
                    }

                    if (!_ownedSprite.texture)
                    {
                        throw new ResourceException(ResourceError.LoadFailed, "The explicit SpriteAtlas has no packed Sprite texture.");
                    }

                    _displayed = _ownedSprite;
                }
                else if (acceptedTarget is RawImage)
                {
                    if (!(asset is Texture texture))
                    {
                        throw new ResourceException(ResourceError.TypeMismatch, "RawImage requires a Texture address, not a Sprite.");
                    }

                    _displayed = texture;
                }
                else
                {
                    if (asset is Sprite sprite)
                    {
                        _displayed = sprite;
                    }
                    else if (asset is Texture2D texture)
                    {
                        _ownedSprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), Vector2.one * .5f, 100, 0, SpriteMeshType.FullRect);
                        _displayed = _ownedSprite;
                    }
                    else
                    {
                        throw new ResourceException(ResourceError.TypeMismatch, "Image requires a Texture2D or Sprite address.");
                    }
                }
                // UI setter/尺寸回调可能停用、换代或销毁组件，必须先登记其可归还的真实责任。
                _hasDisplay = true;
                _leasedAsset = asset;
                _lease = acquired;
                acquired = null;
                if (acceptedTarget is RawImage rawTarget)
                {
                    rawTarget.texture = _displayed as Texture;
                    if (setNativeSize)
                    {
                        rawTarget.SetNativeSize();
                    }
                }
                else
                {
                    var image = (Image)acceptedTarget;
                    image.sprite = _displayed as Sprite;
                    if (setNativeSize)
                    {
                        image.SetNativeSize();
                    }
                }
                if (accepted != _generation || !isActiveAndEnabled || !acceptedTarget)
                {
                    return;
                }

                Status = ResourceRequestStatus.Succeeded;
                Error = null;
                Notify();
            }
            catch (Exception error)
            {
                FailDisplay(accepted, error as ResourceException ?? new ResourceException(ResourceError.LoadFailed, "The image could not be applied to its UI target.", error));
            }
            finally
            {
                acquired?.Dispose();
                request.Dispose();
            }
        }

        /// <summary>读取支持的 UI 目标当前显示对象，供恢复时按引用身份核对。</summary>
        private static UnityEngine.Object ReadTarget(Graphic graphic) => graphic is Image image ? image.sprite : ((RawImage)graphic).texture;

        /// <summary>使旧代请求失效并移出自有责任，再恢复未被外部替换的显示、销毁自有 Sprite 和归还租约。</summary>
        private void ReleaseDisplay()
        {
            _generation++;
            var pendingImage = _imageRequest;
            _imageRequest = null;
            var pendingCatalog = _catalogRequest;
            _catalogRequest = null;
            var oldTarget = _displayTarget;
            var oldDisplayed = _displayed;
            var oldPrevious = _previous;
            var hadDisplay = _hasDisplay;
            var sprite = _ownedSprite;
            var held = _lease;
            _hasDisplay = false;
            _displayed = _previous = _leasedAsset = null;
            _displayTarget = null;
            _ownedSprite = null;
            _lease = null;
            // 先移交局部清理责任，再调用可重入 UI setter；不覆盖外部代码替换的内容。
            try
            {
                try
                {
                    pendingImage?.Dispose();
                }
                finally
                {
                    pendingCatalog?.Dispose();
                }
                if (hadDisplay && oldTarget && ReferenceEquals(ReadTarget(oldTarget), oldDisplayed))
                {
                    if (oldTarget is Image image)
                    {
                        image.sprite = oldPrevious as Sprite;
                    }
                    else
                    {
                        ((RawImage)oldTarget).texture = oldPrevious as Texture;
                    }
                }
            }
            finally
            {
                try
                {
                    if (sprite)
                    {
                        if (Application.isPlaying)
                        {
                            Destroy(sprite);
                        }
                        else
                        {
                            DestroyImmediate(sprite);
                        }
                    }
                }
                finally
                {
                    held?.Dispose();
                }
            }
        }

        /// <summary>只为仍被接受的代次清理失败显示，合并恢复异常而不覆盖重入产生的新代状态。</summary>
        private void FailDisplay(int accepted, ResourceException failure)
        {
            if (accepted != _generation)
            {
                return;
            }

            var released = _generation + 1;
            try
            {
                ReleaseDisplay();
            }
            catch (Exception error)
            {
                failure = new ResourceException(ResourceError.LoadFailed, "The failed image display also failed to restore.", new AggregateException(failure, error));
            }
            if (_generation == released)
            {
                Fail(failure);
            }
        }

        /// <summary>保存失败状态与原因后通知观察者，不在此重复执行资源清理。</summary>
        private void Fail(ResourceException error)
        {
            Status = ResourceRequestStatus.Failed;
            Error = error;
            Notify();
        }

        /// <summary>逐个通知完成观察者，将各自异常记录到日志而不阻断后续通知。</summary>
        private void Notify()
        {
            if (Completed == null)
            {
                return;
            }

            foreach (Action<ResourceImage> observer in Completed.GetInvocationList())
            {
                try
                {
                    observer(this);
                }
                catch (Exception error)
                {
                    Debug.LogException(error, this);
                }
            }
        }

        /// <summary>在配置允许且已有输入时显式刷新，不隐式初始化 ResourceSystem。</summary>
        private void OnEnable()
        {
            if (loadOnEnable && HasInput)
            {
                Refresh();
            }
        }
        /// <summary>停用时归还组件当前责任，并把仍在等待的显示状态记为取消。</summary>
        private void OnDisable()
        {
            try
            {
                ReleaseDisplay();
            }
            finally
            {
                if (Status == ResourceRequestStatus.Pending)
                {
                    Status = ResourceRequestStatus.Canceled;
                }
            }
        }
        /// <summary>销毁时再次结算自身显示责任，不销毁目标原有或外部替换的资产。</summary>
        private void OnDestroy() => ReleaseDisplay();
        /// <summary>检查已显示目标、借读资产与 Sprite 纹理的 Unity 存活状态，外部销毁时结束本代显示。</summary>
        private void LateUpdate()
        {
            if (!_hasDisplay || _displayTarget && _displayed && _leasedAsset && (!(_displayed is Sprite sprite) || sprite.texture))
            {
                return;
            }

            FailDisplay(_generation, new ResourceException(ResourceError.LoadFailed, "The image target or leased Unity object was destroyed externally."));
        }
    }
}
