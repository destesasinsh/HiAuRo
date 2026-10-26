using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;

namespace HiAuRo.ACR;

/// <summary>
/// 职业职能过滤 —— 按坦克/治疗/近战/远敏/法系限制技能目标
/// </summary>
public sealed class SpellTargetLimit_Job : SpellTargetLimit
{
    public enum Filter { Tank, Healer, Melee, Ranged, Caster }

    private readonly Filter _filter;

    public SpellTargetLimit_Job(Filter filter) : base(SpellTargetLimitType.JobRole)
    {
        _filter = filter;
    }

    public override bool Pass(IGameObject target)
    {
        if (target is not ICharacter ch) return false;

        var jobId = ch.ClassJob.RowId;
        if (jobId == 0) return false;

        return _filter switch
        {
            Filter.Tank   => IsTank(jobId),
            Filter.Healer => IsHealer(jobId),
            Filter.Melee  => IsMeleeDps(jobId),
            Filter.Ranged => IsRangedDps(jobId),
            Filter.Caster => IsCasterDps(jobId),
            _ => false
        };
    }

    // FFXIV ClassJob RowId → 职能映射（DT 7.x）
    private static bool IsTank(uint id) => id is 19 or 21 or 32 or 37;
    private static bool IsHealer(uint id) => id is 24 or 28 or 33 or 40;
    private static bool IsMeleeDps(uint id) => id is 20 or 22 or 30 or 34 or 39 or 41;
    private static bool IsRangedDps(uint id) => id is 23 or 31 or 38;
    private static bool IsCasterDps(uint id) => id is 25 or 27 or 35 or 42;
}
