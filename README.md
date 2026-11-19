# ADOFAI-Angle
Optimize the editor experience in terms of copy-paste and orbit angle.


# Angle

**0.1.0-beta** · A Dance of Fire and Ice


## 使用

- 定向粘贴、原地转向与覆盖粘贴。
- 保拍，可选择自动排版或变速补偿, 意在粘贴后自动优化键型, 保持节奏不变, 减少人工成本。
- 键型剪贴板支持保存、浏览、预览、搜索和整理。
- 轨道角度显示、重叠轨道层级、相对角度放置与视角控制。

## 测试版说明
保拍取决于衔接处与谱面事件；从文件载入的旧键型可能没有复制当时的计时信息。复杂段落无法完全保证保拍正确。[保拍与键型说明](docs/pattern-editing.md)。

## 从源码构建

需要 Windows、带 MSBuild 的 .NET SDK（或 Visual Studio Build Tools）、.NET Framework 4.8 开发者包，以及已安装 UMM 的游戏。构建时从本机游戏目录引用 Unity、游戏和 UMM 的程序集。

在仓库根目录的 PowerShell 中运行：

```powershell
$env:ADOFAI_GAME_PATH = 'C:\SteamLibrary\steamapps\common\A Dance of Fire and Ice'
dotnet build .\Angle.csproj -c Release
```

编译结果在 `bin/Release/Angle.dll`。也可运行 `pwsh -File .\pack.ps1`，在 `dist/` 生成可安装的 ZIP。构建所用的 UMM 版本为 0.32.5；其他游戏或 UMM 版本的兼容性未验证。

由AI辅助完成
