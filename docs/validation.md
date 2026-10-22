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

仅在明确要求打包发布后，才执行以下发布验证（不属于日常构建验证）： `python scripts/check-release.py` 和 `pwsh -File scripts/check-package-data.ps1`，检查发布包及用户数据保留行为；其中打包回归脚本会在临时目录中创建测试压缩包。请先阅读脚本参数和前置条件。

可选的 `check-rule-semantics-v2.py`、`check-rule-generation-v2.py` 和 `check-domain-auto.py` 使用本地模拟服务验证规则行为，需要已构建的测试程序、发布版及独立下载到发布工作区 `resources/validation/mihomo.exe` 的内核。它们不是无需外部资源的单元测试。

验证报告、截图、生成配置与运行日志保存在忽略目录中，不作为源码发布。
