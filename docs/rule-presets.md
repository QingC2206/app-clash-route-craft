# 规则预设 / Rule presets

预设只是向现有项目追加规则的参考目录，不是新的规则生成模式。界面不写死服务分类，直接读取工作区 `settings/rule-presets.yaml`。

1. 进入“规则与 DNS → 分流规则 → 添加规则预设”。
2. 搜索或多选预设，可展开查看 URL 和匹配条件。
3. 选择“直连 / 代理 / 拒绝”，查看追加数量、重复和跨组提示。
4. 点击“确认追加”，再保存项目并生成。取消不会修改项目。

同组相同 URL 和匹配条件会跳过；其他组的条目不会删除或挪动。冲突继续按现有组顺序处理；此处不检查域名父子范围等所有潜在重叠。个人附加规则关闭时，可只保存名单并保持停用，也可明确勾选启用（原有个人规则也会随之启用）。

## 配置格式

```yaml
version: 1
presets:
  - id: my-service
    category: 自定义分类
    name: 我的服务
    description: 可选说明；注明名单范围或限制
    urls:
      - https://example.com/service-rules.yaml
    domains:
      - example.org
    rules:
      - DOMAIN,api.example.net
      - IP-CIDR,192.0.2.0/24,no-resolve
```

- `id` 唯一，`name` 非空。分类按文件中的顺序显示。
- `urls` 是 HTTP(S) **规则名单**地址，不是服务官网地址；支持原有 URL 解析器接受的文本或 YAML 名单。仅添加引用，校验或生成时读取名单。
- `domains` 是裸域名，追加为 `DOMAIN-SUFFIX`，包含该域名及所有子域名。精确域名请在 `rules` 中使用 `DOMAIN`。
- `rules` 是不带动作的匹配条件。动作在界面统一选择。支持现有规则语法，不允许 `MATCH` / `FINAL`；兜底在项目中设置。
- `urls`、`domains`、`rules` 可混用，至少有一项非空。没有选择的预设不会生效。
- 编辑后点击“重新加载”或重新打开选择器；文件错误时提示修正，不偷偷退回内置内容。
- 删除文件后重新打开选择器或重启应用会恢复内置模板；已有文件不会被启动或升级覆盖。
- 预设确认追加后与目录脱离：更新目录不会修改已添加的项目规则。需要时再次选择添加，并在原有规则编辑区删除不再需要的旧条目。

## 来源与范围

AI、购物和多数流媒体 URL 使用 [MetaCubeX/meta-rules-dat](https://github.com/MetaCubeX/meta-rules-dat)；腾讯视频使用 [blackmatrix7/ios_rule_script](https://github.com/blackmatrix7/ios_rule_script)；微信采用 [Lanlan13-14/Rules](https://github.com/Lanlan13-14/Rules) 的纯域名名单，避免扩大到整个 ASN。这里只分发 URL 引用，不内置这些第三方名单的快照；名单会随上游维护变化，生成继续遵守项目的缓存和 ASN 策略。

UU 远程 / ToDesk 提供官网基础域名（`uuyc.163.com`、`todesk.com`），不声称覆盖全部服务连接。常见远程桌面进程名名单适用于运行应用的电脑，路由器无法匹配下游电脑的进程名，因此默认预设没有使用它。P2P 纯 IP 流量仍可能走兜底动作，可根据实际连接补充规则；此功能不修改 UDP 或 QUIC。

## 补充服务

URL 校验每批最多并发 4 个请求，共用 HTTP 连接池。相同 URL 和请求头只下载一次，但每行仍按自己的格式选项校验；失败和异常域名提示按原始行号显示。再次点击可取消。每批实时请求，不使用或写入生成缓存，也没有固定等待或自动重试；单条请求默认超时 15 秒，可由 HTTP 设置调整，校验上限为 30 秒。

预设中的腾讯视频采用 `TencentVideo_No_Resolve.yaml`，与上游 `.list` 的规则和 `no-resolve` 参数保持一致。YAML 与文本名单均受支持，不能只根据扩展名判断内容等价；其他来源切换格式前也应比较实际匹配条件。

补充服务按中国常用、购物与生活、中国流媒体、办公协作、地图出行、存储与远程访问、金融证券分类。小红书、高德地图、长桥和 IBKR 使用 MetaCubeX 的专项域名 URL；淘宝沿用购物组的阿里名单，其范围也包含支付宝等其他阿里业务。已有购物、流媒体和飞书预设不重复新增。

极空间暂未找到可信的独立专项规则 URL，使用 [极空间官网](https://www.zspace.cn/) 可核实的 `zspace.cn`、`zconnect.cn` 基础域名，不引入混杂其他服务的大型直连名单；不保证覆盖全部 P2P 纯 IP 连接。预设支持域名条目，因此无需为此修改生成逻辑。金融服务名单同样只是域名参考，不能保证交易终端所有连接都被覆盖。

## English

URL checks run at most four requests concurrently and reuse HTTP connections within each batch. Identical URLs and request headers share one download, while parsing options and results remain per row. Checks are fresh, cancellable, and do not read or write generation caches. There is no fixed delay or automatic retry. The default request timeout is 15 seconds, capped at 30 seconds for validation. The Tencent Video preset uses `_No_Resolve.yaml` to preserve the original text list's IP rule semantics.

Xiaohongshu, Amap, Longbridge and IBKR use dedicated MetaCubeX domain-list URLs. Existing shopping, streaming and Feishu entries are reused. Taobao is covered by the broader Alibaba list, which also includes other Alibaba services. ZSpace uses the verified base domains `zspace.cn` and `zconnect.cn` because a reliable standalone rule-list URL was not found; pure-IP P2P connections may require separate rules. Finance presets likewise do not claim complete coverage of trading-terminal connections.

This is a local suggestion catalogue, not a new routing engine. Select presets, choose direct/proxy/block, preview, and confirm to append. Save and regenerate afterward. Cancelling leaves the project intact. Same-group duplicates are skipped; entries in other groups remain, with existing group order deciding precedence. The preview detects identical entries, not every overlapping domain range.

Edit `settings/rule-presets.yaml` and reload the picker. Use schema `version: 1` and unique preset `id` values. Each entry has a `name`, optional `category` / `description`, and any combination of `urls`, `domains`, and `rules`. URLs must point to HTTP(S) rule lists, domains include subdomains, and rules contain match conditions without an action. Catch-all rules belong in the project, not the catalogue.

Missing catalogue files are restored; existing files are never replaced on upgrade. Invalid files produce an error instead of silently loading defaults. Catalogue edits do not rewrite already-added project rules. Downloads use the existing rule parser, cache and ASN policy. Remote-access domain presets are only a starting point; pure-IP P2P traffic can still use the fallback, and downstream process-name rules do not work on a router.
