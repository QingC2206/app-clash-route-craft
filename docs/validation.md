# 验证流程

在仓库根目录执行：

```powershell
dotnet build ClashRouteCraft.slnx -c Release
dotnet run --project tests/ClashRouteCraft.Tests -c Release --no-build
dotnet run --project tests/ClashRouteCraft.UiTests -c Release --no-build -- artifacts/ui
python -m unittest discover -s tools/python-reference/tests -p "test_*.py"
python scripts/check-parity.py
```

测试使用生成的虚构配置与临时目录，不依赖真实项目或订阅。`MIHOMO_PATH` 可指定 Python 优先级运行测试使用的内核，未提供且默认位置不存在时会跳过该组测试。

已知限制：历史 Python 参考实现尚未同步桌面端的分组优先级与报告字段，`check-parity.py` 当前会在报告结构对照处失败。桌面端的现行行为以 C# 核心回归、界面检查和实际 AOT 自检验证；不能将该历史对照计为通过。

仅在明确要求打包发布后，才执行以下发布验证（不属于日常构建验证）： `python scripts/check-release.py` 和 `pwsh -File scripts/check-package-data.ps1`，检查发布包及用户数据保留行为；其中打包回归脚本会在临时目录中创建测试压缩包。请先阅读脚本参数和前置条件。

可选的 `check-rule-semantics-v2.py`、`check-rule-generation-v2.py` 和 `check-domain-auto.py` 使用本地模拟服务验证规则行为，需要已构建的测试程序、发布版及独立下载到发布工作区 `resources/validation/mihomo.exe` 的内核。它们不是无需外部资源的单元测试。

验证报告、截图、生成配置与运行日志保存在忽略目录中，不作为源码发布。

## ASN 与目标环境检查

生成报告分别记录本机内核校验、ASN 来源分析（`asn`）和目标环境静态依赖（`target_dependencies`）。本机校验通过不等于路由器安装了对应数据库，也不等于已完成路由器联网测试。订阅模式的附加名单同样纳入检查。

每次 Mihomo 校验使用新的临时目录，只复制声明的必要数据库。缺失文件直接失败；损坏文件不能依赖自动下载修复，校验期间 GEO 下载地址指向不可连接的本地端口，且关闭自动更新。该设置仅用于校验，绝不写入交付 YAML。校验日志保存在工作区，临时目录随后清理。

可选 MC2 安装包校验按其导入字段构建配置，使用安装包自身的 Hosts，按项目选择使用原装或生成的 DNS，不把主 YAML 的 hosts 当作 MC2 已保留。使用额外数据库时报告仍明确标记依赖，不能标记为路由器已就绪。本程序不内置或自动下载 MC2 安装包。

ASN 回归包含：默认阻断且旧输出不变、按来源排除、组合规则整条处理、来源重命名、订阅及规则集依赖、未读取的远程名单、显式保留，以及界面保存/检查/生成流程。运行真实内核的损坏数据库与禁止下载测试时可设置：

```powershell
$env:CRC_TEST_MIHOMO = '你的资源目录\validation\mihomo.exe'
$env:CRC_TEST_GEODATA = '你的资源目录\geodata'
dotnet run --project tests/ClashRouteCraft.Tests -c Release
```

未设置 `CRC_TEST_MIHOMO` 时会明确跳过该真实内核测试，其余回归不依赖私有项目或公网订阅。

## 独立 ASN 安装工具

C# 回归和 AOT 自检同时检查内置工具资源的导出：MC2 / Clash 生成均输出完整文件，检查 ASN 来源时不生成文件，重新生成可恢复缺失工具且不改变 YAML，工具写入失败时保留已有输出。工具和主配置使用同一个原子提交流程；不会因导出工具而连接路由器。

安装工具另有认证顺序与登录文件回归：认证失败不下载、下载失败关闭连接、BOM / Unicode 登录文件、密码不回显、命令行覆盖、已安装依赖直接复用，以及 BusyBox 命令入口存在但 applet 不可用时回退至 OpenSSL。生成会保留用户已填写的 `router-login.json`。

```sh
python -m unittest discover -s tests -p test_router_asn_installer.py -v
```

测试仅使用临时文件、模拟内核与回环 SSH 服务器，不连接实际路由器。覆盖重复安装、备份、上传摘要不符、存储不足、内核拒绝、替换后回滚、互斥锁、OpenSSL 校验，以及无 SFTP 的二进制传输、单次密码认证和主机身份拒绝。POSIX 命令测试需要 `sh`；Windows 默认使用 Git Bash，也可设置 `CRC_TEST_BASH`。SSH 传输测试需要 Paramiko；缺失依赖的项目会明确跳过，不能计为通过。脚本测试不代表已在所有路由器固件上实机验收。
