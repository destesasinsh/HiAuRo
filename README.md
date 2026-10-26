# HiAuRo

FFXIV Dalamud 战斗辅助框架（.NET 10，Dalamud.CN.NET.Sdk 15.0.0）。

不是职业循环 — 提供运行时、数据层、ACR 接口、执行轴、事实轴、决策层和 Web 编辑器。

[![Build](https://github.com/destesasinsh/HiAuRo/actions/workflows/ci.yml/badge.svg)](https://github.com/destesasinsh/HiAuRo/actions)

## ACR 开发者 — 一键开始

详见 **[ACR 作者上手指南](public-docs/ACR_AUTHOR_GUIDE.md)** — 从零写出职业循环的完整教程。

```bash
dotnet add package HiAuRo.Sdk
```

如果需要职业数据辅助库，在 ACR 仓库中以 submodule 方式引用 `HiAuRo.Helper`，并保持 `HiAuRo.Sdk` 版本与宿主一致。

```csharp
using HiAuRo.ACR;
using HiAuRo.Helper;

public class MyAcr : IRotationEntry
{
    public IEnumerable<Jobs> TargetJobs => [Jobs.BRD];
    public Rotation? Build(string settingFolder) { return new Rotation(); }
}
```

推荐部署结构：

```text
<HiAuRo配置目录>/ACR/<目录名>/
  <目录名>.dll
  <目录名>.json
```

`<目录名>.json` 和 `<目录名>.dll` 需要与目录名一致。HiAuRo 也仍兼容旧的手动作者目录模式（例如 `ACR/<作者名>/<作者名>.dll`），但新的下载安装与示例工程默认都使用同名 `.json + .dll` 模式。

作者源地址示例（可直接添加到 HiAuRo 源管理页）：

```text
https://raw.githubusercontent.com/destesasinsh/HiAuRo-SampleACR/master/publisher.json
```

## 编译

```bash
git clone --recurse-submodules https://github.com/destesasinsh/HiAuRo
cd HiAuRo
dotnet build HiAuRo.slnx -c Release
```

### 同时开发 HiAuRo 和 ACR

如果同时修改 HiAuRo 本体和 ACR，等 NuGet 更新太慢。可以直接引用本地编译的 `HiAuRo.dll`，跳过 SDK 包：

```xml
<!-- 用直接引用替换 PackageReference HiAuRo.Sdk -->
<Reference Include="HiAuRo">
    <HintPath>..\HiAuRo\HiAuRo\bin\x64\Debug\HiAuRo.dll</HintPath>
    <Private>false</Private>
</Reference>
```

`Private=False` 确保 ACR 输出目录不复制 `HiAuRo.dll`，运行时用宿主已加载的那份。

> 同理，Helper 项目也可以临时换成直接引用 `HiAuRo.dll`，但注意编译器会沿 public API 拉入 OmenTools / Dalamud 等传递依赖，需要确保这些 DLL 在编译路径中可达。

> HiAuRo.Helper 是独立仓库，宿主编译时不依赖。运行时通过 `HelperUpdater` 自动拉取最新 DLL，`ACRLoader` 会把 ACR 对 `HiAuRo.Helper` 的引用解析到同一份已加载程序集。

## HiAuRo.Helper 共存模式

ACR 可以同时引用 `HiAuRo.Sdk` 和 `HiAuRo.Helper`：

```xml
<ItemGroup>
    <PackageReference Include="HiAuRo.Sdk" Version="0.2.11">
        <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
</ItemGroup>

<ItemGroup>
    <Compile Remove="Helper\**" />
    <None Remove="Helper\**" />
    <Content Remove="Helper\**" />
</ItemGroup>

<ItemGroup>
    <ProjectReference Include="Helper\HiAuRo.Helper\HiAuRo.Helper.csproj">
        <Private>False</Private>
    </ProjectReference>
</ItemGroup>
```

`ExcludeAssets=runtime` 避免 ACR 输出第二份 `HiAuRo.dll`，`Private=False` 避免 ACR 输出第二份 `HiAuRo.Helper.dll`。游戏内由宿主加载并共享 Helper。

## 项目结构

```
HiAuRo/              ← 主插件
├── ACR/             ← 接口 + Helper + Slot 系统 + 目标解析器
├── Command/         ← /hi 命令处理
├── Data/            ← 游戏数据层（战斗、对象、队伍、目标）
├── Execution/       ← 执行轴 + 触发器元数据 + 脚本编译器
├── Runtime/         ← 运行时核心、AIRunner、ACR 生命周期、法术队列
├── UI/              ← Web UI（Kestrel + CEF）+ ImGui 覆盖层
├── FactAxis/        ← 事实轴（法术表、时间线、事实节点）
├── Decision/        ← 决策引擎 + 决策类型
├── Authoring/       ← 编辑器后端
├── Infrastructure/  ← 日志、配置、Browsingway IPC
├── Recording/       ← 战斗录制
└── Setting/         ← 设置管理器

OmenTools/           ← Dalamud 服务封装（submodule）
Browsingway/         ← CEF 渲染参考（submodule）
```

## 命令

| 命令 | 说明 |
|------|------|
| `/hi on/off/toggle` | 启停 ACR |
| `/hi status` | 查看状态 |
| `/hi fact` | 切换事实轴 |
| `/hi assist load/unload` | 辅助轴加载/卸载 |
| `/hi reload` | 重新扫描 ACR |

## 相关仓库

| 仓库 | 说明 |
|------|------|
| [HiAuRo-SampleACR](https://github.com/destesasinsh/HiAuRo-SampleACR) | 示例 ACR 实现 |
| [HiAuRo.Helper](https://github.com/destesasinsh/HiAuRo.Helper) | 全职业数据辅助库 |
| [HiAuRo.Sdk](https://www.nuget.org/packages/HiAuRo.Sdk) | ACR 开发 NuGet 包 |
