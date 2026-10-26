using FFXIVClientStructs.FFXIV.Client.Game;
using HiAuRo.Infrastructure;
using OmenTools.Extensions;
using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;
using System.Numerics;

namespace HiAuRo.ACR;

/// <summary>
/// Spell / uint 扩展方法 —— ACR 作者 Check() 中最常用的技能就绪判断
/// </summary>
public static class SpellExtension
{
    #region IsAbility

    /// <summary>是否为能力技（含非 Default 类别：疾跑/药水/LB/舞蹈/道具）</summary>
    public static bool IsAbilityEx(this Spell spell)
        => spell.SpellCategory != SpellCategory.Default || spell.Type == SpellType.Ability;

    /// <summary>spellId 是否为能力技</summary>
    public static bool IsAbilityEx(this uint spellId)
    {
        var row = SpellHelper.GetActionRow(spellId);
        if (row == null) return false;
        return row.Value.ActionCategory.RowId != 0; // 非 GCD 类别 = 能力技
    }

    #endregion

    #region IsUnlock

    /// <summary>技能是否已解锁（OmenTools 已含等级检查，不重复查询 Lumina）</summary>
    public static unsafe bool IsUnlock(this uint spellId)
    {
        return OmenTools.Extensions.ActionManagerExtension.IsActionUnlocked(spellId);
    }

    /// <summary>Spell 版</summary>
    public static bool IsUnlock(this Spell spell) => spell.Id.IsUnlock();

    #endregion

    #region IsUnlockWithCDCheck

    /// <summary>已解锁 + CD 转好了</summary>
    public static bool IsUnlockWithCDCheck(this uint spellId)
    {
        if (!spellId.IsUnlock()) return false;
        return SpellHelper.CanUseSpell(spellId);
    }

    public static bool IsUnlockWithCDCheck(this Spell spell) => spell.Id.IsUnlockWithCDCheck();

    #endregion

    #region IsReadyWithCanCast

    /// <summary>综合就绪检查：GetActionStatus 原生判断 + RecentlyUsed 去重 + 预排队容差</summary>
    public static bool IsReadyWithCanCast(this Spell spell)
    {
        if (!SpellHelper.IsActionReady(spell.Id, ResolveTargetForCheck(spell))) return false;
        if (SpellHistoryHelper.RecentlyUsed(spell.Id, 500)) return false;

        var charges = spell.Charges;
        if (charges >= 1) return true;
        if (charges <= 0 && spell.CooldownMs <= PluginConfig.Instance.ActionQueueInMs) return true;
        return false;
    }

    private static ulong ResolveTargetForCheck(Spell spell)
    {
        return spell.TargetType switch
        {
            SpellTargetType.Self => Data.Me.Object?.GameObjectID ?? 0xE000_0000,
            SpellTargetType.Target => Data.Target.Current?.GameObjectID ?? 0xE000_0000,
            SpellTargetType.TargetTarget => Data.Target.Current is IBattleChara bc
                ? (ulong)bc.TargetObjectID : 0xE000_0000,
            SpellTargetType.SpecifyTarget => spell.SpecifyTarget is IGameObject sgo
                ? sgo.GameObjectID : 0xE000_0000,
            SpellTargetType.DynamicTarget => spell.GetDynamicTarget?.Invoke() is IGameObject dgo
                ? dgo.GameObjectID : 0xE000_0000,
            SpellTargetType.Pm1 or SpellTargetType.Pm2 or SpellTargetType.Pm3 or SpellTargetType.Pm4
            or SpellTargetType.Pm5 or SpellTargetType.Pm6 or SpellTargetType.Pm7 or SpellTargetType.Pm8
                => GetPartyMemberGameObjectID(spell.TargetType),
            _ => 0xE000_0000
        };
    }

    private static ulong GetPartyMemberGameObjectID(SpellTargetType targetType)
    {
        var idx = (int)targetType - (int)SpellTargetType.Pm1;
        var selfId = Data.Me.Object?.GameObjectID ?? 0;
        
        // PM1-PM8 排除自己，所以需要跳过自己在 All 中的位置
        var count = 0;
        foreach (var member in Data.Party.All)
        {
            if (member.Player?.GameObjectID == selfId) continue;
            if (count == idx)
                return member.Player?.GameObjectID ?? 0xE000_0000;
            count++;
        }
        return 0xE000_0000;
    }

