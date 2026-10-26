using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;

namespace HiAuRo.ACR;

/// <summary>
/// Buff / DoT 检测辅助
/// </summary>
public static class AuraHelper
{
    /// <summary>指定对象是否存在 buff</summary>
    public static bool HasAura(IGameObject? target, uint buffId)
    {
        if (target is not IBattleChara bc) return false;
        return bc.StatusList.HasStatus(buffId);
    }

    /// <summary>指定对象是否存在任意 buff</summary>
    public static bool HasAnyAura(IGameObject? target, params uint[] buffIds)
    {
        if (target is not IBattleChara bc) return false;
        return buffIds.Any(bc.StatusList.HasStatus);
    }

    /// <summary>指定对象上 buff 剩余时间 (ms)。不存在时返回 0</summary>
    public static float GetAuraTimeLeft(IGameObject? target, uint buffId, uint sourceId = 0xE0000000)
    {
        if (target is not IBattleChara bc) return 0;
        var list = bc.StatusList;
        for (int i = 0; i < list.Length; i++)
        {
            var s = list[i];
            if (s == null) continue;
            if (s.StatusID == buffId && (sourceId == 0xE0000000 || s.SourceID == sourceId))
                return s.RemainingTime;
        }
        return 0;
    }

    /// <summary>自身是否有 buff</summary>
    public static bool HasSelfAura(uint buffId) =>
        HasAura(Data.Me.Object, buffId);

    /// <summary>目标上是否有 buff</summary>
    public static bool HasTargetAura(uint buffId) =>
        HasAura(Data.Target.Current, buffId);

    private static readonly uint[] InvincibleBuffIds = [325, 529, 656, 671, 775, 776, 969, 981, 1570, 1697, 1829, 328, 2287, 2670, 3012, 3039, 3255, 811, 810, 409, 1836];

    /// <summary>目标身上是否有无敌buff</summary>
    public static bool HasInvincibleBuffs(IGameObject? target) =>
        HasAnyAura(target, InvincibleBuffIds);
}
