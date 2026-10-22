# Python 参考实现

此目录包含独立的配置生成器和回归测试，用于与桌面端的 C# 实现对照。桌面程序运行不依赖 Python。

在仓库根目录安装依赖并运行测试：

```powershell
python -m pip install -r tools/python-reference/requirements.txt
python -m unittest discover -s tools/python-reference/tests -p "test_*.py"
python scripts/check-parity.py
```

对照测试要求先构建根目录的 `ClashRouteCraft.slnx`（Release 配置）。

直接使用生成器时，通过 `--workspace` 指定自己的工作区。省略时仅使用本目录，不搜索桌面发布目录或旧工作区。个人数据、生成结果和下载资源不应提交。
