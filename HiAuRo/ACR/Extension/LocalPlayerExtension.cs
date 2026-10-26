using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using Lumina.Excel.Sheets;

namespace HiAuRo.ACR.Extension;

/// <summary>
/// IPlayerCharacter 扩展方法 —— 道具/LB/仇恨/移动/任务/目标
/// </summary>
public static class LocalPlayerExtension
{
    #region Item

    public static unsafe uint GetItemCount(this IPlayerCharacter lp, uint itemId, bool isHq = false)
        => (uint)InventoryManager.Instance()->GetInventoryItemCount(itemId, isHq, true, true, 0);

    public static unsafe bool UseItem(this IPlayerCharacter lp, uint itemId, bool isHq = false)
        => ActionManager.Instance()->UseAction(
            ActionType.Item, isHq ? itemId + 1000000U : itemId,
            Data.Me.Object?.EntityID ?? 0, ushort.MaxValue, 0, 0, null);

    public static unsafe TimeSpan GetItemCoolDown(this IPlayerCharacter lp, uint itemId)
    {
        var elapsed = ActionManager.Instance()->GetRecastTimeElapsed(ActionType.Item, itemId);
        if (elapsed == 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(ActionManager.Instance()->GetRecastTime(ActionType.Item, itemId))
             - TimeSpan.FromSeconds(elapsed);
    }

    #endregion

    #region Limit Break

    public static unsafe LimitBreakController LimitBreakController(this IPlayerCharacter lp)
        => UIState.Instance()->LimitBreakController;

    public static unsafe byte LimitBreakBarCount(this IPlayerCharacter lp)
        => UIState.Instance()->LimitBreakController.BarCount;

    public static unsafe uint LimitBreakBarValue(this IPlayerCharacter lp)
        => UIState.Instance()->LimitBreakController.BarUnits;

    public static unsafe ushort LimitBreakCurrentValue(this IPlayerCharacter lp)
        => UIState.Instance()->LimitBreakController.CurrentUnits;

    #endregion

    #region Enmity

    public static unsafe IBattleChara? GetHighestEnmityGameObject(this IPlayerCharacter lp)
    {
        var target = (lp as IGameObject)?.TargetObject;
        if (target == null) return null;

        return UIState.Instance()->Hate.HateInfo.ToArray()
            .MaxBy(h => h.Enmity).EntityId
            .ToGameObject<IBattleChara>();
    }

    public static unsafe IBattleChara? GetHighestEnmityTank(this IPlayerCharacter lp)
    {
        var target = (lp as IGameObject)?.TargetObject;
        if (target == null) return null;

        return UIState.Instance()->Hate.HateInfo.ToArray()
            .Select(h => (Enmity: h.Enmity, Ch: h.EntityId.ToGameObject<IBattleChara>()))
            .Where(x => x.Ch != null && x.Ch.IsTank())
            .MaxBy(x => x.Enmity).Ch;
    }

    #endregion

    #region Movement / Mount

    public static unsafe bool IsMoving(this IPlayerCharacter lp)
        => AgentMap.Instance()->IsPlayerMoving;

    public static bool IsMounted(this ICharacter chr)
        => DService.Instance().Condition?[ConditionFlag.Mounted] == true;

    public static bool IsFlight(this ICharacter chr)
        => DService.Instance().Condition?[ConditionFlag.RidingPillion] == true;

    #endregion

    #region Quest

    public static bool IsQuestComplete(this IPlayerCharacter lp, uint questId)
        => QuestManager.IsQuestComplete(questId);

    public static unsafe bool IsQuestAccepted(this IPlayerCharacter lp, uint questId)
        => QuestManager.Instance()->IsQuestAccepted(questId);

    #endregion

    #region Target

    public static void SetTarget(this IPlayerCharacter lp, IGameObject? target)
        => OmenTools.OmenService.TargetManager.Target = target;

    public static void ClearTarget(this IPlayerCharacter lp)
        => lp.SetTarget(null);

    public static unsafe bool InteractWithObject(this IPlayerCharacter lp, IGameObject obj)
        => TargetSystem.Instance()->InteractWithObject((CSGameObject*)obj.Address, true) > 0;

    #endregion

    #region PvP

    public static bool IsPvP(this IPlayerCharacter lp)
    {
        var territory = DService.Instance().ClientState.TerritoryType;
        if (territory == 0) return false;
        var row = DService.Instance().Data.GetExcelSheet<TerritoryType>()?.GetRow(territory);
        return row?.IsPvpZone ?? false;
    }

    #endregion
}
