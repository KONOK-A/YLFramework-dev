namespace YLFramework.Audio
{
    /// <summary>音乐交叉淡变时两条轨道的过渡增益映射。</summary>
    public enum AudioCrossfadeCurve
    {
        /// <summary>增益线性变化；保留零值以兼容既有序列化数据，需要旧映射时显式选择。</summary>
        Linear = 0,
        /// <summary>新音乐过渡的默认映射；正弦/余弦增益使不相关的满增益素材更接近恒功率，不保证等响度。</summary>
        EqualPower = 1
    }
}
