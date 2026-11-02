using System;
using System.Collections.Generic;

namespace YLFramework.Resource
{
    public sealed partial class ResourceSystem
    {
        private readonly Dictionary<string, Audio.ResourceAudioClipProvider> _audioProviders = new(StringComparer.Ordinal);

        /// <summary>显式取得本 authority、指定 ProjectId 的 Audio 后端；不初始化资源、不建立额外的资产缓存。</summary>
        public Audio.ResourceAudioClipProvider GetAudioClipProvider(string projectId)
        {
            CheckThread();
            if (!IsAvailable)
            {
                throw new ResourceException(ResourceError.SessionUnavailable, "ResourceSystem is not available.");
            }

            ResourceAddress.ValidateId(projectId, nameof(projectId));
            if (!_catalogs.ContainsKey(projectId))
            {
                throw new ResourceException(ResourceError.AddressNotFound, "Unknown resource project: " + projectId);
            }

            if (!_audioProviders.TryGetValue(projectId, out var provider))
            {
                _audioProviders.Add(projectId, provider = new Audio.ResourceAudioClipProvider(this, projectId));
            }

            return provider;
        }
    }
}
