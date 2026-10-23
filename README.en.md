# ClashRouteCraft

[中文](README.md) · **English**

Combine subscription nodes, static residential proxies, and your own routing rules into inspectable, exportable Clash / Mihomo configurations.

**[Download the latest Windows x64 AOT build](https://github.com/QingC2206/app-clash-route-craft/releases/latest/download/ClashRouteCraft-win-x64.zip)** · [Releases & changelog](https://github.com/QingC2206/app-clash-route-craft/releases/latest) · [SHA-256 checksums](https://github.com/QingC2206/app-clash-route-craft/releases/latest/download/SHA256SUMS.txt)

Current release: **[v1.16.5](https://github.com/QingC2206/app-clash-route-craft/releases/tag/v1.16.5)**. This portable Windows x64 application requires no separate .NET installation. It generates configuration files; your Clash / Mihomo client or MC2 router handles the actual traffic.

![Project overview: subscriptions, residential exits, routing, DNS and configuration outputs](docs/screenshots/overview.png)

> Screenshots render the actual UI in an isolated temporary workspace with fictional projects, example subscriptions, and reserved test addresses. No personal workspace was captured. The application UI is currently in Chinese; this README provides English guidance.

## Install and get started

1. Download the ZIP and **extract the entire archive** to a writable directory. Run `ClashRouteCraft.exe`. Do not run inside the ZIP or copy only the EXE; keep the accompanying DLLs.
2. First launch creates a generic read-only `Example` and default templates. Create a project. Under **订阅与入口 (Subscriptions & entry)**, fetch a subscription or import a complete Clash / Mihomo configuration.
3. By default, the **first enabled subscription supplies the rules**; other subscriptions supply nodes only. Residential proxies are optional. Without one, use the subscription entry directly.
4. Add direct, proxy, or block rules as needed. DNS remains client-managed by default; keeping that default is a good starting point.
5. Select a target under **生成配置 (Generate configuration)**, check and generate, then open **主配置输出 (Main configuration output)**. Import `HomeChain.yaml` into your client. Generation does not automatically apply anything to a router.

Mihomo validation binaries and GeoData are not bundled; download optional resources through the app when needed. MC2 installation packages are outside the downloader's scope. A **nodes-only cache cannot serve as the primary rule source**: fetch or import the complete configuration first.

## Core features

### Multiple subscriptions, one rule baseline

![Two example subscriptions, with the first supplying the routing baseline](docs/screenshots/subscriptions.png)

The first enabled complete subscription supplies routing conditions and their original order. Other subscriptions contribute nodes without mixing in another rule set. Move subscriptions up or down to choose the rule source.

**机场入口 (Subscription entry)** offers an aggregate group across subscriptions and a group for each subscription. Default filtering includes Hong Kong, Japan, Taiwan, and Singapore; filters and latency checks are configurable. Required node-resolution `hosts` mappings survive caching and generation. Conflicting mappings or alias cycles produce errors instead of silent overwrites.

### Optional residential proxies, one proxy exit

![Residential proxy editor with a reserved test address, fictional account and masked password](docs/screenshots/residential.png)

Proxied traffic uses **代理出口 (Proxy exit)**. In the client dashboard, choose **住宅出口 (Residential exit)** or **机场入口 (Subscription entry)**. A configured residential exit is the default; otherwise the subscription entry is used.

| Path | How it connects |
| --- | --- |
| `[链式] node name` — chained | Via a subscription node to the residential proxy; exits from the residential IP |
| `[直连] node name` — direct connection | Straight to the residential proxy; still exits from the residential IP |
| Routing action `DIRECT` | Local network, without a residential or subscription proxy |

“Direct” on a residential node describes **how the residential proxy is reached**. It differs from the routing action `DIRECT`. Residential UDP and TLS settings are preserved; availability depends on the provider and connection path.

### Three routing actions; your rules come first

![Subscription baseline, ASN dependency controls and three routing actions](docs/screenshots/routing.png)

Subscription actions become **local direct, proxy, and block**. Rejection and ad-blocking rules become block actions. Proxied traffic shares one exit. Unmatched traffic keeps the primary subscription's final action, without an additional catch-all policy group.

![Custom direct, proxy and block groups with priority controls](docs/screenshots/custom-rules.png)

Matching order is **all custom rules → primary subscription rules**. Custom groups run from top to bottom. Every manual, URL-based, and existing imported rule in a higher group precedes the next group. Use the arrows to reorder groups. Conflicting fallback actions are not removed through deduplication, and continuation semantics such as `PASS` are preserved.

For example, to allow a domain covered by a block rule, place its direct or proxy group above the block group. You can also disable custom additions while keeping their contents for later use.

**保存为 Example 规则 (Save as Example rules)** and **应用 Example 规则 (Apply Example rules)** reuse rule templates while preserving subscriptions, nodes, and DNS. Advanced custom mode imports rule sets from files, URLs, pasted text, or fetched subscriptions with policy mapping. The subscription baseline does not require re-importing its own rules.

### Client-managed DNS by default; optional privacy enhancement

![Optional Fake-IP DNS privacy enhancement with residential DNS egress](docs/screenshots/dns-privacy.png)

The default leaves DNS to the client / MC2. Optional **Fake-IP DNS privacy enhancement** uses encrypted queries and routes proxy DNS through the residential exit by default, with subscription entry available as an alternative. It adds no domain databases and does not change QUIC. The screenshot shows it enabled manually; **it is off by default**.

This can reduce local DNS exposure for proxied domains. Trade-offs include potentially slower initial queries or connections, dependence on the selected exit, and compatibility issues with some applications. It does not guarantee bypassing platform risk checks or control a browser's independent DoH.

**MC2 requires a separate DNS application step.** Apply `HomeChain.MC2DNS.yaml` following the generated instructions. When disabling enhancement, restore MC2's default or your previously saved DNS too; importing the main YAML alone will not undo DNS changes. A version-specific stock Fake-IP recovery template from MC2 1.2.2 ARM64 is also exported. See the [usage and recovery guide (Chinese)](docs/usage.md).

### Inspect dependencies and understand the output

![Output summary showing rule source, DNS mode and actual validation status](docs/screenshots/output.png)

Reports show the rule source, DNS mode, local validation status, and target database dependencies. The screenshot fixture has no validation core, so it honestly reports that only structural checks ran. **A successful local check is not proof of connectivity on the router.**

For MC2, ASN controls let you exclude ASN rules by source or explicitly retain them after preparing the target database. Main outputs include:

| File / directory | Purpose |
| --- | --- |
| `HomeChain.yaml` | Main configuration for Clash / Mihomo / MC2 |
| `HomeChain.report.json` | Generation and validation report |
| `HomeChain.MC2DNS.yaml` | MC2 companion file when the corresponding DNS configuration is enabled |
| `HomeChain.MC2DNS.Default.yaml` | Version-specific stock DNS recovery template for the MC2 target |
| `HomeChain.DNS-README.txt` | MC2 DNS application and recovery instructions |
| `router-asn/` | Standalone ASN tools and instructions |

ASN tools support PowerShell and Linux / macOS terminals with Python 3.10+. They authenticate before downloading, upload over SSH, verify SHA-256, validate with the router's core, and support backups and rollback. They do not automatically restart MC2. SSH host records are isolated by project: different routers sharing a private IP do not interfere, but a changed key within one project is still rejected. See the [ASN setup guide (Chinese)](docs/router-asn.md).

## Upgrade without losing data

1. Close ClashRouteCraft and back up your workspace.
2. Extract the new ZIP into a temporary directory. Copy program files, DLLs, and accompanying documentation into the existing installation, replacing matching files.
3. **Keep** `config/`, `settings/`, `output/`, `resources/`, `.cache/`, `.project-trash/`, and other data directories. Do not delete the whole installation to upgrade.
4. Reopen the app. Regenerate a project to update its exported tools; login file `router-login.json` and project SSH records in `router-known_hosts` are preserved. Apply the generated configuration in the client for it to take effect.

The default workspace is local to the application directory. A new installation directory creates a separate workspace. To migrate existing data, back it up first and use the app's workspace migration action. The portable app moves data into its new EXE directory rather than simply opening the old location; it never searches or migrates other installations automatically. Old nodes-only caches cannot recover lost rules or `hosts`; fetch the complete subscription again when needed.

## Privacy and scope

- Templates contain generic placeholders only. Downloads exclude personal projects, subscriptions, node credentials, outputs, SSH login files, and downloaded resources.
- `.gitignore` excludes runtime data, login files, SSH host records, and macOS metadata. Ignore rules do not remove secrets already committed; review staged content before publishing.
- Optional `router-login.json` stores login details as local plaintext. Keep it private.
- Desktop releases currently target Windows x64. Linux / macOS support refers to the standalone ASN scripts, not a desktop download.
- Generation, builds, and upgrades do not automatically modify a router. Verify dependencies and connectivity on the actual device.

## Development and validation

The app uses .NET 10 and Avalonia. Development requires the .NET 10 SDK; Windows Native AOT publishing also requires Visual Studio C++ Build Tools and the Windows SDK.

```powershell
dotnet build ClashRouteCraft.slnx -c Release
dotnet run --project src/ClashRouteCraft.Desktop -c Release
dotnet publish src/ClashRouteCraft.Desktop -c Release -r win-x64 --self-contained true -p:PublishAot=true -o artifacts/publish/win-x64-aot
```

```text
src/                       Core library and desktop application
tests/                     Regression tests and synthetic fixtures
scripts/                   Build, packaging and standalone tools
tools/python-reference/    Historical Python reference implementation
docs/                      Guides, validation notes and screenshots
```

See [validation notes (Chinese)](docs/validation.md) for test commands, real-core checks, and known differences from the historical Python reference. Screenshot provenance and reproduction commands are in the [screenshot notes](docs/screenshots/README.md).

Only run packaging when creating a distribution. Ordinary AOT builds do not create ZIPs. Packaging produces one Windows x64 edition and excludes workspace data.

```powershell
pwsh -File scripts/Prepare-Notices.ps1
pwsh -File scripts/Package.ps1
python scripts/check-release.py
```

## Documentation and license

[Usage (Chinese)](docs/usage.md) · [ASN setup (Chinese)](docs/router-asn.md) · [Validation (Chinese)](docs/validation.md) · [Report an issue](https://github.com/QingC2206/app-clash-route-craft/issues)

Source code is under the [MIT License](LICENSE). Third-party components retain their own licenses; see [third-party notices](THIRD-PARTY-NOTICES.md).
