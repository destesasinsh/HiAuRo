using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;

namespace HiAuRo.ACR;

/// <summary>
/// Spell 计算属性 + 便利构造函数（partial 补充）
/// </summary>
public sealed partial class Spell
{
    // === 便利构造函数 ===

    public Spell() { }
    public Spell(uint id, SpellTargetType targetType) { Id = id; TargetType = targetType; }
    public Spell(uint id, IBattleChara target) { Id = id; TargetType = SpellTargetType.SpecifyTarget; SpecifyTarget = target; }
    public Spell(uint id, Func<IBattleChara> getTargetFunc) { Id = id; TargetType = SpellTargetType.DynamicTarget; GetDynamicTarget = () => getTargetFunc(); }
    public Spell(uint id, Vector3 pos) { Id = id; TargetType = SpellTargetType.Location; UsePos = pos; }
    public Spell(uint itemId, bool isHq) { Id = itemId; SpellCategory = SpellCategory.Potion; TargetType = SpellTargetType.Self; Hq = isHq; Name = $"Item_{itemId}"; }

    /// <summary>链式：不使用 GCD 优化偏移</summary>
    public Spell DontUseGcd() { DontUseGcdOpt = true; return this; }

    /// <summary>复制一个 Spell，并覆盖执行期选项</summary>
    public Spell WithExecutionOptions(bool? waitServerAcq = null, int? gcdGuardMs = null)
    {
        return new Spell
        {
            Id = Id,
            Name = _name,
            TargetType = TargetType,
            SpellCategory = SpellCategory,
            Type = Type,
            SpecifyTarget = SpecifyTarget,
            GetDynamicTarget = GetDynamicTarget,
            UsePos = UsePos,
            DontUseGcdOpt = DontUseGcdOpt,
            WaitServerAcq = waitServerAcq ?? WaitServerAcq,
            GcdGuardMs = gcdGuardMs ?? GcdGuardMs,
            Hq = Hq,
            TargetLimits = TargetLimits
        };
    }

    /// <summary>静态工厂：创建药水 Spell</summary>
    public static Spell CreatePotion() => CreatePotion(false);
    public static Spell CreatePotion(bool isHq) => new(ItemHelper.GetCurrJobPotionActionId(isHq), SpellTargetType.Self)
    {
        SpellCategory = SpellCategory.Potion,
        Type = SpellType.Ability,
        Hq = isHq,
        Name = "爆发药"
    };
    /// <summary>静态工厂：创建疾跑 Spell</summary>
    public static Spell CreateSprint() => new(3, SpellTargetType.Self) { Type = SpellType.Ability, SpellCategory = SpellCategory.Sprint };
    /// <summary>静态工厂：创建极限技 Spell</summary>
    public static Spell CreateLimitBreak() => new(0, SpellTargetType.Target) { SpellCategory = SpellCategory.LimitBreak };
    /// <summary>静态工厂：创建舞步 Spell</summary>
    public static Spell CreateDance() => new(0, SpellTargetType.Self) { SpellCategory = SpellCategory.Dance };

    // === 计算属性（实时读取游戏数据）===

    /// <summary>冷却剩余时间</summary>
    public unsafe TimeSpan Cooldown => TimeSpan.FromSeconds(
        Math.Max(0, ActionManager.Instance()->GetRecastTime(ActionType.Action, Id)
                      - ActionManager.Instance()->GetRecastTimeElapsed(ActionType.Action, Id)));

    /// <summary>冷却剩余时间（毫秒）</summary>
    public float CooldownMs => (float)Cooldown.TotalMilliseconds;

    /// <summary>冷却剩余时间（秒）</summary>
    public float CooldownSec => (float)Cooldown.TotalSeconds;

    /// <summary>充能层数</summary>
    public unsafe float Charges => (int)ActionManager.Instance()->GetCurrentCharges(Id);

    /// <summary>最大充能层数</summary>
    public unsafe int MaxCharges => ActionManager.GetMaxCharges(Id, 0);

    /// <summary>咏唱时间（传技能 ID，由游戏计算 Swiftcast 等状态）</summary>
    public unsafe TimeSpan CastTime
    {
        get
        {
            int adjusted = ActionManager.GetAdjustedCastTime(ActionType.Action, Id);
            return TimeSpan.FromMilliseconds(adjusted);
        }
    }

    /// <summary>GCD 复唱时间</summary>
    public unsafe TimeSpan RecastTime
    {
        get
        {
            var row = SpellHelper.GetActionRow(Id);
            if (row == null) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(
                ActionManager.GetAdjustedRecastTime((ActionType)row.Value.ActionCategory.RowId, row.Value.Recast100ms) * 100f);
        }
    }

    /// <summary>技能射程</summary>
    public float ActionRange => SpellHelper.GetActionRow(Id)?.Range ?? 0f;

    /// <summary>MP 消耗</summary>
    public uint MPNeed => SpellHelper.GetActionRow(Id)?.PrimaryCostValue ?? 0;
}
