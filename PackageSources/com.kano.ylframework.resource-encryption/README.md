# YLFramework 远端资源加密

`com.kano.ylframework.resource-encryption` 是面向 Windows x64 Release Player 的选装包，用固定 Catalog 交付一个远端 Addressables 组的加密 Bundle。主包不依赖它，普通 Addressables 项目不需要安装；安装本包也不会自动启用加密。

## 依赖与接入

- 先提供同一批次的 `com.kano.ylframework` 主包，再从 Package Manager 添加本包的 `package.json`；使用同一批次的本地 `.tgz` 时也先装主包。Addressables 和 Json.NET 由 UPM 按依赖声明解析。
- 为一个远端组显式配置加密 Provider，由 `EncryptedBundleExporter` 构建配套 Player 和密文。在应用启动任何 Addressables 消费者之前，先启动 `EncryptedDeliverySession`。应用自行准备密钥，构建成功也不代表已上传内容。

支持范围、组配置、调用示例、密钥与关闭责任、停用及重新构建步骤见[本包使用手册](Documentation~/README.md)。普通资源流程见源码仓库当前的[主包资源交付指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Modules/Resource/Delivery.md)。

## 许可证与来源

见 [LICENSE.md](LICENSE.md) 与手册内的[原生后端与来源](Documentation~/README.md#原生后端与来源)。
