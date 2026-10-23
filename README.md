# ClashRouteCraft

面向 Clash / Mihomo 的可视化配置工作台，支持订阅管理、节点链路编排、规则集导入与策略映射、DNS 分流及配置生成校验。

项目仓库名为 `app-clash-route-craft`，桌面程序为 `ClashRouteCraft.exe`。

## 功能

- 多项目管理，编辑订阅来源、筛选节点和配置住宅出口链路。
- 从文件、URL、粘贴内容或已拉取的订阅导入规则集，查看规则并映射到本地策略；导入过程不带入订阅、节点或 DNS。
- 默认采用列表中第一个启用的完整订阅作为规则基线，其余订阅只提供节点。保留主订阅的匹配条件与顺序，动作归并为本地直连、代理、拦截，不再保留广告、业务分类或“漏网之鱼”等独立策略组。
- 所有代理规则统一使用“代理出口”，在客户端面板选择住宅出口或机场入口。有住宅时默认住宅，否则默认机场；代理选择中不混入 DIRECT。每个住宅保留 `[链式] 节点名`、`[直连] 节点名` 两条路径，不覆盖住宅的 UDP 或 TLS 校验选项。
- 链式住宅经“机场入口”连接，可选“聚合组”或按实际订阅名称生成的分组；组内自动测速，不生成机场优先级分层。默认筛选香港、日本、台湾、新加坡，可在界面调整。
- 主订阅保留原有 `hosts`；其他订阅和节点 provider 保留节点服务器所需的解析映射及连续域名别名，随订阅拉取、文件导入、缓存及生成传递。其他订阅的通配映射仅应用于实际节点及别名，避免带入无关业务域名；不同来源的同域名映射冲突或别名循环会阻止生成并保留旧输出。
- 个人附加规则在订阅规则前匹配，可整体停用而保留内容；直连、代理、拦截按界面顺序整组匹配，手工、URL、导入和旧前置规则均按实际动作归组。冲突保留规则，按输出顺序匹配；代理目标归并到统一出口。界面可查看订阅策略归并结果，不合并多个订阅的规则。
- 默认由 MC2 / 客户端管理 DNS。可一键选择 Fake-IP DNS 隐私增强，代理 DNS 默认经住宅出口，也可明确改选机场入口；不改变业务规则或 QUIC，不增加域名库或 DNS 规则集。
- MC2 目标同时输出带版本来源的原装 Fake-IP DNS 恢复文件和应用说明。增强 DNS 需在 MC2 单独应用；关闭软件开关并重新导入主配置不会自动恢复路由器 DNS。主订阅自身的规则依赖仍需客户端支持。
- 旧项目可在“规则与 DNS”中勾选“使用第一个启用订阅的规则”。旧的自定义编排和 DNS 编辑仍可显式选择，原设置不会被删除。

## 默认数据与隐私

内置 Example 模板位于 `src/ClashRouteCraft.Core/Resources/Defaults/`，只含通用占位配置，不带订阅规则，DNS 隐私增强默认关闭。首次启动或模板文件缺失时，程序恢复缺失文件，已有配置保留。填写并拉取完整订阅后即可生成，住宅出口可选。主订阅缓存若只有节点，必须重新拉取或导入完整配置，程序不会改用低优先级订阅的规则。

每份程序默认只使用自身目录中的工作区和设置，不读取本机旧版偏好设置，也不会搜索或自动迁移其他目录的数据。只有显式选择已有工作区或使用工作区命令行参数时，才会访问指定目录。

旧版仅含节点的缓存无法还原已经丢失的 `hosts`。若原订阅依赖这些映射，请重新拉取订阅或导入完整文件，再重新生成；升级程序不会自行改写项目、订阅缓存或路由器配置。

Mihomo 和 GeoData 是可选的独立下载资源，程序提示后再下载；发行包不内置这些二进制资源，也不包含 MC2 安装包。

路由器缺少 ASN 数据库时，可使用独立的 [PowerShell / Linux / macOS 安装工具](docs/router-asn.md)，通过 SSH 自动上传、校验和备份。每次成功生成都会从程序内置资源输出完整工具到 `output/<项目名>/router-asn/`，可单独复制使用。该工具不会改动项目、DNS 或重启 MC2。

