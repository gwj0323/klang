# Klang · 汽水运行时（Runtime/Soda）

## 这是什么

- `helper.mjs`：隐藏 Node 子进程脚本，经私有 stdin/stdout 单行 JSON 提供三条命令：
  `register`（官方 `device.node` 匿名设备注册）、`sign`（官方 `bdms.node` 请求签名）、
  `decode-file`（MIT `audioDecryptor.js` 做 CENC/AES-CTR 解密，输入输出都走文件路径）。
- `ThirdParty/qishui-api/`：来自上游 [qishui-api](https://github.com/guowenye/qishui-api) 的 `src/audioDecryptor.js`、`src/errors.js`
  与 MIT LICENSE 原文。Klang 修改了 `audioDecryptor.js` 的 CENC 容器遍历：正确跳过 `stsd` 的 8 字节 FullBox 前缀与 `enca` 的 28 字节 AudioSampleEntry 正文，再处理内部加密信息。修改范围与上游归属见 [ATTRIBUTION.md](ThirdParty/qishui-api/ATTRIBUTION.md)。打包前曾通过合成 CENC 样本检查；按清理要求，源码包不附测试脚本和样本。

## 依赖与分发边界

- 汽水账号接口、签名与授权音频解密需要 **Node.js**：设置 `KLANG_SODA_NODE_EXE`（或 `KLANG_SODA_NODE`）指向 node.exe 即可；
  不设置时按 PATH → `Program Files\nodejs` 查找。找不到时相关能力不可用，并给出错误；本地播放与网易云功能不依赖此运行时。
- 官方原生组件（`device.node`、`bdms.node`、`metasecml.dll`）**不随本仓库分发**。
  程序会从用户本机已装的固定版本 **3.7.0** 读取这三件公共组件，复制到数据目录
  `StateService.ResolveDataDirectory()/soda/native/<版本-哈希>/` 后在同目录加载；
  不改官方安装目录、不读取官方账号数据。发现其它版本时不静默混用，直接报“只支持 3.7.0”。
- `KLANG_SODA_SDK_DIR` 可显式覆盖组件目录（开发/临时排查用），同样只接受这三件公共组件。

## 许可

- 本目录自有代码（helper.mjs、README）随 Klang 项目许可。
- `ThirdParty/qishui-api/*` 为 MIT，Copyright (c) 2026 arrow，原文见 `ThirdParty/qishui-api/LICENSE`。
