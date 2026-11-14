# 汽水登录资源说明

`login.js` 与宿主窗口由 Klang 编写，使用公开客户端协议的字段和端点；未包含官方客户端源码或上游插件的浏览器环境收集器。

协议核对来源：

- guowenye/qishui-api，MIT，`src/qishuiClient.js` 的 Passport 取码/轮询接口。
- yiyiSmallti/mygo-qishui，提交 `2cc2eb40638d4f0672f3161efb4af2a1864ccf7d`，`qishui-account/embedded-login.js`、`browser-verification.js` 与 `login-helper/main.go`。仅据其核对协议和组件调用方式，没有复制该 AGPL 窗口实现或第三方浏览器收集器。

运行时通过 HTTPS 加载官方发布的 SDK Glue、官方额外验证组件；它们的权利属于原权利人，不包含在 Klang 安装资源中。需要现代额外验证时从固定版本 CDN 加载 React 与 ReactDOM 18.3.1 UMD（MIT，https://github.com/facebook/react/blob/v18.3.1/LICENSE）。

登录页面省略可选的 `account_sdk_source_info`，没有伪造浏览器环境报告。是否所有账号的额外验证都接受该流程仍需真实登录验收。
