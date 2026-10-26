using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;

namespace HiAuRo.ACR;

/// <summary>
/// 技能目标过滤类型
/// </summary>
public enum SpellTargetLimitType
{
    /// <summary>HP 百分比过滤</summary>
    HP,
    /// <summary>职业职能过滤</summary>
    JobRole,
}

/// <summary>
/// 技能目标限制过滤基类 —— 用于在 Spell.GetTarget() 中对已解析目标做后置过滤
/// </summary>
public abstract class SpellTargetLimit
{
    public SpellTargetLimitType Type { get; }
    protected SpellTargetLimit(SpellTargetLimitType type) => Type = type;

    /// <summary>返回 true 表示目标通过过滤</summary>
    public abstract bool Pass(IGameObject target);
}