个人项目、订阅快照、节点凭据、生成结果、缓存、下载资源和日志不属于仓库内容。`.gitignore` 在任意目录层级排除 `config/`、`settings/`、`resources/`、`output/`、`subscrip/` 等运行数据，并排除备份、私钥、数据库和网络抓包文件；源码中的 `Resources/` 目录单独放行。YAML 默认排除，仅允许逐个列出的内置模板、测试夹具及 GitHub 工作流等公开配置；新增公开模板或夹具时需同步更新白名单。测试使用临时目录、回环地址和虚构项目。

提交前仍应检查暂存区的文件列表和内容。忽略规则不会移除已经跟踪的文件，也不能阻止 `git add -f`，不能清除已有提交中的敏感数据；复制进源码、文档或公开模板的凭据也需要人工复核。

## 目录结构

```text
src/                       # 核心库与桌面应用
tests/                     # .NET 回归测试与夹具
scripts/                   # 构建、打包和验证脚本
tools/python-reference/    # Python 参考实现及其测试
docs/                      # 使用与验证说明
ClashRouteCraft.slnx        # 解决方案
```

为兼容已有部署，生成配置仍使用 `HomeChain.yaml`、`HomeChain.MC2DNS.yaml` 等原有文件名与规则标识。
MC2 输出目录另含 `HomeChain.MC2DNS.Default.yaml`（MC2 1.2.2 ARM64 原装 DNS）和 `HomeChain.DNS-README.txt`。完整应用、恢复和隐私边界见 [使用说明](docs/usage.md)。

## 构建与运行

桌面端使用 .NET 10 和 Avalonia，当前发布目标为 Windows x64。安装 .NET 10 SDK 后，在仓库根目录执行：

```powershell
dotnet build ClashRouteCraft.slnx -c Release
dotnet run --project src/ClashRouteCraft.Desktop -c Release
```

上面的命令用于开发检查。日常使用和编译交付以 Windows x64 Native AOT 为主，需要安装 Visual Studio C++ 构建工具和 Windows SDK：

```powershell
dotnet publish src/ClashRouteCraft.Desktop -c Release -r win-x64 --self-contained true -p:PublishAot=true -o artifacts/publish/win-x64-aot
```

运行 `artifacts/publish/win-x64-aot/ClashRouteCraft.exe`。更新前关闭该目录的应用；自动更新编译产物时，应先在临时目录完成编译和验证，再仅替换程序文件，保留工作区数据。AOT 编译本身不生成 ZIP，也不需要运行打包脚本。

开发过程中产生的工作区、构建目录和日志不应提交。用户数据位于所选工作区；发布版可以在独立目录中使用。

## 测试

.NET 测试采用可执行测试程序：

```powershell
dotnet run --project tests/ClashRouteCraft.Tests -c Release
dotnet run --project tests/ClashRouteCraft.UiTests -c Release -- artifacts/ui
```

`tools/python-reference/` 保留 Python 参考生成器和回归测试，供行为对照使用；桌面应用的实现位于 `src/`。

```powershell
python -m pip install -r tools/python-reference/requirements.txt
python -m unittest discover -s tools/python-reference/tests -p "test_*.py"
python scripts/check-parity.py
```

实际内核运行测试需要单独下载 Mihomo。Python 优先级测试可用环境变量 `MIHOMO_PATH` 指定内核路径；缺少内核时该测试会跳过。其余内核验证脚本参见 [验证说明](docs/validation.md)。

## 发布

以下步骤仅在明确需要分发包时手动执行；日常 AOT 编译不执行这些打包步骤。先按上文生成 AOT，再在仓库根目录执行：

```powershell
pwsh -File scripts/Prepare-Notices.ps1
pwsh -File scripts/Package.ps1
```

打包产物位于 `releases/`，打包脚本通过文件清单排除用户数据。已有安装目录更新前需要关闭对应应用。仅生成 `ClashRouteCraft-win-x64/`、对应 ZIP 和 `release-manifest.json`，不再生成 `full` 版本。

## 许可证

源码采用 [MIT License](LICENSE)。第三方组件各自遵循其许可证，参见 [第三方说明](THIRD-PARTY-NOTICES.md)。
