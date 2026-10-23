# README 截图 / Screenshot provenance

这些 PNG 由真实 Avalonia 控件渲染，不是设计稿或个人工作区截图。截图使用独立临时工作区，退出后清理。全部数据由 `tests/ClashRouteCraft.UiTests/ReadmeScreenshots.cs` 创建：项目名为“演示项目 Demo”，订阅使用 `.example.invalid`，域名使用 `.example`，代理使用保留测试网段 `192.0.2.0/24`，账户为虚构占位值。没有访问真实订阅、路由器或登录文件。

These PNGs render actual application controls, not mockups or a personal workspace. The capture harness creates disposable synthetic fixtures: reserved domains and test addresses, fictional credentials, and a demo project. It does not contact subscriptions or routers or load personal login files. No successful network or core validation is fabricated.

从仓库根目录重新生成 / Reproduce from the repository root:

```powershell
dotnet run --project tests/ClashRouteCraft.UiTests -c Release -- artifacts/readme-screenshots --readme
```

逐张检查画面、内容和隐私后，再将七张 PNG 复制到本目录。不要从日常项目中取图。输出页如实显示“本机内核：未运行，仅检查结构”，DNS 页展示手动开启的增强模式，出厂默认仍关闭。

Review every image for layout and privacy before copying the seven PNGs here. Do not capture a live personal project. The output screenshot intentionally shows that no core validation ran; the DNS screenshot shows an explicitly enabled option that is off by default.
