# 第三方代码归属与修改说明

- 上游：https://github.com/guowenye/qishui-api （`src/audioDecryptor.js`、`src/errors.js`）
- 许可：MIT（原文见同目录 `LICENSE`，Copyright (c) 2026 arrow）
- 用途：Klang 汽水接入的授权音频 CENC（AES-128-CTR）解密与 `spade_a` 密钥解析。

## Klang 修订（非上游原样）

为让官方 CENC 容器能被正确还原，Klang 在保留 MIT 许可与归属的前提下对 `audioDecryptor.js` 做了两处定点修改：

1. `processBoxTree` 处理 `stsd` 时跳过 8 字节 FullBox 前缀（version/flags + entry_count）再遍历 sample entry；
2. 处理 `enca` 时跳过 28 字节 AudioSampleEntry 固定正文（6 reserved + 2 data_reference_index + 20 音频字段）再遍历 `esds`/`sinf`/`tenc`，输出改写为 `mp4a`。

其余逻辑（样本解密、`senc` 解析、`stco` 重算、FLAC 分支）保持上游实现；打包前曾通过合成 CENC 样本检查，源码包不附测试脚本和样本。