    #endregion

    #region IsMaxChargeReady

    /// <summary>充能是否接近满层</summary>
    public static bool IsMaxChargeReady(this uint spellId, float delta = 0.5f)
    {
        var current = SpellHelper.GetCharges(spellId);
        var max = SpellHelper.GetMaxCharges(spellId);
        return current >= max - delta;
    }

    public static bool IsMaxChargeReady(this Spell spell, float delta = 0.5f)
        => spell.Id.IsMaxChargeReady(delta);

    #endregion

    #region CoolDownInGCDs

    /// <summary>CD 是否在 N 个 GCD 内转好</summary>
    public static bool CoolDownInGCDs(this uint spellId, int gcdCount)
    {
        if (SpellHelper.CanUseSpell(spellId)) return true;
        var remaining = SpellHelper.GetCooldownRemaining(spellId);
        return remaining <= gcdCount * GCDHelper.GetGCDDuration();
    }

    public static bool CoolDownInGCDs(this Spell spell, int gcdCount)
        => spell.Id.CoolDownInGCDs(gcdCount);

    #endregion

    #region Ability window prediction

    /// <summary>能力技 CD 在接下来 N 个 GCD 窗口内转好</summary>
    public static bool AbilityCoolDownInNextGCDsWindow(this Spell spell, int count = 2)
    {
        if (SpellHelper.CanUseSpell(spell.Id)) return true;
        return SpellHelper.GetCooldownRemaining(spell.Id) <= count * GCDHelper.GetGCDDuration();
    }

    public static bool AbilityCoolDownInNextGCDsWindow(this uint spellId, int count = 2)
    {
        if (SpellHelper.CanUseSpell(spellId)) return true;
        return SpellHelper.GetCooldownRemaining(spellId) <= count * GCDHelper.GetGCDDuration();
    }

    #endregion

    #region RecentlyUsed

    /// <summary>技能在指定 ms 内是否刚使用过</summary>
    public static bool RecentlyUsed(this uint spellId, int timeMs = 1200)
        => SpellHistoryHelper.RecentlyUsed(spellId, timeMs);

    public static bool RecentlyUsed(this Spell spell, int timeMs = 1200)
        => spell.Id.RecentlyUsed(timeMs);

    #endregion

    #region Level check

    /// <summary>等级是否满足</summary>
    public static bool IsLevelEnough(this uint spellId)
    {
        var row = SpellHelper.GetActionRow(spellId);
        if (row == null) return false;
        return Data.Me.CurrentLevel >= row.Value.ClassJobLevel;
    }

    public static bool IsLevelEnough(this Spell spell) => spell.Id.IsLevelEnough();

    #endregion

    #region GetSpell — spellId → Spell 便利工厂

    /// <summary>skillId → Spell（默认 Target 目标）</summary>
    public static Spell GetSpell(this uint spellId)
        => new(spellId, SpellTargetType.Target);

    /// <summary>skillId → Spell（指定目标类型）</summary>
    public static Spell GetSpell(this uint spellId, SpellTargetType targetType)
        => new(spellId, targetType);

    /// <summary>skillId → Spell（指定目标对象，如 party member）</summary>
    public static Spell GetSpell(this uint spellId, IBattleChara target)
        => new(spellId, target);

    /// <summary>skillId → Spell（地面放置，指定坐标）</summary>
    public static Spell GetSpell(this uint spellId, Vector3 pos)
        => new(spellId, pos);

    #endregion

    #region GetActionChange — 技能变身检测

    /// <summary>获取技能的变身 ID（如火苗 → 火三、雷云 → 雷三）</summary>
    public static unsafe uint GetActionChange(this uint spellId)
    {
        var am = ActionManager.Instance();
        if (am == null) return spellId;
        var adjusted = am->GetAdjustedActionId(spellId);
        return adjusted != 0 ? adjusted : spellId;
    }

    #endregion

    /// <summary>Standalone Cast。在事件回调中即时释放技能。</summary>
    public static async Task<bool> CastAsync(this Spell spell)
    {
        var slot = new Slot(spell) { Source = "Cast" };
        return await Runtime.SpellCast.ExecuteAsync(slot, spell, Runtime.ACRLifecycle.Runner);
    }
}
