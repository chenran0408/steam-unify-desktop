# 构建 Steam 统译 2.8

在 Windows x64 上安装 .NET 10 SDK，从源码包根目录运行以下 PowerShell 命令。程序项目只使用 .NET 和 WPF。第一次自包含发布时，.NET SDK 会下载目标平台的运行时包。

```powershell
dotnet publish .\SteamUnify2\SteamUnify.csproj -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.15 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o .\wpf-28-publish
```

运行 `wpf-28-publish\SteamUnify.exe` 即可打开程序。发布目录内的 `Resources` 需随 EXE 一起分发。本版随程序提供 .NET 10.0.15 桌面运行时。

## 验证

分类和联网测试默认使用模拟响应。添加 `-- --live` 可以查询一个公开的游戏标题。

```powershell
dotnet run --project .\suggestion-tests\SuggestionsTests.csproj -c Release
```

添加 `-- --benchmark` 可运行固定延迟的翻译测试，分别测量官方译名可提交和全部译名候选完成的时间。

以下脚本使用 Python 3 标准库生成模拟 Steam 缓存。请使用空的测试目录。先生成并验证缓存样本，再用它们运行原生文件读写、备份和还原测试。

```powershell
dotnet build .\cache-tests\CacheTests.csproj -c Release
python .\cache-tests\verify.py .\cache-tests\bin\Release\net10.0\CacheTests.dll .\test-output\cache
dotnet run --project .\io-tests\IoTests.csproj -c Release -- .\test-output\cache\v29
dotnet run --project .\io-tests\IoTests.csproj -c Release -- .\test-output\cache\v30
```

桌面验收覆盖两种缓存格式、单游戏库和 2500 项游戏列表、按行与范围勾选、多行 AppID 粘贴、页内预览与编辑、两阶段翻译和写入后的核对入口，以及还原和动态收藏。请在已解锁的 Windows 桌面运行。常规场景运行发布版 EXE。`--replay` 另从同一份窗口源码生成测试程序，供 `receipt` 场景按指定时机返回本地机翻结果。

```powershell
python .\acceptance\fixtures.py .\test-output\desktop --replay
dotnet run --project .\acceptance\Acceptance.csproj -c Release -- .\wpf-28-publish .\test-output\desktop direct selection large v29 workspace facets workbench advanced v30 undo dynamic receipt
```

动态收藏使用 Steam 的「玩过」或「从未玩过」条件。为核对筛选范围，测试将程序在模拟账号中写出的收藏记录交给 Steam 客户端随附的 `steamui/sp.js`（构建 11229305）处理。Steam 的读取、筛选和更新函数覆盖了已安装、未安装、玩过、从未玩过以及稍后加入的新条目。

## 打包

按上述命令完成发布并关闭程序后，运行以下命令。打包脚本将 Windows x64 便携版和源码分别写入指定目录，并校验 ZIP 中所有文件。脚本会从发布使用的本地 NuGet 运行时包读取许可文本。

```powershell
python .\pack.py --publish .\wpf-28-publish --output .\release
```

移植参考的上游提交为 `b8f371bd0e93144a08fa5f7f3e93f6d8b36e3d85`。[上游源码](https://github.com/moutou232/steam-unify/tree/b8f371bd0e93144a08fa5f7f3e93f6d8b36e3d85)
