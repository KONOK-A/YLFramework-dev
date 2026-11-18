using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace YLFramework.Resource
{
    /// <summary>只提供受限动态图片；共享和引用计数完全交给官方 ResourceManager operation。</summary>
    internal sealed class AddressablesImageProvider : ResourceProviderBase
    {
        internal const string Id = "YLFramework.Resource.AddressablesImageProvider";
        private readonly int _threadId = Thread.CurrentThread.ManagedThreadId;
        private readonly HashSet<string> _running = new(StringComparer.Ordinal);
        private readonly HashSet<Texture2D> _created = new();

        internal sealed class Context
        {
            internal readonly ResourceImageOptions Options;
            internal readonly Action<IEnumerator> Run;
            internal readonly string Identity = Guid.NewGuid().ToString("N");
            internal int WorkCount;

            internal Context(ResourceImageOptions options, Action<IEnumerator> run)
            {
                Options = options;
                Run = run;
            }

        }

        internal sealed class Input
        {
            internal readonly Context Owner;
            internal readonly string Value;
            internal readonly bool IsLocal;

            internal Input(Context owner, string value, bool local)
            {
                Owner = owner;
                Value = value;
                IsLocal = local;
            }
        }

        private sealed class Work
        {
            internal string Key;
            internal ProvideHandle Handle;
            internal Input Input;
            internal bool IsDone;
        }

        internal AddressablesImageProvider() => m_ProviderId = Id;

        internal static IResourceLocation CreateLocation(string value, Context context)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("An exact HTTP(S) URL or absolute PNG/JPEG file path is required.");
            }
            var local = Path.IsPathFullyQualified(value);
            if (!local && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)))
            {
                throw new ArgumentException("Dynamic images require HTTP(S) without user-info or fragment, or an absolute file path.");
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            if (local)
            {
                throw new NotSupportedException("Absolute local image paths are not supported in WebGL.");
            }
#endif
            // 官方 location/异常/Profiler 只看到不透明身份，原始 URL 只留在 Provider 私有输入中。
            using var hash = SHA256.Create();
            var digest = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
            var key = "image:" + context.Identity + ":" + digest;
            return new ResourceLocationBase(key, key, Id, typeof(Texture2D)) { Data = new Input(context, value, local) };
        }

        public override Type GetDefaultType(IResourceLocation location) => typeof(Texture2D);

        public override bool CanProvide(Type type, IResourceLocation location)
            => type == typeof(Texture2D) && location?.ProviderId == Id && location.Data is Input && !location.HasDependencies;

        // 同键在途 operation 由 ResourceManager 合并，不占第二个 IO 槽。
        internal bool CanStart(string key, Context context) => _running.Contains(key) || _running.Count < context.Options.MaximumConcurrentLoads;

        public override void Provide(ProvideHandle handle)
        {
            CheckThread();
            if (!CanProvide(handle.Type, handle.Location))
            {
                handle.Complete<Texture2D>(null, false, new ArgumentException("This Provider accepts only its own dependency-free dynamic Texture2D locations."));
                return;
            }
            var key = handle.Location.InternalId;
            var input = (Input)handle.Location.Data;
            if (_running.Count >= input.Owner.Options.MaximumConcurrentLoads || !_running.Add(key))
            {
                handle.Complete<Texture2D>(null, false, new InvalidOperationException("The dynamic image Provider was invoked without an available IO slot."));
                return;
            }
            var work = new Work { Key = key, Handle = handle, Input = input };
            input.Owner.WorkCount++;
            try
            {
                input.Owner.Run(Load(work));
            }
            catch (Exception error)
            {
                Finish(work, null, SafeFailure(error));
            }
        }

        private IEnumerator Load(Work work)
        {
            UnityWebRequest request = null;
            UnityWebRequestAsyncOperation operation = null;
            ImageDownload download = null;
            Texture2D texture = null;
            Exception failure = null;
            try
            {
                try
                {
                    var input = work.Input;
                    if (input.IsLocal)
                    {
                        CheckFile(input.Value, input.Owner.Options.MaximumEncodedBytes);
                    }
                    download = new ImageDownload(input.Owner.Options.MaximumEncodedBytes);
                    request = new UnityWebRequest(input.IsLocal ? new Uri(input.Value).AbsoluteUri : input.Value,
                        UnityWebRequest.kHttpVerbGET, download, null);
                    request.timeout = 30;
                    work.Handle.SetProgressCallback(() => request == null ? 0 : Mathf.Max(0, request.downloadProgress));
                    operation = request.SendWebRequest();
                }
                catch (Exception error)
                {
                    failure = SafeFailure(error);
                }
                if (failure == null)
                {
                    while (!operation.isDone)
                    {
                        yield return null;
                    }
                    try
                    {
                        if (download.HasExceededLimit)
                        {
                            throw new InvalidDataException("Image download exceeded the encoded byte budget.");
                        }
                        if (request.result != UnityWebRequest.Result.Success)
                        {
                            // UnityWebRequest.error 以及第三方异常可能包含 URL/query，不能直接并入公开诊断。
                            throw new InvalidDataException("Image request failed (HTTP " + request.responseCode + ", " + request.result + ").");
                        }
                        texture = Decode(download.CopyBytes(), work.Input.Owner.Options);
                    }
                    catch (Exception error)
                    {
                        failure = SafeFailure(error);
                    }
                }
            }
            finally
            {
                try
                {
                    if (request != null)
                    {
                        request.Dispose();
                        request = null;
                    }
                    else
                    {
                        download?.Dispose();
                    }
                }
                finally
                {
                    Finish(work, texture, failure ?? (texture ? null : new InvalidDataException("Image work ended before decoding completed.")));
                }
            }
        }

        private void Finish(Work work, Texture2D texture, Exception error)
        {
            if (work.IsDone)
            {
                return;
            }
            work.IsDone = true;
            _running.Remove(work.Key);
            work.Input.Owner.WorkCount--;
            if (error != null && texture)
            {
                DestroyTexture(texture);
                texture = null;
            }
            if (texture)
            {
                _created.Add(texture);
            }
            work.Handle.Complete(texture, error == null && texture, error);
        }

        public override void Release(IResourceLocation location, object asset)
        {
            CheckThread();
            if (asset is Texture2D texture && _created.Remove(texture))
            {
                DestroyTexture(texture);
            }
        }

        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _threadId)
            {
                throw new InvalidOperationException("Dynamic image Provider operations must run on the Unity main thread.");
            }
        }

        private static Exception SafeFailure(Exception error)
        {
            if (error is InvalidDataException)
            {
                return error;
            }
            if (error is NotSupportedException)
            {
                return new ResourceException(ResourceError.Unsupported, "This platform does not support the requested dynamic image input.");
            }
            return new IOException("Dynamic image input could not be loaded (" + error.GetType().Name + ").");
        }

        private static void CheckFile(string path, int maximumBytes)
        {
            var current = path;
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Local image paths cannot traverse symbolic links or junctions.");
                }
                current = Path.GetDirectoryName(current);
            }
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new InvalidDataException("Local image file is missing or inaccessible.");
            }
            if (file.Length <= 0 || file.Length > maximumBytes)
            {
                throw new InvalidDataException("Local image exceeded the encoded byte budget or is empty.");
            }
        }

        private static Texture2D Decode(byte[] bytes, ResourceImageOptions options)
        {
            var (width, height) = Dimensions(bytes);
            if (width <= 0 || height <= 0 || width > SystemInfo.maxTextureSize || height > SystemInfo.maxTextureSize ||
                (long)width * height > options.MaximumPixels)
            {
                throw new InvalidDataException("Image dimensions exceed the pixel budget or GPU texture limit.");
            }
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!ImageConversion.LoadImage(texture, bytes, true))
                {
                    throw new InvalidDataException("PNG/JPEG image decoding failed.");
                }
                if (texture.width != width || texture.height != height)
                {
                    throw new InvalidDataException("Decoded image dimensions do not match its header.");
                }
                return texture;
            }
            catch
            {
                DestroyTexture(texture);
                throw;
            }
        }

        private static void DestroyTexture(Texture2D texture)
        {
            if (!texture)
            {
                return;
            }
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(texture);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static (int width, int height) Dimensions(byte[] bytes)
        {
            if (bytes.Length >= 33 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71 &&
                bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10 &&
                bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0 && bytes[11] == 13 &&
                bytes[12] == 73 && bytes[13] == 72 && bytes[14] == 68 && bytes[15] == 82)
            {
                return (Read32(bytes, 16), Read32(bytes, 20));
            }
            if (bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216)
            {
                var offset = 2;
                while (offset < bytes.Length)
                {
                    if (bytes[offset++] != 255)
                    {
                        break;
                    }
                    while (offset < bytes.Length && bytes[offset] == 255)
                    {
                        offset++;
                    }
                    if (offset >= bytes.Length)
                    {
                        break;
                    }
                    var marker = bytes[offset++];
                    if (marker == 217 || marker == 218)
                    {
                        break;
                    }
                    if (marker == 1 || marker >= 208 && marker <= 215)
                    {
                        continue;
                    }
                    if (offset + 2 > bytes.Length)
                    {
                        break;
                    }
                    var length = (bytes[offset] << 8) | bytes[offset + 1];
                    if (length < 2 || length > bytes.Length - offset)
                    {
                        break;
                    }
                    if (marker >= 192 && marker <= 207 && marker != 196 && marker != 200 && marker != 204)
                    {
                        if (length < 8)
                        {
                            break;
                        }
                        return ((bytes[offset + 5] << 8) | bytes[offset + 6], (bytes[offset + 3] << 8) | bytes[offset + 4]);
                    }
                    offset += length;
                }
            }
            throw new InvalidDataException("A valid PNG or JPEG image header is required.");
        }

        private static int Read32(byte[] bytes, int offset)
            => (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];

        private sealed class ImageDownload : DownloadHandlerScript
        {
            private readonly int _maximumBytes;
            private readonly MemoryStream _bytes = new();
            internal bool HasExceededLimit
            {
                get; private set;
            }

            internal ImageDownload(int maximumBytes) : base(new byte[16 * 1024]) => _maximumBytes = maximumBytes;
            protected override void ReceiveContentLengthHeader(ulong length) => HasExceededLimit |= length > (ulong)_maximumBytes;

            protected override bool ReceiveData(byte[] data, int count)
            {
                if (HasExceededLimit || count > _maximumBytes - _bytes.Length)
                {
                    HasExceededLimit = true;
                    return false;
                }
                if (data != null && count > 0)
                {
                    _bytes.Write(data, 0, count);
                }
                return true;
            }

            internal byte[] CopyBytes() => _bytes.ToArray();
            public override void Dispose()
            {
                _bytes.Dispose();
                base.Dispose();
            }
        }
    }
}
