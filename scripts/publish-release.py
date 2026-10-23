"""Publish verified assets from a tag build; resume drafts, never replace public releases."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("guard", ROOT / "scripts/release-guard.py")
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


def gh(*args):
    return subprocess.check_output(["gh", *args], cwd=ROOT, text=True)


def main():
    guard.require(os.environ.get("GITHUB_EVENT_NAME") == "push" and os.environ.get("GITHUB_REF_TYPE") == "tag", "Publishing requires a pushed version tag")
    tag = os.environ["GITHUB_REF_NAME"]
    guard.version(tag=tag)
    repo = os.environ["GITHUB_REPOSITORY"]
    archive = ROOT / f"releases/{guard.NAME}.zip"
    digest = guard.archive(archive, ROOT)
    checksum = ROOT / "releases/SHA256SUMS.txt"
    guard.require(checksum.read_text() == f"{digest}  {archive.name}\n", "Asset checksum mismatch")
    # Handle annotated tags as well as lightweight tags.
    target = gh("api", f"repos/{repo}/commits/{tag}", "--jq", ".sha").strip()
    local = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    guard.require(target == local, "The remote tag moved after this build")
    # Listing first distinguishes a missing release from network/authentication errors.
    releases = json.loads(gh("api", "--paginate", "--slurp", f"repos/{repo}/releases?per_page=100"))
    existing = next((r for page in releases for r in page if r["tag_name"] == tag), None)
    if existing and not existing["draft"]:
        raise ValueError("A public Release already exists. It was not overwritten; use a new version tag.")
    body = f"""## 中文

Windows x64 **Native AOT** 便携版，仅一个版本，无需安装 .NET。

- 下载并解压 `ClashRouteCraft-win-x64.zip`，运行 `ClashRouteCraft.exe`。
- 包内包含中文 `README.md`、English `README.en.md`、7 张演示截图及使用指南，离线可阅读。
- 升级前关闭程序并备份工作区；覆盖程序和随包文档，保留 `config/`、`settings/`、`resources/`、`output/`、`.cache/`、`.project-trash/`。
- 不包含个人项目、订阅、节点凭据、SSH 账户、主机记录或下载资源；首次启动创建通用示例。不会自动修改路由器。
- 用 `SHA256SUMS.txt` 校验下载文件。构建已通过核心、界面、工具、AOT 自检和发布包检查；不等同于路由器联网验证。

## English

One portable **Windows x64 Native AOT** edition; no .NET installation required.

- Extract `ClashRouteCraft-win-x64.zip` and run `ClashRouteCraft.exe`.
- Includes Chinese / English READMEs, seven synthetic demo screenshots and offline guides.
- Before upgrading, close the app and back up your workspace. Replace program files and bundled documentation; preserve the six data directories listed above.
- No personal projects, subscriptions, node credentials, SSH login files, host records or downloaded resources are bundled. First launch creates generic templates. Routers are never updated automatically.
- Verify the download using `SHA256SUMS.txt`. Automated checks cover core/UI/tool tests, the native executable and package; they do not test router connectivity.

[中文说明](https://github.com/{repo}/blob/{tag}/README.md) · [English guide](https://github.com/{repo}/blob/{tag}/README.en.md)

Commit: `{local}`
"""
    with tempfile.TemporaryDirectory(prefix="crc-release-") as temporary:
        notes = Path(temporary) / "notes.md"
        notes.write_text(body, encoding="utf-8")
        if existing:
            gh("release", "edit", tag, "--notes-file", str(notes))
        else:
            options = ["--prerelease"] if "-" in tag else []
            gh("release", "create", tag, "--verify-tag", "--draft", "--title", f"ClashRouteCraft {tag}", "--notes-file", str(notes), *options)
        gh("release", "upload", tag, str(archive), str(checksum), "--clobber")
        release = json.loads(gh("release", "view", tag, "--json", "assets"))
        assets = {a["name"]: a for a in release["assets"]}
        guard.require(set(assets) == {archive.name, checksum.name}, "Unexpected assets in draft; inspect before publication")
        # Download the uploaded bytes before making the release public.
        gh("release", "download", tag, "--dir", temporary)
        for path in (archive, checksum):
            guard.require((Path(temporary) / path.name).read_bytes() == path.read_bytes(), "Uploaded asset verification failed")
        options = ["--latest"] if "-" not in tag else []
        gh("release", "edit", tag, "--draft=false", *options)
    print(f"Published https://github.com/{repo}/releases/tag/{tag}")


if __name__ == "__main__":
    main()
