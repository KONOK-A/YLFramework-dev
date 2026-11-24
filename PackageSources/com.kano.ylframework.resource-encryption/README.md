# YLFramework 资源加密

`com.kano.ylframework.resource-encryption` 是面向 Windows x64 Release Player 的选装包。它用同一个 Addressables Catalog 交付多个随包或 HTTPS 加密组。主包不依赖它，普通 Addressables 项目不需要安装；安装本包也不会自动启用加密。

## 依赖与接入

- 先提供同一批次的 `com.kano.ylframework` 主包，再从 Package Manager 添加本包的 `package.json`；使用同一批次的本地 `.tgz` 时也先装主包。Addressables 和 Json.NET 由 UPM 按依赖声明解析。
- 为保护组显式配置加密 Provider，由 `EncryptedBundleExporter` 一次交付配套 `Player/`、`Upload/` 和清单。应用先启动 `EncryptedDeliverySession`，再开放任何资源消费者。混合交付可用本地密钥直接读随包章节，需要远端章节时再准备远端缓存。构建成功不代表内容已上传。

支持范围、组配置、调用示例、密钥与关闭责任、停用及重新构建步骤见[本包使用手册](Documentation~/README.md)。普通资源流程见源码仓库当前的[主包资源交付指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Modules/Resource/Delivery.md)。

## 许可证与来源

见 [LICENSE.md](LICENSE.md) 与手册内的[原生后端与来源](Documentation~/README.md#原生后端与来源)。
