using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;

namespace HiAuRo.ACR;

/// <summary>
/// HP 阈值过滤 —— 按目标 HP 百分比限制技能目标
/// </summary>
public sealed class SpellTargetLimit_HP : SpellTargetLimit
{
    public enum Mode { Below, Above }

    private readonly Mode _mode;
    private readonly float _threshold; // 0.0 ~ 1.0

    /// <param name="mode">Below=低于阈值, Above=高于阈值</param>
    /// <param name="threshold">HP 百分比阈值（0.0~1.0）</param>
    public SpellTargetLimit_HP(Mode mode, float threshold) : base(SpellTargetLimitType.HP)
    {
        _mode = mode;
        _threshold = threshold;
    }

    public override bool Pass(IGameObject target)
    {
        if (target is not ICharacter ch) return false;
        if (ch.MaxHp == 0) return false;

        float pct = (float)ch.CurrentHp / ch.MaxHp;
        return _mode switch
        {
            Mode.Below => pct <= _threshold,
            Mode.Above => pct > _threshold,
            _ => false
        };
    }
}
