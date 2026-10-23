# 为路由器安装 ASN 数据库

这些脚本在**电脑**上运行，通过 SSH 安装或更新路由器上的 `ASN.mmdb`。它们与桌面程序独立，不依赖某家订阅，不导入项目，不修改规则、DNS、Hosts 或 QUIC，不重启 MC2。

每次成功生成配置，程序都会在 `output/<项目名>/router-asn/` 输出本说明、三个脚本和 `router-login.example.json` 模板。它们内置于程序资源，不需要源码仓库。再次生成会恢复或更新这五个公开文件，保留用户自己的 `router-login.json`。生成配置本身不会执行安装脚本、连接路由器或下载 ASN。

安装数据库不会自动启用项目的 ASN 规则。目标设备验证完成后，才在“规则与 DNS → ASN 依赖与兼容性”开启“保留 ASN（高级）”，重新生成并按需要自行应用配置。电脑的生成报告仍会提示目标设备需要数据库，不会把本机验证当作路由器联网验收。

## 前置条件

- 电脑能访问路由器 SSH（同一局域网或已经建立 VPN / 转发），路由器已启用 SSH；仅有账户密码不能访问异地私网地址。
- 路由器已有 MC2 / Mihomo 和可写的数据目录。默认内核是 `/koolshare/bin/clash`，数据目录是 `/koolshare/merlinclash`，可通过参数修改。
- 电脑有 Python 3.10+；Linux 发行版若拆分了 `venv`，需安装对应的 `python3-venv` 包。PowerShell 支持 5.1 / 7，POSIX 启动脚本支持 Linux / macOS 的 `sh`。
- 首次运行如缺少 Paramiko，会从 PyPI 安装到电脑的独立虚拟环境。Windows 位于 `%LOCALAPPDATA%\ClashRouteCraft\router-tools`，Linux / macOS 位于 `~/.local/share/ClashRouteCraft/router-tools`，不修改系统 Python。已有兼容 Paramiko 时直接使用。
- 默认从 MetaCubeX 官方仓库下载 ASN，电脑需能访问 GitHub；也可指定之前下载好的本地文件。路由器本身无需联网下载。

## Windows PowerShell

如需记住登录信息，将同目录的 `router-login.example.json` 复制为 `router-login.json`，填写 `host`（路由器地址）、`port`、`username` 和 `password`。脚本默认从**脚本所在目录**读取它，当前终端目录可以不同。文件是明文 JSON，仅保留在自己的电脑；不要把已填写文件分享出去。Git 默认忽略 `router-login.json`，重新生成不会覆盖它。Linux / macOS 可用 `chmod 600 router-login.json` 限制读取权限。

填写后直接运行 ` .\Install-RouterAsn.ps1`；Linux / macOS 运行 `sh ./install-router-asn.sh`，无需反复输入地址、账号和密码。没有文件或字段留空时再交互输入。先完成 SSH 主机身份检查和登录认证，成功后才下载 ASN；失败立即返回。首次准备 Python SSH 依赖仍可能需要联网，已安装的隔离环境会直接复用，不反复执行 pip。

命令行参数优先于文件；若命令行指定的目标或用户与文件不同，不复用该文件的密码。`-Password` / `--password-stdin` 可显式覆盖保存的密码。自定义文件位置使用 `-LoginFile` / `--login-file`；自行命名的凭据文件也需要自行排除 Git。

在输出的 `router-asn` 文件夹打开终端（使用源码时进入仓库的 `scripts` 文件夹），替换实际地址和管理员用户名；密码随后隐藏输入：

```powershell
.\Install-RouterAsn.ps1 -Router 192.168.50.1 -UserName admin
```

使用本地文件：

```powershell
.\Install-RouterAsn.ps1 -Router 192.168.50.1 -UserName admin -AsnFile "$HOME\Downloads\ASN.mmdb"
```

自动化也可传入 `-Password`（`SecureString`），例如 `-Password (Read-Host '路由器密码' -AsSecureString)`，或者从可信凭据管理器取得该对象。该参数通过子进程标准输入传递，不作为明文命令行参数，也不会自动写回登录文件。不要把明文密码写入脚本或终端历史。

## Linux / macOS 终端

退出路由器 SSH 会话，在电脑的 `router-asn` 文件夹运行（使用源码时进入仓库的 `scripts` 文件夹）：

```sh
sh ./install-router-asn.sh --host 192.168.50.1 --user admin
```

使用本地文件：

```sh
sh ./install-router-asn.sh --host 192.168.50.1 --user admin \
  --asn-file "$HOME/Downloads/ASN.mmdb"
```

自动化程序可直接运行 `python3 ./install-router-asn.py --host ... --user ... --password-stdin`，通过子进程 stdin 传入一行密码。一个安装流程只使用一次 SSH 认证，不依赖 `sshpass`、SFTP 或反复调用 SCP。不要在聊天中公开密码；交互式运行不需要将密码交给助手。

## SSH 身份确认与参数

脚本默认只使用**脚本所在目录**的 `router-known_hosts`，不读取 `~/.ssh/known_hosts` 或旧版共享的 `~/.config/ClashRouteCraft/router-known_hosts`。因此不同项目即使连接相同的内网地址，也各自保存设备身份；从其他终端目录启动不会改变默认记录位置。可用 `-KnownHosts` / `--known-hosts` 指定另一份独立记录，该模式同样不读取系统记录。

第一次连接未知设备时会显示 SSH 主机 SHA-256 指纹，需要核对并输入 `yes`；以后同一项目连接同一设备无需再次确认。**同一项目的已知主机密钥变化仍会在发送密码前拒绝连接**，不自动覆盖旧记录，错误会指出记录文件位置。

