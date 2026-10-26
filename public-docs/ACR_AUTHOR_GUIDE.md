# HiAuRo ACR 作者上手指南

> **写给谁**：第一次在 HiAuRo 框架下写 ACR（Advanced Combat Rotation）的开发者。  
> **前置知识**：会用 C#、了解 FFXIV 战斗基本概念（GCD、oGCD、DOT、Buff、连击）。  
> **读完能干什么**：从零写出一个可以自动打循环的职业 ACR。

---

## 目录

1. [五分钟写出第一个 ACR](#1-五分钟写出第一个-acr)
2. [ACR 运行时全景图](#2-acr-运行时全景图)
3. [核心接口详解](#3-核心接口详解)
4. [数据层速览](#4-数据层速览)
5. [事件回调](#5-事件回调)
6. [工具类速查](#6-工具类速查)（含 6.7 SlotHelper + 6.8 MovementHelper + 6.11 HiAuRo.Helper）
7. [UI 注册](#7-ui-注册)
8. [高级特性](#8-高级特性)（含 8.7 身位指示器）
9. [实战技巧与常见错误](#9-实战技巧与常见错误)
10. [附录：接口速查表](#10-附录接口速查表)

---

## 1. 五分钟写出第一个 ACR

### 你要写什么

HiAuRo 的 ACR 是一个放在插件配置目录 `ACR/` 下的独立目录。**新推荐模式**是：

```text
ACR/<目录名>/
  <目录名>.dll
  <目录名>.json
```

框架会按同名规则自动发现并加载它。旧的手动作者目录模式仍兼容，但新的下载安装和示例工程都默认使用同名 `.json + .dll` 模式。

如果作者使用 `HiAuRo-SampleACR` 模板仓库，默认可直接提供的作者源地址示例为：

```text
https://raw.githubusercontent.com/destesasinsh/HiAuRo-SampleACR/master/publisher.json
```

一个最小 ACR 只需做三件事：

1. **实现 `IRotationEntry`** — 告诉框架"我是谁、我支持什么职业"
2. **在 `Build()` 中返回一个 `Rotation`** — 把技能槽位、事件处理器塞进去
3. **实现若干个 `ISlotResolver`** — 每个代表一个技能的"什么时候用/怎么用"

### Hello World：吟游诗人的强力射击

```csharp
using HiAuRo.ACR;
using static HiAuRo.Data;

namespace MyFirstACR;

// ① 入口类：实现 IRotationEntry
public class BRDRotationEntry : IRotationEntry
{
    public string AuthorName => "你的名字";
    public bool UseCustomUi => false;              // 用 HiAuRo 的 UI（推荐）
    public IEnumerable<Jobs> TargetJobs => [Jobs.BRD]; // 支持的职业

    public Rotation? Build(string settingFolder)
    {
        var rot = new Rotation
        {
            TargetJob = Jobs.BRD,
            MinLevel = 1,
            MaxLevel = 100,
            Description = "我的第一个 BRD ACR",

            // ② 按优先级列出所有技能槽位（排前面的先检查）
            SlotResolvers =
            [
                new() { Resolver = new HeavyShot(), Mode = SlotMode.Gcd },
                new() { Resolver = new Bloodletter(), Mode = SlotMode.OffGcd },
            ],
        };
        return rot;
    }

    // ③ UI（下一节细讲），暂时返回 null
    public IRotationUI? GetRotationUI() => null;
    public void OnDrawSetting() { }
    public void Dispose() { }
    public void OnEnterRotation() { }
    public void OnExitRotation() { }
}

// ④ 一个 GCD 槽位：强力射击
public class HeavyShot : ISlotResolver
{
    public int Check()
    {
        // 返回 >=0 表示"我想用"，框架会在 GCD 好了时调用 Build
        return 0;
    }

    public void Build(Slot slot)
    {
        // 填充一个技能：SkillID=97（强力射击），目标是当前目标
        slot.Add(new Spell(97, SpellTargetType.Target));
    }
}

// ⑤ 一个 oGCD 槽位：失血箭
public class Bloodletter : ISlotResolver
{
    public int Check()
    {
        // 冷却没转好就不打
        if (!SpellHelper.CanUseSpell(110))  // 110 = 失血箭
            return -1;
        return 0;
    }

    public void Build(Slot slot)
    {
        slot.Add(new Spell(110, SpellTargetType.Target));
    }
}
```

编译完成后，推荐放置为：

```text
HiAuRo插件配置目录/ACR/MyFirstACR/
  MyFirstACR.dll
  MyFirstACR.json
```

切到吟游诗人职业，框架会自动加载它。

> **关键理解**：`Check()` 返回值本身不影响优先级——优先级由 `SlotResolvers` 列表的**排列顺序**决定。排前面的先 `Check`，先通过就先执行。

### 基础循环多源码开发模式

如果只想快速调试 `SlotResolver` 的排列顺序和优先级，可以使用基础 ACR 开发模式直接加载一组 C# 源文件，不需要创建完整 ACR 项目，也不需要手动构建 DLL。

使用步骤：

1. 在任意目录创建一个或多个受信任的 `.cs` 文件。
2. 在 HiAuRo 中启用开发者工具，然后打开 **ACR → 基础 ACR 开发**。
3. 逐个添加 `.cs` 文件的绝对路径，并用每行的 **加载** 开关选择参与编译的文件。
4. 打开 **开发模式** 开关；开启时会立即编译并加载所有已启用文件。
5. 修改源码后，点击 **加载/重载** 或执行 `/hi reload` 使改动生效。

下面是一个可直接使用的吟游诗人示例：

```csharp
using System.Collections.Generic;
using HiAuRo.ACR;

public sealed class BasicBrd : IBasicAcrScript
{
    public Jobs TargetJob => Jobs.BRD;

    public IReadOnlyList<SlotResolverData> BuildSlotResolvers() =>
    [
        new() { Resolver = new HeavyShot(), Mode = SlotMode.Gcd },
    ];
}

public sealed class HeavyShot : ISlotResolver
{
    public int Check() => 0;

    public void Build(Slot slot)
    {
        slot.Add(new Spell(97, SpellTargetType.Target));
    }
}
```

所有已启用文件会作为同一个程序集的一组源码共同编译，因此可以把入口、Resolver 和辅助类型拆到不同文件中。整组源码仍必须包含且仅包含一个 public、非抽象的 `IBasicAcrScript` 实现；未启用的文件不会被读取或参与校验。

这个模式只提供 `TargetJob` 和 `SlotResolver` 列表，适合验证最基础的循环。它不支持 QT、Hotkey、Settings UI、EventHandler、Opener、SlotSequence、自定义触发器、NuGet、项目引用或额外 DLL。源码会作为受信任的本机代码运行，**不是沙箱**；不要加载来源不明的文件。

手动加载/重载以及启用/停用开发模式不受战斗、施法、切图、数据未就绪或活动 Slot 状态限制。操作会立即取消当前 Slot：尚未发出的动作停止，已经发送的技能无法撤回；旧脚本程序集会在取消中的 Slot 完全退出后释放。

开发模式开启时，`/hi reload` 只重编译 Basic ACR，不会重新扫描或回退到正式 ACR；开发模式关闭时，该命令扫描正式 ACR。首次开启时如果加载失败，开发模式仍保持启用并进入 Failed 状态，可修复源码后点击 **加载/重载** 或执行 `/hi reload`。

如果任一已启用文件缺失或读取失败，或者编译、入口识别、入口构造、Resolver 列表构建、应用中的任一步失败，整组源码都不会应用。开发模式会立即进入无 ACR 状态并停止技能输出：不会继续运行旧版本，也不会回退到正式 ACR。修复源码后手动点击 **加载/重载**，或者关闭 **开发模式** 恢复正式 ACR。

插件重启时，如果配置仍启用了开发模式，只等待 Helper 初始化完成就会自动加载所有已启用源码，不等待脱战或游戏数据就绪。切图期间可以完成编译并进入 Ready 状态；游戏数据恢复且当前职业可识别后，再加载匹配的 Rotation。这不是源码保存监听；运行期间修改 `.cs` 文件不会被自动监控，也不会自动重载。

---

## 2. ACR 运行时全景图

### 整体流程

```
游戏每帧 (≈60fps)
  │
  ▼
RuntimeCore.OnTick()
  │
  ├─ Data.IsReady 检查（登录 & 地图加载完毕）
  ├─ Coroutine 协程调度
  ├─ EventSystem 事件系统
  ├─ HotkeyPoller 热键轮询
  │
  └─ ACRLifecycle.Update()
       │
       ├─ 检测职业切换 → 加载/卸载 ACR
       │
       └─ 进战斗？───→ AIRunner.Update()
                         │
                         ├─ 刷新 Data.Objects / Data.Party
                         ├─ OnBattleUpdate(battleTimeMs) 事件回调
                         │
                         ├─ 执行轴 / 事实轴 / 辅助轴（Phase 6+）
                         ├─ Opener 起手序列
                         ├─ SpellQueue 待处理槽位
                         │
                         └─ AILoop_Normal.CheckAll() / Build() ◀── 核心循环
                              │
                              └─ 遍历 SlotResolvers 列表
                                   │
                                   ├─ Check() >= 0 ？
                                   ├─ 窗口匹配？（GCD已好 / oGCD可插入）
                                   │
                                   └─ 通过 → Build(slot) → SlotExecutor.Execute(slot)
                                        │
                                        ├─ BeforeSpell 回调
                                        ├─ 使用技能（UseAction）
                                        ├─ OnSpellCastSuccess 回调
                                        ├─ AfterSpell 回调
                                        └─ 处理 AppendedSequence
```

### 决策优先级（谁说了算）

当多个系统同时想放技能时，优先级如下：

```
1. 高优先级强制技能（Phase 6 执行轴指定）
2. 事实轴决策（Phase 7）
3. 辅助轴强制技能
4. Opener 起手序列
5. SpellQueue 待处理队列
6. AILoop 正常循环 ← 你的 ACR 在这里
```

也就是说，框架可能在你的 ACR 想要放技能之前，先插入了执行轴指定的技能。你可以通过 `CanUseHighPrioritySlotCheck` 拒绝这种插入（见高级特性）。

### SlotMode 窗口控制

每个 `SlotResolverData` 都有一个 `Mode`，决定 **Check 通过后要不要执行 Build**：

| Mode | 含义 | 何时 Build |
|------|------|-----------|
| `Gcd` | GCD 技能 | GCD 冷却完毕时 |
| `OffGcd` | 能力技 | GCD 窗口前段（剩余<750ms）或后段（已过1500ms），且当前 GCD 内能力技次数未达上限 |
| `Always` | 不限制 | 只要 Check 通过，立即执行 |

**能力技插入窗口示意图**：

```
|←────────────── 一个 GCD 周期 (约 2.5s) ──────────────→|
|                                                        |
|  前段（oGCD 窗口）         |  后段（第二个 oGCD 窗口）   |
|  elapsed < 1500ms           |  remaining < 750ms         |
|  CanUseOffGcd() = true      |  CanUseOffGcd() = true     |
```

### 能力技计数

在每个 GCD 窗口内，框架自动追踪已用能力技数量：
- `Data.Combat.AbilityCountInGcd` — 本 GCD 窗口已经用了几个能力技（只读）
- `Data.Combat.MaxAbilityTimesInGcd` — 本 GCD 窗口最多允许几个能力技（默认 2，ACR 可修改）

当你一个 GCD 技能执行后，`AbilityCountInGcd` 自动归零；每用一个能力技，自动 +1。

---

## 3. 核心接口详解

### 3.1 IRotationEntry — 你的 ACR 的"身份证"

```csharp
public interface IRotationEntry
{
    string AuthorName { get; }                    // 作者名（显示在 UI 中）
    bool UseCustomUi { get; }                     // 是否用自定 HTML UI（新手用 false）
    IEnumerable<Jobs> TargetJobs { get; }         // 支持哪些职业（BRD/MNK/WHM...）
    Rotation? Build(string settingFolder);        // 核心：创建 Rotation 容器
    IRotationUI? GetRotationUI();                 // 返回 UI 注册对象
    void OnDrawSetting();                         // 可选：绘制设置面板
    void Dispose();                               // Dll 卸载时清理
    void OnEnterRotation();                       // 切入时（初始化状态）
    void OnExitRotation();                        // 切出时（清理状态）
}
```

**使用技巧**：
- `UseCustomUi = false` 是推荐方式，用 `IAcrUiBuilder` 声明式注册控件
- `TargetJobs` 返回支持的职业列表，框架会按当前职业自动匹配
- `Build()` 在每次切换职业时调用，所以不要把状态存在 `IRotationEntry` 上

### 3.2 Rotation — 技能、事件、配置的容器

```csharp
public sealed class Rotation
{
    // 核心字段
    List<SlotResolverData> SlotResolvers;    // 技能槽位列表（决定优先级顺序！）
    List<ISlotSequence> SlotSequences;       // 技能序列（连招、循环段）
    IOpener? Opener;                         // 起手爆发序列
    IRotationEventHandler? EventHandler;      // 11 个事件回调
    List<ITriggerAction> TriggerActions;     // 全局触发器
    List<ITriggerCond> TriggerConditions;    // 全局触发条件

    // 热键
    List<IHotkeyEventHandler> HotkeyEventHandlers; // 热键处理器

    // 元数据
    Jobs TargetJob; int MinLevel; int MaxLevel; string Description;

    // 钩子
    Func<int>? CanPauseACRCheck;             // 返回 >=1 暂停 ACR
    Func<Slot?, int>? CanUseHighPrioritySlotCheck;  // 返回 <0 拒绝高优先技能插入
}
```

**使用技巧**：
- `SlotResolvers` 的顺序就是优先级——把你想优先放的技能排前面
- 链式方法写法：

```csharp
var rot = new Rotation { ... }
    .AddOpener(myOpener)
    .AddSlotSequences(myCombo, myBurst);
```

### 3.3 ISlotResolver — 核心决策单元

这是你要写得最多的接口。每个技能一个类，实现两个方法：

```csharp
public interface ISlotResolver
{
    int Check();          // >=0 表示可用，<0 表示不可用（跳过）
    void Build(Slot slot); // 构建要执行的技能槽位
}
```

**Check() 的约定**：
- 返回值本身**不影响优先级**——优先级由列表顺序决定
- 但返回值会显示在调试面板中，方便你调优
- 每帧对**每个** Resolver 都会调用一次 Check（不管 GCD 状态）
- 只有第一个"Check >= 0 且通过窗口检查"的 Resolver 会执行 Build

**Build() 的约定**：
- 通过链式 `.Add()` 往 `Slot` 添加技能
- 一个 Slot 可以包含多个技能（比如 GCD + oGCD + oGCD）
- 技能按添加顺序依次执行
- **不需要在 Build 中做任何条件判断**——所有判断都在 Check 中完成

### 3.4 Slot — 技能执行单元

```csharp
public sealed class Slot
{
    public List<SlotAction> Actions { get; }       // 要执行的技能序列
    public int MaxDuration { get; set; } = 600;    // 整体失败重试时间（ms）
    public bool Wait2NextGcd { get; set; }         // 强制等待下个 GCD

    // 链式构建
    public Slot Add(Spell spell);                  // 普通技能
    public Slot Add(SlotAction action);            // 自定义 SlotAction
    public Slot Add2NdWindowAbility(Spell spell);  // 第二个 oGCD 窗口能力技
    public Slot AddDelaySpell(int delayMs, Spell spell); // 延迟后放技能
    public void AppendSequence(ISlotSequence? seq); // 槽位结束时追加序列
}
```

**典型 Build 写法**：

```csharp
// 单技能 GCD
public void Build(Slot slot)
{
    slot.Add(new Spell(97, SpellTargetType.Target)); // 强力射击 → 目标
}

// GCD 插入一个 oGCD
public void Build(Slot slot)
{
    slot.Add(new Spell(7406, SpellTargetType.Target));   // 爆发射击
    slot.Add2NdWindowAbility(new Spell(16496, SpellTargetType.Target)); // 侧风诱导箭（第二窗口）
}

// 双 oGCD
public void Build(Slot slot)
{
    slot.Add(new Spell(110, SpellTargetType.Target));   // 失血箭
    slot.Add(new Spell(117, SpellTargetType.Target));   // 死亡箭雨
}

// 带延迟
public void Build(Slot slot)
{
    slot.AddDelaySpell(300, new Spell(100, SpellTargetType.Self)); // 等 300ms 再放
}
```

### 3.5 SlotAction — 单个技能行为

一般不需要手动创建 `SlotAction`，`Slot.Add()` 已经封装好了。只有当需要精细控制时才用：

```csharp
public sealed class SlotAction
{
    public required Spell Spell { get; init; }
    public WaitType Wait { get; init; } = WaitType.None;
    public int TimeInMs { get; init; }              // WaitInMs 时有效
    public int MaxDuration { get; init; } = 1000;    // 失败重试时间
}
```

WaitType 枚举：
| 值 | 含义 |
|----|------|
| `None` | 不等待 |
| `WaitInMs` | 等待指定毫秒 |
| `WaitForSndHalfWindow` | 等待到 GCD 后半段（第二个 oGCD 窗口） |

### 3.6 Spell — 技能定义

```csharp
public sealed partial class Spell
{
    public uint Id { get; init; }                   // 技能ID（来自 FFXIV 数据）
    public SpellTargetType TargetType { get; init; }// 目标类型
    public SpellType Type { get; init; }            // GCD/Ability/None
    public SpellCategory SpellCategory { get; init; } // Default/Potion/Sprint/Dance/Item

    // 便捷构造
    Spell()                                          // Idle 占位符
    Spell(uint id, SpellTargetType targetType)       // 最常用
    Spell(uint id, IBattleChara target)             // 指定目标对象
    Spell(uint id, Func<IBattleChara> getTargetFunc) // 动态目标
    Spell(uint id, Vector3 pos)                     // 地面目标

    // 计算属性（实时读取，每帧值可能不同）
    float CooldownMs          // 冷却剩余毫秒
    float Charges             // 当前充能层数
    int MaxCharges            // 最大充能层数
    float CastTime / AdjustedCastTime  // 读条时间
    float ActionRange         // 技能射程
    uint MPNeed               // MP 消耗
}
```

**SpellTargetType 常用值**：

| 值 | 目标 |
|----|------|
| `Self` | 自己 |
| `Target` | 当前目标（默认） |
| `TargetTarget` | 目标的目标（T 拉怪时） |
| `Pm1` ~ `Pm8` | 小队第 1~8 号成员 |
| `SpecifyTarget` | 显式指定对象 |
| `DynamicTarget` | 动态计算目标 |

> **注意**：`Spell.Id` 设为 0 表示"什么都不做"，框架会自动延迟 100ms 后继续下一个技能。

---

## 4. 数据层速览

所有游戏数据通过 `Data` 静态类的子模块访问。每帧在战斗循环开始前自动刷新。

```csharp
using static HiAuRo.Data;  // 导入 Data 的嵌套类：Target, Party, Objects, Combat, Me 等
```

### 4.1 Data.Me — 自己

```csharp
Me.Object               // IPlayerCharacter? 自身角色对象
Me.Name                 // 角色名
Me.ClassJob             // 当前职业 ID
Me.CurrentLevel         // 当前等级
Me.IsMoving             // 是否移动中
Me.IsInParty            // 是否在队伍中
Me.DistanceToObject2D(target)  // 到目标的 2D 距离
Me.DistanceToObject3D(target)  // 到目标的 3D 距离
Me.HasStatus(statusId, out index) // 自己是否有某状态
```

### 4.2 Data.Target — 目标

```csharp
Data.Target.Current          // IGameObject? 当前目标
Data.Target.Focus            // 焦点目标
Data.Target.MouseOver        // 鼠标悬停目标
Data.Target.Soft             // 软目标（未锁定的）
Data.Target.Previous         // 上一个目标
```

### 4.3 Data.Party — 队伍

框架每帧扫描一次队伍列表，自动分类到多个视图：

```csharp
Data.Party.All               // 全部成员
Data.Party.Alive / Dead      // 存活 / 死亡
Data.Party.Tanks / Healers / Dps / Melees / Rangeds / Casters
Data.Party.Nearby5y / 10y / 15y          // 按距离筛选（仅他人）
Data.Party.CastableParty                // 可施法成员（存活的队友）
Data.Party.CastableTanks / Healers / Dps
Data.Party.CastableMainTanks            // 开着盾姿的 T
Data.Party.CastableAlliesWithin20 / 25 / 30  // 按距离筛选的队友
```

每个成员是 `PartyMemberInfo`：
```csharp
Player: IPlayerCharacter?     // 角色对象
JobId: uint                   // 职业 ID
Distance: float               // 2D 距离
IsAlive: bool                 // 是否存活
```

### 4.4 Data.Objects — 周围对象

框架帮你分好类，不需要自己遍历全表：

```csharp
Data.Objects.All              // 全部对象
Data.Objects.Enemies          // 敌人（已经过滤掉不可攻击的）
Data.Objects.Allies           // 友方（玩家+NPC+宠物）
Data.Objects.Party            // 小队成员
Data.Objects.Pets / Summons   // 宠物 / 召唤兽
```

> **框架已经帮你处理的脏活**：敌人分类不仅看 `BattleNpcSubKind.Enemy`，还检查了 `ObjectKind`、`OwnerId`、`BuddyList`、`IsTargetable`。你直接用 `Data.Objects.Enemies` 就行。

### 4.5 Data.Combat — 战斗状态

```csharp
Data.Combat.InCombat          // 是否在战斗中
Data.Combat.IsCasting         // 自身是否在读条
Data.Combat.IsOnMount         // 是否在坐骑上
Data.Combat.IsBetweenAreas    // 是否在过图中
Data.Combat.IsInPVPArea       // 是否在 PVP 区域
Data.Combat.IsInInstanceArea  // 是否在副本内
Data.Combat.TerritoryType     // 当前地图 ID
Data.Combat.AbilityCountInGcd // 本 GCD 已用能力技数（只读）
Data.Combat.MaxAbilityTimesInGcd // 本 GCD 允许的能力技上限（默认 2，可改）
```

### 4.6 Data.BattleEvents — 战斗事件

```csharp
Data.BattleEvents.GetRecentTethers()        // 最近 30s 的连线事件（List<TetherEvent>）
Data.BattleEvents.GetRecentActionEffects()  // 最近 30s 的技能效果事件（List<ActionEffectEvent>）
Data.BattleEvents.GetRecentMapEffects()     // 最近 30s 的地图效果事件（List<MapEffectEvent>）
```

> 类名原为 `Data.BattleData`，因与 `Runtime.BattleData`（slot 中心状态）重名易混淆，故改名 `BattleEvents`。

### 4.7 Data.FactState — 事实轴状态

当 FactAxis 运行时，可以查询当前副本时间线状态和事件时间预测：

```csharp
Data.FactState              // FactAxis.FactState? 事实轴当前状态快照（未运行时为 null）

// 时间维度
state.PhaseName             // string: 当前阶段名称
state.PhaseTime             // double: 当前阶段已过秒数
state.TotalTime             // double: 战斗总秒数

// 目标可选中
state.IsTargetable          // bool? 当前可选中状态（null=未声明）
state.NextTargetableIn      // double? 距下次变为可占用的秒数（null=无后续声明）
state.NextUntargetableIn    // double? 距下次变为不可占用的秒数（null=不会再变不可占用）

// 游戏事件时间预测
state.NextEventTimeOfType(FactEventType.Ability)            // 距下一技能效果秒数
state.NextEventTimeOfType(FactEventType.Ability, 46295)     // 距ID=46295的技能效果秒数
state.NextEventTimeOfType(FactEventType.StartsUsing)        // 距下一读条秒数
state.NextEventTimeOfType(FactEventType.HeadMarker, 0x017F) // 距ID=0x017F的分散点名秒数
// abilityId=0 时不筛ID，等价于无参版本；传具体ID则只匹配该ID的事件

// 自定义前向扫描
state.PendingEvents         // List<FactEvent>: 未到达事件（按时序），ACR 可 LINQ 过滤
```

```csharp
public void OnBattleUpdate(int battleTimeMs)
{
    var s = Data.FactState;
    if (s == null) return;

    // 当前阶段 + 已过时间
    // s.PhaseName, s.PhaseTime, s.TotalTime

    // 10s内不会变不可占用 → 可以开爆发
    if (s.NextUntargetableIn == null || s.NextUntargetableIn > 10)
        _canBurst = true;

    // 下一读条还有多久？
    var nextCast = s.NextEventTimeOfType(FactEventType.StartsUsing);

    // 自定义：查下一带减伤需求的事件
    var nextMit = s.PendingEvents
        .FirstOrDefault(e => e.Actions.Any(a => a is 需求减伤动作));
}
```

---

## 5. 事件回调

实现 `IRotationEventHandler`。除 `OnBeforeSpellCast` 有默认空实现外，其余方法必须显式实现（不需要的写空方法体）。

### 回调总览

| 回调 | 触发时机 | 典型用途 |
|------|---------|---------|
| `OnEnterRotation()` | ACR 被加载时 | 初始化状态变量、重置计数器 |
| `OnExitRotation()` | ACR 被卸载时 | 清理资源 |
| `OnTerritoryChanged()` | 切图 / 进副本 | 重置副本特定状态 |
| `OnPreCombat()` | 每帧，未进战时 | 远敏唱歌、T 切姿态 |
| `OnResetBattle()` | 脱战时 | 重置战斗计数器、清缓存 |
| `OnNoTarget()` | 决策路径中当前目标为 null、非战斗对象或不可选中时触发一次 | 舞者转阶段提前跳舞等 |
| `OnBattleUpdate(int ms)` | 战斗中每帧（**最常用**） | 更新 Dot 计时、Buff 追踪等 |
| `BeforeSpell(Slot)` | Slot 开始时触发一次 | 仅返回非 null 且不同于当前 Slot 时插入前序；原 Slot 保留并在前序 Slot 完成后继续执行 |
| `OnBeforeSpellCast(Slot, Spell)` | 每个 Action 执行前（有默认实现） | 最后的资源检查 |
| `AfterSpell(Slot, Spell)` | 技能释放后 | 记录状态变更 |
| `OnSpellCastSuccess(Slot, Spell)` | 读条技能成功判定时 | 滑步时间记录 |
| `OnGameEvent(ITriggerCondParams)` | 底层游戏事件发生时 | Boss 读条检测、Buff 变化追踪、连线检测等 |
| `OnPhaseChanged(string, string)` | 事实轴阶段切换时 | 副本阶段策略切换 |

### 新增：游戏事件回调 `OnGameEvent`

`OnGameEvent` 将全部 22 种底层游戏事件分发给 ACR。回调在 GameEventHook 线程执行，为只读通知，ACR 作者自行处理线程安全。

**ITriggerCondParams 完整子类型**：

| 类型 | 含义 | 关键字段 |
|------|------|---------|
| `ActorCastParams` | Boss 开始读条 | `ActionID`, `CastTime`, `SourceID`, `PosX/Y/Z` |
| `ActionEffectParams` | 技能效果命中 | `ActionID`, `SourceID`, `TargetOID` |
| `NoTargetAbilityEffectParams` | 地面AOE效果 | `ActionID`, `SourceID`, `PosX/Y/Z` |
| `ActorControlTargetIconParams` | 点名标记 (HeadMarker) | `SourceID`, `TargetID`, `IconID` |
| `TetherCreateParams` | 连线创建 | `TetherID`, `SourceID`, `TargetOID` |
| `TetherRemoveParams` | 连线移除 | `SourceID` |
| `ActorControlDeathParams` | Actor 死亡 | `SourceID`, `TargetID` |
| `BuffGainParams` | Buff 获得 | `SourceID`, `StatusID`, `StackCount` |
| `BuffRemoveParams` | Buff 移除 | `SourceID`, `StatusID` |
| `MapEffectParams` | 地图特效 | `PositionIndex`, `Param1`, `Param2` |
| `NpcYellParams` | NPC 喊话 | `SourceID`, `SourceName`, `YellID`, `YellMsg` |
| `UnitCreateParams` | 单位出现 | `EntityId`, `DataId`, `Name` |
| `UnitDeleteParams` | 单位消失 | `EntityId`, `DataId`, `Name` |
| `ActorControlTargetableParams` | 可选中状态变化 | `SourceID`, `TargetID`, `IsTargetable` |
| `ActorControlCombatParams` | 战斗状态变化 | `IsEntering` |
| `ActorControlTimelineParams` | 时间轴播放 | `SourceID`, `TimelineID` |
| `ActorControlParams` | ActorControl 原始 | `SourceID`, `Command`, `P1~P6`, `TargetID` |
| `DirectorUpdateParams` | Director 更新 | `Category`, `Param1~4` |
| `EnvControlParams` | 环境控制 | `Index`, `Flag` |
| `WeatherChangedParams` | 天气变化 | `NewWeatherId` |
| `AfterSpellParams` | 自身技能后 | `SpellID` |
| `CombatStateParams` | 战斗状态 | `IsEntering` |

```csharp
// 使用示例：监听 Boss 读条和点名
public class BRDEventHandler : IRotationEventHandler
{
    private bool _bossCastingRaidwide;

    public void OnGameEvent(ITriggerCondParams eventParams)
    {
        switch (eventParams)
        {
            case ActorCastParams cast:
                if (cast.ActionID == 12345) // 某个 Boss AOE ID
                    _bossCastingRaidwide = true;
                break;

            case BuffGainParams buff:
                if (buff.StatusID == 999) // 某个点名 Buff
                    DService.Instance().Log.Info($"[BRD] 需要处理点名！");
                break;
        }
    }

    // 在 Check 中使用事件收集的信息
    public int Check()
    {
        if (_bossCastingRaidwide && MinstrelReady())
            return 1;
        return 0;
    }
}
```

### 新增：阶段切换回调 `OnPhaseChanged`

当 FactAxis 运行时，副本时间线进入新阶段或分支切换时会触发此回调。适用于根据 BOSS 阶段切换 ACR 策略（如 AOE / 单体切换）。

```csharp
public void OnPhaseChanged(string phaseId, string phaseName)
{
    DService.Instance().Log.Info($"[BRD] 阶段切换: {phaseName}");

    switch (phaseId)
    {
        case "p1_opening":
            // P1 开场，不需要特殊处理
            break;
        case "p2_clone":
            // P2 分身阶段，切换为双目标策略
            _isDualTarget = true;
            break;
        case "p3_enrage":
            // P3 狂暴阶段，全力输出
            _holdResources = false;
            break;
    }
}
```

### 使用示例

```csharp
public class BRDEventHandler : IRotationEventHandler
{
    private int _dotTimer;

    // 战斗中每帧更新状态
    public void OnBattleUpdate(int battleTimeMs)
    {
        // 检查目标身上的 DoT 剩余时间
        var dotLeft = AuraHelper.GetAuraTimeLeft(Data.Target.Current, 1200); // 风蚀
        _dotTimer = (int)(dotLeft / 1000);
    }

    // 游戏事件：捕获 Boss 读条，提交高优先级减伤
    public void OnGameEvent(ITriggerCondParams eventParams)
    {
        if (eventParams is ActorCastParams cast && cast.ActionID == 12345)
        {
            var slot = new Slot();
            slot.Add(new Spell(7561, SpellTargetType.Self)); // 策动
            SlotHelper.Execute(slot);
        }
    }

    // 脱战重置
    public void OnResetBattle()
    {
        _dotTimer = 0;
    }
}

// 在 Build 中挂载
var rot = new Rotation
{
    EventHandler = new BRDEventHandler(),
    ...
};
```

---

## 6. 工具类速查

以下都是 `HiAuRo.ACR` 命名空间下的静态工具类。

### 6.1 GCDHelper — GCD 时间

```csharp
GCDHelper.IsGCDReady()         // bool: GCD 转好了吗
GCDHelper.GetGCDCooldown()     // float: GCD 剩余毫秒数
GCDHelper.GetGCDDuration()     // float: 动态 GCD 总时长（受技速影响）
GCDHelper.CanUseOffGcd()       // bool: 当前可以插入 oGCD 吗
```

### 6.2 SpellHelper — 技能冷却/就绪

```csharp
SpellHelper.CanUseSpell(id)              // bool: 冷却转好了吗（只看 CD）
SpellHelper.IsActionReady(id, targetId)  // bool: 综合就绪（CD+MP+射程+解锁），推荐
SpellHelper.GetCooldownRemaining(id)     // float: 冷却剩余毫秒
SpellHelper.GetCharges(id)               // int: 当前充能层数
SpellHelper.GetMaxCharges(id)            // int: 最大充能层数
SpellHelper.GetChargeCooldown(id)        // float: 距下次充能毫秒数
SpellHelper.IsInRange(id, target)        // bool: 目标在射程内吗
```

> **推荐**：用 `IsActionReady(id, targetId)` 代替 `CanUseSpell(id)`——它额外检查了 MP、目标状态、技能解锁等。

### 6.3 Spell 扩展方法

```csharp
spell.IsAbilityEx()                  // 是能力技吗（含 Sprint/Potion 等）
spell.IsUnlock()                     // 当前等级已解锁该技能
spell.IsUnlockWithCDCheck()          // 解锁 + CD 就绪
spell.IsReadyWithCanCast()           // 最全面的就绪检查
spell.IsMaxChargeReady(delta)        // 充能近满
spell.CoolDownInGCDs(count)          // CD 在 N 个 GCD 内转好
spell.AbilityCoolDownInNextGCDsWindow(n) // CD 在接下来 N 个 GCD 窗口内转好
spell.RecentlyUsed(ms)               // 最近 ms 内用过吗
```

### 6.4 AuraHelper — Buff/Debuff 检测

```csharp
AuraHelper.HasSelfAura(buffId)           // 自己身上有吗
AuraHelper.HasTargetAura(buffId)         // 目标身上有吗
AuraHelper.HasAura(target, buffId)       // 指定对象身上有吗
AuraHelper.HasAnyAura(target, ids...)    // 有任意一个吗
AuraHelper.GetAuraTimeLeft(target, id, sourceId?)  // 剩余毫秒数（0=不存在）
```

### 6.5 ComboHelper — 连击状态

```csharp
ComboHelper.LastComboSpellId             // uint: 当前连击动作 ID
ComboHelper.ComboTimer                   // float: 连击剩余时间（秒）
ComboHelper.LastSpellId                  // uint: 上一次成功使用的技能 ID
ComboHelper.WasLastCombo(spellId)        // bool: 上一招是这个吗
ComboHelper.ComboInWindow(id, windowMs)  // bool: 连击还在窗口内
ComboHelper.ComboAboutToExpire(id, ms)   // bool: 连击快过期了
```

### 6.6 TargetHelper — 目标选择

```csharp
TargetHelper.GetNearbyEnemyCount(target, range)  // 目标周围敌人数（AOE 判断核心）
TargetHelper.IsBehind(target)                     // 在目标背后吗（身位）
TargetHelper.IsFlanking(target)                   // 在目标侧面吗（身位）
TargetHelper.TargetCastingIsBossAOE(target, ms)   // Boss 在读 AOE 吗
TargetHelper.GetCastingSpellTiming(target)        // 目标读条剩余毫秒数
TargetHelper.GetMostCanTargetObjects(id, min, r)  // 找最佳 AOE 目标
```

### 6.7 SlotHelper — 回调中手动释放技能

在事件回调（`OnGameEvent` / `OnBattleUpdate` 等）中，可以向高优先级队列提交 Slot，等待下一次符合条件的 `ResolveSlots` 处理：

```csharp
// 加入高优先级队列，在下一次符合条件的 ResolveSlots 处理中执行
SlotHelper.Execute(new Slot(new Spell(7561, SpellTargetType.Self)));

// 加入 SpellQueue 排队执行（下次队列处理时）
SlotHelper.Enqueue(new Slot(new Spell(7561, SpellTargetType.Self)));
```

```csharp
// 使用示例：监听到 Boss 读条后提交高优先级减伤
public void OnGameEvent(ITriggerCondParams eventParams)
{
    if (eventParams is ActorCastParams cast && cast.ActionID == 12345)
    {
        // Boss 在读 AOE，提交高优先级策动
        var slot = new Slot();
        slot.Add(new Spell(7561, SpellTargetType.Self));
        SlotHelper.Execute(slot);
    }
}
```

### 6.8 MovementHelper — 移动/TP 快捷操作

在事件回调中直接控制角色移动，立即执行：

```csharp
// 寻路移动到目标位置（依赖 VNavmesh）
MovementHelper.MoveTo(new Vector3(100, 0, 100));

// 瞬移到坐标（内部实现）
MovementHelper.TeleportTo(new Vector3(95, 0, 105));

// 停住移动
MovementHelper.Stop();
```

```csharp
// 使用示例：Boss 读条时预走位
public void OnGameEvent(ITriggerCondParams eventParams)
{
    if (eventParams is ActorCastParams cast && cast.ActionID == bossAoeId)
        MovementHelper.MoveTo(new Vector3(90, 0, 100));
}
```

> **MoveTo** 需要安装 VNavmesh 插件；**TeleportTo** 内部实现无需外部依赖。

### 6.10 其他常用

```csharp
// 道具
ItemHelper.ForceUsePotion(itemId, isHq)
ItemHelper.CheckCurrJobPotion(isHq)

// 主控
MainControlHelper.IsPaused          // 是否暂停
MainControlHelper.TogglePause()     // 切换暂停

// QT（快速切换开关）
QTHelper.IsEnabled(id)              // 开关状态（字符串 ID）
QTHelper.IsEnabled(BuiltinQt.Burst) // 开关状态（枚举，推荐）
QTHelper.GetAll()                   // 所有 QT 开关

// 热键
HotkeyHelper.GetAll()               // 所有热键
HotkeyHelper.GetBinding(id)         // 当前绑定键

// 数学
MathHelper.CountInSector(center, dir, radius, halfAngleDeg)  // 扇形内敌人数
```

### 6.11 HiAuRo.Helper — 职业数据辅助库（强烈推荐）

> **GitHub**：[https://github.com/destesasinsh/HiAuRo.Helper](https://github.com/destesasinsh/HiAuRo.Helper)

`HiAuRo.Helper` 是社区维护的全职业数据辅助库，覆盖 FFXIV 全部 **21 个战斗职业**（4T + 4H + 6 近战 + 3 远敏 + 4 法系）。特点：

- **SDK 原生**——Helper 编译期引用 `HiAuRo.Sdk`，复用宿主暴露的 ACR/Data API
- **静态 API 开箱即用**——所有 Helper 都是 `static` 属性/方法，无需 `new`、无需实现接口、无需初始化
- **宿主共享加载**——运行时由 `HelperUpdater` 加载，`ACRLoader` 解析到同一份 Helper 程序集

#### 覆盖职业

| 职能 | 职业 |
|------|------|
| 坦克 | PLD, WAR, DRK, GNB |
| 治疗 | WHM, SCH, AST, SGE |
| 近战 | MNK, DRG, NIN, SAM, RPR, VPR |
| 远程 | BRD, MCH, DNC |
| 法师 | BLM, SMN, RDM, PCT |

#### 使用示例

```csharp
using HiAuRo.Helper;

// 诗人：检查直线射击就绪 + 当前歌曲
if (BRDHelper.HasStraightShotReady && BRDHelper.CurrentSong == Song.WanderersMinuet)
    ...

// 战士：检查原初的解放
if (WARHelper.Has原初的解放)
    ...

// 龙骑：检查龙威
if (DRGHelper.HasPowerSurge)
    ...

// 武士：检查彼岸花 DOT
if (SAMHelper.IsTargetDotOk)
    ...

// 黑魔：检查当前天语状态
if (BLMHelper.HasEnochian && BLMHelper.IsAstralFireMax)
    ...
```

#### 如何引入

ACR 项目需要同时引用 `HiAuRo.Sdk` 和 `HiAuRo.Helper`。`HiAuRo.Sdk` 版本必须与宿主匹配，`ExcludeAssets="runtime"` 避免 ACR 输出第二份 `HiAuRo.dll`。

```xml
<ItemGroup>
    <PackageReference Include="HiAuRo.Sdk" Version="0.2.11">
        <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
</ItemGroup>
```

**推荐以 git submodule 方式引入 `HiAuRo.Helper`**，这样本地改完就能验证，同时也方便向主库提 PR：

```bash
# 1. 在你的 ACR 仓库根目录添加 submodule
cd YourACR
git submodule add https://github.com/destesasinsh/HiAuRo.Helper.git Helper

# 2. 加入 solution
dotnet sln YourACR.slnx add Helper/HiAuRo.Helper/HiAuRo.Helper.csproj
```

然后在你的 `.csproj` 中添加项目引用，并排除 submodule 源码被 ACR 项目隐式编译：

```xml
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

`Private=False` 避免 ACR 输出第二份 `HiAuRo.Helper.dll`。游戏内由 HiAuRo 宿主加载并共享 Helper；之后直接在代码中 `using HiAuRo.Helper;` 即可。

#### 社区贡献

HiAuRo.Helper 是社区开源项目，欢迎所有人贡献：

1. **Fork 仓库** → 创建分支 → 修改 → 提交 PR
2. **AI 自动审查**通过后可自动合并

建议的贡献方向：
- 更新技能 ID（版本更新后）
- 补充 Buff/Debuff 检测
- 新增职业辅助属性
- 修复兼容性问题

---

## 7. UI 注册

HiAuRo 的 UI 是 Web 前端渲染的（CEF 内嵌浏览器），但 ACR 作者不需要懂 HTML/CSS/JS。你只需要在 `IRotationUI` 中**声明式地**描述要哪些控件。

`IRotationUI.RegisterControls` 接收 `IAcrUiBuilder`，值控件使用 `ref` 参数直接绑定 settings 字段，无需手写 id 映射。

### 7.1 实现 IRotationUI

```csharp
using HiAuRo.ACR;
using HiAuRo.ACR.HotkeyResolvers;

// ① 定义 Settings（继承 AcrSettings，字段需为 public 字段，Web UI 回写靠字段名匹配）
public class BRDSettings : AcrSettings
{
    public bool AutoSing = true;
    public float RefreshSecs = 3f;
    public string OpenerType = "标准起手";
    public int AoeCount = 3;
    public bool UsePitchPerfect = true;
}

// ② 入口实现 ISettingsProvider<T>，宿主自动托管 Settings 的加载与保存
public class BRDEntry : IRotationEntry, ISettingsProvider<BRDSettings>
{
    public BRDSettings Settings { get; set; } = new();
    public bool UseCustomUi => false;
    public IRotationUI? GetRotationUI() => new BRDRotationUI(Settings);
    // ... 其他成员省略
}

// ③ UI 类：通过构造函数接收 settings，用 ref 绑定
public class BRDRotationUI : IRotationUI
{
    private readonly BRDSettings _s;
    public BRDRotationUI(BRDSettings settings) => _s = settings;

    public void RegisterControls(IAcrUiBuilder builder)
    {
        // 分 Tab
        builder.AddTab("基础设置");
        builder.AddTab("AOE 设置");

        // ── Tab 1: 基础设置 ──
        builder.AddGroup("Buff 相关");
        builder.AddCheckbox("自动唱歌", ref _s.AutoSing);

        builder.AddGroup("DoT 续费");
        builder.AddSlider("提前续费秒数", 1, 5, ref _s.RefreshSecs);

        builder.AddGroup("起手爆发");
        builder.AddDropdown("起手类型",
            ["标准起手", "双爆发起手", "2.47 特化"], ref _s.OpenerType);

        // ── Tab 2: AOE 设置 ──
        builder.AddGroup("AOE");
        builder.AddIntInput("AOE 触发敌人数量", ref _s.AoeCount, 1, 1);
        builder.AddCheckbox("使用影噬箭", ref _s.UsePitchPerfect);

        // ── QT 开关（独立于 Tab/Group，显示在悬浮窗 QT 面板） ──
        builder.AddBuiltinQt(BuiltinQt.Burst);
        builder.AddBuiltinQt(BuiltinQt.Mitigation, true);
        builder.AddQtToggle("自动唱歌", false, "战斗中自动释放歌曲");

        // ── 热键（IHotkeyResolver 实现，显示在悬浮窗热键面板） ──
        builder.AddQtHotkey("手动爆发",
            new HotkeyResolver_技能("爆发", 7405, SpellTargetType.Target, "Ctrl+Shift+F"));
        builder.AddQtHotkey("疾跑", new HotkeyResolver_疾跑());

        // 多热键同行排列
        builder.AddHotkeyRow([
            new HotkeyResolver_技能("Ex弓", 117, SpellTargetType.Target),
            new HotkeyResolver_吃药("爆发药", 39727, true, "Ctrl+F"),
        ]);

        // 主控面板（暂停/保存）
        builder.AddMainControl(showPause: true, showSave: true);
    }
}
```

### 7.2 ref 绑定原理

`IAcrUiBuilder` 的值控件（`AddCheckbox` / `AddSlider` / `AddDropdown` / `AddIntInput` / `AddFloatInput` / `AddTextInput`）使用 `[CallerArgumentExpression]` 自动捕获字段名：

```csharp
builder.AddCheckbox("自动唱歌", ref _s.AutoSing);
// → CallerArgumentExpression 捕获 "_s.AutoSing" → 提取字段名 "AutoSing"
// → Web UI 回写时通过 GetCurrentSettings().GetField("AutoSing") 定位字段
```

> Settings 字段必须是 **public 字段**（不是属性），否则 Web UI 回写无法定位。ImGui 悬浮窗模式通过 `ref` 直接读写，不受此限制。

### 7.3 IAcrUiBuilder 控件一览

**结构控件**（`IUiBuilder` 基础接口）：

| 方法 | 用途 |
|------|------|
| `AddTab(title)` | 开始一个标签页 |
| `EndTab()` | 结束当前标签页 |
| `AddGroup(title)` | 创建分组（折叠面板） |
| `AddSeparator()` | 分隔线 |
| `AddSameLine()` | 下一个控件同行 |
| `AddLabel(text)` | 文本标签 |
| `AddMainControl(showPause?, showSave?)` | 主控面板（暂停/保存） |
| `AddTooltip(targetId, tooltip)` | 给控件加提示 |

**值控件**（`IAcrUiBuilder` ref 重载，直接绑定 settings 字段）：

| 方法 | 签名 |
|------|------|
| `AddCheckbox` | `(label, ref bool value)` |
| `AddSlider` | `(label, float min, float max, ref float value)` |
| `AddDropdown` | `(label, string[] options, ref string value)` |
| `AddIntInput` | `(label, ref int value, int step=1, int stepFast=10)` |
| `AddFloatInput` | `(label, ref float value)` |
| `AddTextInput` | `(label, ref string value)` |
| `AddButton` | `(label)` → `bool`（返回是否点击） |

**QT / 热键**：

| 方法 | 签名 | 说明 |
|------|------|------|
| `AddBuiltinQt` | `(BuiltinQt type, bool? value=null)` | 注册内置 QT（Burst/Potion/Hold/Mitigation/Dump/AoE/TTK） |
| `AddQtToggle` | `(label, bool value, tooltip?, color?, defaultVisible?)` | 自定义 QT 开关 |
| `AddQtHotkey` | `(label, IHotkeyResolver resolver, defaultVisible=true)` | 注册热键（点击释放技能） |
| `AddHotkeyRow` | `(IHotkeyResolver[] hotkeyIds)` | 多个热键同行排列 |

**内置 HotkeyResolver**（命名空间 `HiAuRo.ACR.HotkeyResolvers`）：

| 类 | 用途 |
|------|------|
| `HotkeyResolver_技能(label, spellId, targetType, defaultKey)` | 释放指定技能 |
| `HotkeyResolver_NormalSpell(spellId, name, targetType)` | 释放技能（带 CD 检查） |
| `HotkeyResolver_吃药(label, itemId, ishq, defaultKey)` | 使用爆发药 |
| `HotkeyResolver_疾跑()` | 疾跑（无参数） |
| `HotkeyResolver_极限技` / `HotkeyResolver_LB` | 极限技 |

> **自定义 UI**：如果 `UseCustomUi = true`，`GetRotationUI()` 返回 `null`，你需要自己提供 HTML 文件（放在 ACR DLL 同目录下）。

---

## 8. 高级特性

### 8.1 Opener — 起手爆发序列

`IOpener` 继承自 `ISlotSequence`，是一组按顺序执行的预设技能。HiAuRo 会自动管理倒计时、执行顺序和中断逻辑。

```csharp
public class BRDOpener : IOpener
{
    public uint Level => 100;   // 多少级可用
    public List<Action<Slot>> Sequence { get; } = [];
    public int StartCheck() => 1;           // >=0 可以开始
    public int StopCheck(int index) => -1;  // <0 不允许中断

    public BRDOpener()
    {
        // 预填充起手序列
        Sequence.Add(slot => {
            slot.Add(new Spell(7405, SpellTargetType.Target)); // 辉煌箭（GCD 前预读）
        });
        Sequence.Add(slot => {
            slot.Add(new Spell(3558, SpellTargetType.Self));   // 贤者叙事谣
        });
        Sequence.Add(slot => {
            slot.Add(new Spell(7406, SpellTargetType.Target)); // 爆发射击
            slot.Add(new Spell(16496, SpellTargetType.Target)); // 侧风诱导箭
        });
        // ... 更多步骤
    }

    public void InitCountDown(CountDownHandler handler)
    {
        // 注册倒计时技能：2s 时预读辉煌箭
        handler.AddAction(2.0f, () => new Spell(7405, SpellTargetType.Target));
    }
}
```

**起手序列的执行特点**：
- 倒计时期间就可以执行 `InitCountDown` 注册的技能
- 序列步骤按 `Sequence` 索引顺序执行
- `StopCheck` 可以终止序列（比如目标死亡、转阶段）
- 序列结束后自动回到正常 ACR 循环

### 8.2 SlotSequence — 连招序列

当需要控制一系列技能按固定顺序执行时（比如某些爆发窗口），用 `ISlotSequence`：

```csharp
public class RagingStrikesBurst : ISlotSequence
{
    public List<Action<Slot>> Sequence { get; } = [];

    public int StartCheck()
    {
        // 猛者强击可用且 GCD 就绪时启动
        return SpellHelper.CanUseSpell(101) ? 1 : -1;
    }

    public int StopCheck(int index) => -1; // 不中断

    public RagingStrikesBurst()
    {
        Sequence.Add(slot => slot.Add(new Spell(101, SpellTargetType.Self)));    // 猛者强击
        Sequence.Add(slot => {
            slot.Add(new Spell(7406, SpellTargetType.Target));   // 爆发射击
            slot.Add(new Spell(16496, SpellTargetType.Target));  // 侧风诱导箭
        });
        Sequence.Add(slot => slot.Add(new Spell(7406, SpellTargetType.Target))); // 爆发射击
    }
}
```

**挂载序列**：

```csharp
// 方式一：放入 Rotation.SlotSequences（框架会在适当时机尝试启动）
rot.AddSlotSequences(new RagingStrikesBurst());

// 方式二：在 Slot 结束时追加序列
public void Build(Slot slot)
{
    slot.Add(new Spell(101, SpellTargetType.Self));
    slot.AppendSequence(new RagingStrikesBurst()); // 猛者后接完整爆发序列
}
```

### 8.3 Trigger — 条件触发器

用于全局的条件→动作响应，适合减伤、团辅等场景：

```csharp
// 触发条件
public class BossCastingTankbuster : ITriggerCond
{
    public bool Handle(ITriggerCondParams? condParams = null)
    {
        return TargetHelper.TargetCastingIsBossAOE(
            Data.Target.Current as IBattleChara, 3000);
    }
}

// 触发动作
public class UseFeint : ITriggerAction
{
    public bool Handle()
    {
        if (!SpellHelper.CanUseSpell(7549)) return false;
        // ... 执行牵制
        return true;
    }
}

// 挂载到 Rotation
rot.AddTriggerCondition(new BossCastingTankbuster());
rot.AddTriggerAction(new UseFeint());
```

触发器和 ACR 正常循环**并行运行**，互不阻塞。触发器在 `AIRunner.Update()` 中每次循环都检查。

### 8.4 HotkeyEventHandler — 自定义热键

```csharp
public class ManualBurst : IHotkeyEventHandler, IHotkeyResolver
{
    public bool IsActive { get; set; }

    public string Id => "manual_burst";
    public string Label => "手动爆发";
    public string DefaultKey => "Ctrl+Shift+F";

    public bool HandleKeyPress() => false; // 不拦截
    public void OnHotkeyExecuted() => IsActive = true;
    public int Check() => IsActive ? 0 : -1;
    public void Execute() { IsActive = false; }

    public bool HandleKeyDown() => false;
}
```

### 8.5 CanPauseACRCheck — 暂停 ACR

```csharp
// Chat 窗口打开时暂停 ACR
rot.AddCanPauseACRCheck(() =>
    DService.Instance().Chat.IsChatOpen ? 1 : -1);
```

返回 `>=1` 时，跳过 `OnPreCombat`、Slot 序列和普通 Resolver；普通待处理 Slot 保留至恢复。已入队的 SpellQueue、NextSlot/强制 Slot 与高优先级 Slot 仍可执行。

### 8.6 CanUseHighPrioritySlotCheck — 拒绝高优先级技能

```csharp
// 某些情况下拒绝执行轴强制插入的技能
rot.CanUseHighPrioritySlotCheck = _ =>
    Data.Combat.IsCasting ? -1 : 0; // 读条时拒绝
```

### 8.7 身位指示器 — 在目标脚下显示身位 VFX

ACR 在每个 GCD 构建 Slot 时调用 `PositionalState.Push()`，系统自动在目标脚下显示身位扇形和攻击范围圈。

```csharp
// 在 Build(Slot slot) 中调用:
Rendering.PositionalState.Push(
    Rendering.PositionalDir.Behind,  // 身位方向
    gcdTimeMs: 2500,                  // GCD 剩余时间 (ms)
    actionId: spell.Id                // 技能 ID
);
```

**PositionalDir 枚举**：

| 值 | 说明 |
|----|------|
| `None` | 无身位需求（清除指示器） |
| `Behind` | 背身位 — 目标后方 90° Fan90 VFX |
| `Flank` | 侧身位 — 目标左右各 120° Fan120 VFX |

**视觉效果**：
- 正确身位 → 绿色半透明 `(0, 1, 0, 0.35)`
- 错误身位 → 红色半透明 `(1, 0, 0, 0.35)`
- 扇形半径 = `HitboxRadius + 3f`
- 攻击范围圈 = 1px ImGui 细线圆（绿色命中圈 + 黄色自动攻击圈）

**用户开关**：主控→设置→身位指示器（`ShowPositional` / `ShowTargetHitbox` / `ShowAutoAttackRange`）

---

## 9. 实战技巧与常见错误

### 9.1 Check / Build 分离原则

**Check 做判断，Build 做组装。不要反过来。**

```csharp
// ✅ 正确
public class HeavyShot : ISlotResolver
{
    public int Check()
    {
        if (Me.CurrentLevel < 1) return -1;  // 没学会
        return 0;
    }

    public void Build(Slot slot)
    {
        slot.Add(new Spell(97, SpellTargetType.Target));
    }
}

// ❌ 错误：在 Build 中放条件判断
public void Build(Slot slot)
{
    if (!Data.Combat.InCombat) return;  // 不应该在这里判断
    slot.Add(new Spell(97, SpellTargetType.Target));
}
```

### 9.2 优先级是顺序，不是数值

```csharp
// ✅ 正确：通过列表顺序控制优先级
SlotResolvers = [
    new() { Resolver = new MostImportantSkill(), Mode = SlotMode.Gcd },   // 先检查
    new() { Resolver = new LessImportantSkill(), Mode = SlotMode.Gcd },   // 后检查
];

// ❌ 误解：Check 返回值不能用来改变优先级
// Check 返回 100 和返回 1 效果一样——谁排在前面谁先执行
```

### 9.3 GCD 技能记得设 SlotMode.Gcd

```csharp
// ✅ GCD 技能
new() { Resolver = new HeavyShot(), Mode = SlotMode.Gcd }

// ✅ oGCD 能力技
new() { Resolver = new Bloodletter(), Mode = SlotMode.OffGcd }

// ⚠️ 需要立即执行的（比如疾跑、康复）
new() { Resolver = new Sprint(), Mode = SlotMode.Always }
```

> `SlotMode.Gcd` 的技能只会在 GCD 冷却完毕时放；`SlotMode.OffGcd` 只会在 oGCD 可用窗口放；`SlotMode.Always` 不限制，Check 通过就 Build。

### 9.4 用 Spell.IsReadyWithCanCast() 而不是自己拼条件

```csharp
// ✅ 框架已经封装好的综合检查
public int Check()
{
    var spell = new Spell(97, SpellTargetType.Target);
    if (!spell.IsReadyWithCanCast()) return -1;  // CD + 状态 + 射程 + 解锁
    return 0;
}

// ❌ 自己零散拼条件，容易漏
public int Check()
{
    if (!SpellHelper.CanUseSpell(97)) return -1;
    // 忘了检查射程？
    // 忘了检查 MP？
    return 0;
}
```

### 9.5 直接使用 Data.Objects.Enemies，不要自己做分类

框架已经处理了目标可攻击性、友好 NPC 误判等问题。

### 9.6 不要在 Check 中做昂贵的操作

Check 每帧对每个 Resolver 都会调用。避免在里面：
- 遍历整个 ObjectTable
- 复杂 LINQ 查询大列表
- GetExcelSheet 多次调用

把昂贵操作的结果缓存到字段中，在 `OnBattleUpdate` 里更新。

```csharp
public class AoeSkill : ISlotResolver
{
    private int _enemyCount;

    // 在事件回调中更新
    public void OnBattleUpdate(int ms)
    {
        _enemyCount = TargetHelper.GetNearbyEnemyCount(Data.Target.Current, 5f);
    }

    // Check 只读缓存
    public int Check()
    {
        return _enemyCount >= 3 ? 0 : -1;
    }
    // ...
}
```

### 9.7 常见坑

| 坑 | 正确做法 |
|----|---------|
| 用 `Svc.ClientState.LocalPlayer` | 已废弃，用 `Me.Object` |
| 迭代 `IPartyMember.GameObject` 多次 | `Data.Party` 每帧只扫描一次，直接用预分类列表 |
| 只用 `BattleNpcSubKind.Enemy` 判断敌人 | 直接用 `Data.Objects.Enemies`，框架已做完整分类 |
| 多线程访问游戏数据 | 所有 ACR 逻辑在主线程 Tick 中执行，不需要加锁 |
| Build 中调用 UseAction | **永远不要**在 Build 中手动释放技能，只构建 Slot |
| Spell.Id = 0 表示 Idle | 不是错误，是一个合法的"什么都不做"指令 |
| Spell 对象跨帧复用 | Spell 有计算属性（如 CooldownMs），值每帧会变。可以复用 Id，但不要假设旧属性值有效 |

### 9.8 调试技巧

HiAuRo 内置调试面板（Web UI），会显示每个 Resolver：
- Check 返回值
- 是否通过窗口检查
- 是否被执行 Build

如果发现某个技能应该放却没放，检查：
1. Check 是否返回 >=0（面板可以看到）
2. SlotMode 窗口是否匹配（GCD 好了吗？oGCD 窗口打开了吗？）
3. 有没有更高优先级的技能抢在前面

---

## 10. 附录：接口速查表

### 10.1 你必须实现的

| 接口 | 方法 | 说明 |
|------|------|------|
| `IRotationEntry` | `Build(string folder) → Rotation?` | 创建 Rotation 容器 |
| `IRotationEntry` | `GetRotationUI() → IRotationUI?` | 返回 UI 注册 |
| `ISlotResolver` | `int Check()` | 判断技能是否可用 |
| `ISlotResolver` | `void Build(Slot slot)` | 构建技能槽位 |
| `IRotationEventHandler` | 11 个回调（`OnBeforeSpellCast` 有默认实现） | 事件响应 |

### 10.2 你可能用到的

| 接口 | 关键方法 | 说明 |
|------|---------|------|
| `IOpener` | `Sequence`, `StartCheck()`, `StopCheck()`, `InitCountDown()` | 起手爆发序列 |
| `ISlotSequence` | `Sequence`, `StartCheck()`, `StopCheck()` | 连招序列 |
| `ITriggerCond` | `bool Handle(params)` | 全局触发条件 |
| `ITriggerAction` | `bool Handle()` | 全局触发动作 |
| `IHotkeyEventHandler` | `HandleKeyPress()`, `OnHotkeyExecuted()` | 热键事件处理 |

### 10.3 Slot / SlotAction 链式方法

```
slot.Add(spell)                       // 添加技能
slot.Add(action)                      // 添加 SlotAction
slot.Insert(action, index)            // 插入到指定位置
slot.Add2NdWindowAbility(spell)       // 第二个 oGCD 窗口添加
slot.AddDelaySpell(delayMs, spell)    // 延迟后添加
slot.AppendSequence(sequence, wait)   // 追加序列
```

### 10.4 Spell 构造便利签

```
new Spell()                           // Idle (什么都不做)
new Spell(97, SpellTargetType.Target) // ID=97, 对目标施放
new Spell(97, targetObj)              // ID=97, 指定对象
new Spell(97, () => GetTarget())      // ID=97, 动态目标
new Spell(97, new Vector3(x,y,z))     // ID=97, 地面目标
new Spell(itemId, isHq: true)         // 道具
Spell.CreatePotion()                  // 药水
Spell.CreateSprint()                  // 疾跑
Spell.CreateLimitBreak()              // 极限技
Spell.CreateDance()                   // 舞步
Spell.Idle                            // 空操作占位符
```

### 10.5 常用技能 ID 参考

| 类别 | 技能名 | ID |
|------|--------|-----|
| 通用 | 疾跑 | 3 |
| 通用 | 极限技 | (自动) |
| 近战 | 牵制 | 7549 |
| 远程 | 速行 | 7557 |
| 法系 | 醒梦 | 7562 |
| T | 减伤（铁壁） | 7531 |
| T | 挑衅 | 7533 |
| T | 退避 | 7535 |
| 奶 | 康复 | 7568 |

> 完整技能 ID 请查询 FFXIV 数据网站（如 Garland Tools、XIVAPI）或使用 Dalamud 的 Data Window。

---

## 参考

- [HiAuRo 示例 ACR (完整参考实现)](https://github.com/destesasinsh/HiAuRo-SampleACR)
- [HiAuRo 架构设计](./ARCHITECTURE.md)
- [HiAuRo 项目章程](./PROJECT.md)
- [HiAuRo 需求文档](./REQUIREMENTS.md)
- [OmenTools 使用指南](./OMEN_TOOLS_USAGE.md)

> **下一步**：读完本指南后，建议克隆 [HiAuRo-SampleACR](https://github.com/destesasinsh/HiAuRo-SampleACR) 示例仓库对照学习实际写法。
