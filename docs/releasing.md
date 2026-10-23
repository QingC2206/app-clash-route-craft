# 自动发布 / Automated releases

仓库使用 `.github/workflows/release.yml`：推送 `v` 开头的版本标签后，在 GitHub 托管的干净 Windows 环境中构建一个 **Windows x64 Native AOT** 版本。中文和英文 README、七张演示截图、使用指南和许可证随 ZIP 一起发布。不会读取本机工作区，也不会连接或修改路由器。

The workflow builds one **Windows x64 Native AOT** edition in a clean GitHub-hosted Windows runner. The ZIP includes both READMEs, seven demo screenshots, guides and licenses. It never accesses a local personal workspace or a router.

## 发布新版本 / Publish a new version

1. 更新 `src/ClashRouteCraft.Desktop/ClashRouteCraft.Desktop.csproj` 中的 `Version`，例如 `1.16.6`。检查默认模板和截图只包含虚构数据，提交并推送到 `main`。
2. 建议先打开 GitHub → Actions → **AOT Release** → **Run workflow**，选择 `main`。手动运行只验证并保存构建附件，**不会发布 Release**。
3. 验证通过后，在对应提交上创建并推送版本标签。标签必须严格等于 `v` 加程序版本，否则工作流失败。

Update the project `Version`, review public templates and screenshots, commit and push. Run **AOT Release → Run workflow** first for a validation-only build. Then tag that exact commit; the tag must match the project version.

```bash
# 示例：先确保程序版本已改为 1.16.6，并提交到 main。
# Example: first commit the project Version change to 1.16.6.
git tag -a v1.16.6 -m "Release v1.16.6"
git push origin v1.16.6
```

正式标签发布稳定版，`v1.16.6-rc.1` 这类标签发布预览版。不要移动已发布的标签。已有公开 Release 不会被覆盖；重试会报错。若失败时只创建了草稿，修复外部问题后可重跑原任务，续传并校验草稿附件；若需要修改代码，请使用新提交和新版本标签。

Stable tags publish stable releases; suffixes such as `-rc.1` publish prereleases. Do not move published tags. Existing public releases are never overwritten. A failed draft can be resumed by rerunning the same build after fixing an external issue; code changes should use a new commit and version tag.

## 检查与附件 / Checks and assets

- 版本一致性、Git 已跟踪但被忽略的文件、私人数据路径及常见凭据模式检查。错误只报告文件名，不打印秘密值。
- 核心测试、界面测试、ASN 工具回归、发布检查回归；从解压后的 AOT 程序执行自检与首次启动、模板恢复检查。未提供 Mihomo 的 CI 环境会跳过真实内核测试，不代表已验证路由器联网。
- 发布 ZIP 必须严格匹配公开文件白名单；检查 Windows x64 原生 PE、全部文件校验和、文档一致性和离线链接。不会上传本地 `release-manifest.json`、测试日志、个人配置或下载资源。
- 仅发布 `ClashRouteCraft-win-x64.zip` 和 `SHA256SUMS.txt`。先上传草稿，再下载核对内容，最后公开。GitHub 自动提供的 Source code 归档不是额外桌面版本。

Checks cover version agreement, tracked private/ignored files, common credential patterns, core/UI/installer regressions, native smoke tests, first-run recovery, an exact public archive inventory, hashes and offline documentation links. CI without Mihomo skips real-core tests. Only the ZIP and its checksum are published, after a draft upload/download verification. GitHub's automatic source archives are not additional desktop editions.

隐私扫描是防误操作门槛，不能识别任意形式的秘密或截图中的所有文字。提交前仍需人工检查源码、模板和截图；截图仅使用 [独立演示夹具](screenshots/README.md)，不截取真实项目。忽略规则不能清除历史中已经泄露的数据。

Privacy scanning is a guard against mistakes, not a guarantee against every possible secret or image content. Review source, templates and screenshots before committing. Use only the [isolated demo fixtures](screenshots/README.md). Ignore rules cannot erase previously committed secrets.

## 环境与权限 / Environment and permissions

GitHub Actions 需在仓库启用。构建使用 .NET 10、Python 3.12、Windows 2022 的 C++ 构建工具，官方 Actions 固定到提交 SHA。构建任务只有读取权限；只有标签发布任务获得 `contents: write`，使用自动提供的 `GITHUB_TOKEN`，无需个人访问令牌。组织策略或标签规则仍可能限制发布。手动构建附件保留 7 天，Release 附件长期保留。

Enable GitHub Actions in the repository. The workflow uses .NET 10, Python 3.12, Windows 2022 C++ tools and commit-pinned official actions. Build jobs are read-only; only the tag publishing job receives `contents: write` through the automatic `GITHUB_TOKEN`. No personal access token is needed. Organization policy and tag protections still apply. Manual-build artifacts expire after seven days; release assets persist.

本地复现应使用独立干净检出和 PowerShell 7，安装测试依赖后运行。脚本在独立构建暂存目录编译，不覆盖常用 AOT 安装目录；会明确生成一个分发 ZIP。

For a local reproduction, use a separate clean checkout and PowerShell 7. The script builds in a separate staging directory and explicitly creates one ZIP; it does not replace your regular AOT installation.

```powershell
python -m pip install 'paramiko>=3.5,<5'
pwsh -File scripts/Build-Release.ps1
```

参考 / References: [Native AOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) · [Workflow permissions](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#permissions)