不匹配时明确显示 `❌ [FAIL] SSH 指纹不匹配`：先确认当前网络、路由器地址和项目是否对应；若确实换机或刷机，核实新设备身份后再备份并移除该地址的旧记录。若是手动指定的指纹参数不匹配，则提示核实后更新参数，不会误导用户删除主机记录。

旧版共享记录不自动迁移，因为无法仅凭相同 IP 判断它属于哪台路由器；更新后首次连接需重新核对一次。旧记录和账户密码文件不会被删除或修改。重新生成只更新工具文件，保留本项目的 `router-login.json` 和 `router-known_hosts`。创建另一台路由器的项目时，不要复制旧设备的 `router-known_hosts`；如确实换机或刷机，应先核实新设备身份，再备份并移除该项目记录中的对应地址，重新确认指纹。无需清理系统 SSH 记录。

无人值守的首次连接应传入从可信渠道取得的指纹：PowerShell `-HostKeySha256 'SHA256:...'` / POSIX `--host-key-sha256 'SHA256:...'`。未知主机且未提供可信指纹时，非交互运行直接失败。指纹在发送密码前检查。

| PowerShell 参数 | POSIX / Python 参数 | 含义 |
| --- | --- | --- |
| `-Router` / `-UserName` | `--host` / `--user` | 地址和用户名；交互运行可省略后填写 |
| `-Port` | `--port` | SSH 端口，默认 22 |
| `-AsnFile` | `--asn-file` | 本地 ASN 文件；省略则下载 |
| `-LoginFile` | `--login-file` | 自定义登录 JSON；默认读取脚本同目录的 `router-login.json` |
| `-DataDirectory` | `--data-dir` | 内核实际工作目录 |
| `-CorePath` | `--core` | Mihomo 可执行文件 |
| `-KnownHosts` | `--known-hosts` | 独立主机密钥记录文件；默认是脚本同目录的 `router-known_hosts` |
| `-HostKeySha256` | `--host-key-sha256` | 可选的可信 SSH 指纹 |

## 自动执行的步骤

1. 读取登录文件或交互输入，建立 SSH 连接并完成认证。失败时不下载数据库。认证成功后保持连接活跃，电脑获取 `MetaCubeX/meta-rules-dat` 发布的 `GeoLite2-ASN.mmdb`，核对 GitHub 发布 API 的 SHA-256；缺少摘要或校验失败就停止。本地文件模式检查 MMDB 的 ASN 元数据标记并计算本机摘要，不宣称已核对上游发布摘要。
2. 检查内核、目录、空间，取得安装互斥锁。拒绝替换已有符号链接或目录。
3. 上传到唯一临时文件，对比电脑与路由器 SHA-256。依次实际执行 `sha256sum`、`openssl dgst -sha256`、`busybox sha256sum`；命令缺失、提示 `applet not found` 或返回无效摘要时继续尝试下一种。全部失败则停止，不跳过完整性校验。
4. 用路由器自己的 Mihomo 和临时最小 `IP-ASN` 配置执行 `-t` 校验。临时数据库修复下载被禁用，不干扰正在运行的内核。测试中的 ASN 编号只用于加载数据库，不会写入业务配置。
5. 如存在旧文件，先建立校验过的备份，再在同一目录原子替换，设为 `644`；安装后再检查摘要和内核。内容相同则只验证，返回 `already_installed`，不重复写入或备份。
6. 替换后的校验失败时恢复旧文件；原先没有文件则撤回新文件。正常完成后清理临时文件，显示安装或验证成功、文件位置和内核验证结果；使用 `-Json` / `--json` 时输出包含状态、摘要和备份路径的 JSON。

脚本不会重新启动服务或自动切换配置。仍使用无 ASN 规则的现有配置时，装好数据库即可；之后正常应用含 ASN 规则的配置时，由用户按 MC2 流程重启。

## 失败恢复

正常结束会显示 `✅ [OK] 安装成功` 或 `✅ [OK] 验证成功：已安装，无需重复安装`，并明确内核校验通过、MC2 未重启。失败显示 `❌ [FAIL] 执行失败` 和具体原因，进程退出码非零。终端支持时使用绿色／红色；输出重定向或终端不支持时保留 `[OK]` / `[FAIL]` 文本，不依赖图标判断结果。无法显示中文的编码会改用英文。无法自动判断所有终端字体，图标异常时可加 PowerShell `-Plain` 或 POSIX `--plain`，强制关闭图标和颜色。`NO_COLOR` 环境变量也可关闭颜色。

需要原始结果时使用 `-Json` / `--json`：最终结果保留 JSON，前面的进度提示仍是文本。

下载、上传或替换前的验证失败不会更动原文件。替换后的常规失败会尝试回滚；如果网络中断、断电，或发现另一程序又改写了目标文件，脚本会停止并提示人工检查，不会宣称回滚成功。

旧文件备份名为数据目录中的 `.ASN.mmdb.backup-日期时间-随机标识`，成功后也保留。手工恢复时先核对输出的**实际备份路径**，再将其复制回 `ASN.mmdb` 并检查权限。多个历史备份可能占用闪存；确认不再需要后自行清理，脚本不擅自删除。

若上次意外中断遗留 `.crc-asn-install.lock`，确认没有另一个安装进程后，再删除这个**空目录**；不要在仍有安装任务时删除锁。错误输出会列出本次临时路径。

数据库来自 [MetaCubeX/meta-rules-dat](https://github.com/MetaCubeX/meta-rules-dat)，保留上游的数据许可要求。SSH 使用 [Paramiko](https://www.paramiko.org/)，仅作为独立工具依赖，不打入桌面 AOT EXE。
