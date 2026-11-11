# 构建

玩家请从发行版下载便携包，无需构建。

源码构建使用 Windows、PowerShell 7 和 .NET 10 SDK。

在仓库目录运行：

```powershell
dotnet restore src/Companion.Diagnostics/Companion.Diagnostics.csproj
& ./scripts/Build.ps1 -Publish -OutputDirectory artifacts/local-build
```

输出目录必须为空，不覆盖正在运行的助手或恢复备份。

自包含包只需要一个 QATK.exe 启动，说明与第三方许可证一并保留。

测试可使用源码中的自检及 tests 目录下的合成验证工程；这些不是玩家启动入口，也不能替代真实游戏验收。

游戏账号、存档、私人证据、构建缓存和密钥不属于源码分发内容。
