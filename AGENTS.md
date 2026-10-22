# 构建与打包边界

- 用户日常只使用 Windows x64 Native AOT 版本。后续涉及程序变更的编译交付，默认执行 `dotnet publish src/ClashRouteCraft.Desktop -c Release -r win-x64 --self-contained true -p:PublishAot=true`，并更新 `artifacts/publish/win-x64-aot/`，无需再次询问是否生成 AOT。
- 必要的中间构建、测试和静态检查仍可使用 `bin/obj`；完成后应验证实际 AOT 程序，不能只交付普通 Release 构建。
- 更新 AOT 时先在临时目录编译和验证，再只替换程序文件。保留原 AOT 目录内的项目、订阅缓存、节点、设置、下载资源及生成结果。
- AOT 编译不等于压缩打包。只有用户明确要求打包或发布分发包时，才运行 `scripts/Prepare-Notices.ps1`、`scripts/Package.ps1` 及发布包验证；不要额外生成普通发布版本、发布目录或 ZIP。
- `scripts/check-package-data.ps1` 会在临时目录中创建压缩包，同样属于打包验证。
- 发布脚本只生成一个 `ClashRouteCraft-win-x64` 版本。
- 不得将真实项目、订阅、节点凭据或工作区缓存加入源码或发布包；启动隔离应保持有效。
