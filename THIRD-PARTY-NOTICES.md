# Third-party components

This package contains a local build of ClashRouteCraft and the following third-party software. Their respective licenses continue to apply. License texts collected for this distribution are in `licenses/`.

| Component | Version used | Project / source | License |
| --- | --- | --- | --- |
| .NET runtime / Native AOT | 10.0.13 | https://github.com/dotnet/runtime | MIT and included third-party notices |
| Avalonia | 11.3.22 | https://github.com/AvaloniaUI/Avalonia | MIT |
| AvaloniaEdit | 11.4.1 | https://github.com/AvaloniaUI/AvaloniaEdit | MIT |
| YamlDotNet | 18.1.0 | https://github.com/aaubry/YamlDotNet | MIT |
| SkiaSharp | 2.88.9 | https://github.com/mono/SkiaSharp | MIT and included third-party notices |
| HarfBuzzSharp | 8.3.1.1 | https://github.com/mono/SkiaSharp | MIT and upstream notices |
| ANGLE native renderer | Avalonia runtime dependency | https://github.com/google/angle | BSD-style and upstream notices |
| Mihomo (optional separate download) | Meta v1.19.28 | https://github.com/MetaCubeX/mihomo/tree/v1.19.28 | GPL-3.0 |

Starting with 1.12.0, distribution ZIPs do not contain Mihomo, GeoData or an MC2 installation archive. The application's optional resource downloader obtains Mihomo v1.19.28 from MetaCubeX/mihomo releases and GeoData from MetaCubeX/meta-rules-dat releases, verifying the SHA-256 digest supplied by GitHub's release API. Those separately downloaded components retain their upstream licenses and notices; see their source repositories. MC2 installers are outside the downloader's scope.

Factory templates are embedded and contain no provider subscription rules or credentials. Personal configuration, subscription snapshots, generated outputs, caches, downloaded resources and local logs are excluded from distribution ZIPs.

The optional MC2 DNS recovery template reproduces the configuration values in `merlinclash/yaml_dns/fakeip.yaml` from `MC2_1.2.2_ARM64.tar.gz`. Original file SHA-256: `22c47f81d7f55960bc9400f58ab1c9a4741bc14d2aaadd743b633c2e727c3577`. Only this small configuration template is embedded; no MC2 installer, executable, domain database or provider subscription is bundled. Its existing `geosite:gfw` reference uses the database already supplied with MC2. The template is version-specific and is not a full router backup.
