using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;
using OmenTools.OmenService;
using FFXIVClientStructs.FFXIV.Client.Game;
using HiAuRo.Execution;
using HiAuRo.Execution.Events;

namespace HiAuRo.Runtime;

/// <summary>
/// 底层游戏事件监听分发
/// </summary>
public static class EventSystem
{
    private static readonly object _lock = new();
    private static readonly List<Action<uint, ulong>> _onActionUsedHandlers = [];
    private static readonly List<Action<UseActionEvent>> _onUseActionHandlers = [];
    private static readonly List<Action<uint>> _onActionCompletedHandlers = [];
    private static readonly List<Action<IGameObject?>> _onTargetChangedHandlers = [];

    private static nint _lastTargetAddress;
    private static bool _initialized;

    /// <summary>最近一次成功执行的技能 ID（供触发条件使用）</summary>
    public static uint LastCompletedActionId { get; private set; }

    /// <summary>上一次成功执行的技能 ID（连击系统用）</summary>
    public static uint LastComboSpellId { get; private set; }

    /// <summary>UseActionManager.PostUseAction 的原始返回信息。</summary>
    public readonly record struct UseActionEvent(
        bool Result,
        ActionType ActionType,
        uint ActionId,
        ulong TargetId,
        ActionManager.UseActionMode QueueState,
        uint ComboRouteId);

    /// <summary>去重：自记录 ID 集合，防止 OnPostUseAction 同 ID 二次覆盖</summary>
    private static readonly HashSet<uint> _selfRecordedIds = [];

    /// <summary>
    /// SlotExecutor 自行调用的记录入口（绕过 OmenTools Hook）。
    /// 调用时机是 UseAction 返回成功或检测到已排队；因此 RecentlyUsed 记录的是提交时间，
    /// 不是读条完成时间，也不是服务端 ActionEffect 结算时间。
    /// </summary>
    public static void OnUseActionSuccess(uint actionId, HiAuRo.ACR.SpellType spellType)
    {
        _selfRecordedIds.Add(actionId);
        if (_selfRecordedIds.Count > 8)
            _selfRecordedIds.Remove(_selfRecordedIds.Min());

        LastCompletedActionId = actionId;
        HiAuRo.ACR.SpellHistoryHelper.RecordSpell(actionId);
        if (spellType is HiAuRo.ACR.SpellType.RealGcd or HiAuRo.ACR.SpellType.GeneralGcd)
        {
            HiAuRo.ACR.SpellHistoryHelper.RecordGcd();
        }
        Hi.AcrRuntimeDebug($"[EventSystem] 自行记录: id={actionId} LastCompleted={LastCompletedActionId}");
    }

    /// <summary>初始化事件系统</summary>
    public static void Init()
    {
        if (_initialized) return;

        UseActionManager.Instance().RegPreUseAction(OnPreUseAction);
        UseActionManager.Instance().RegPostUseAction(OnPostUseAction);

        _initialized = true;
    }

    /// <summary>关闭事件系统</summary>
    public static void Shutdown()
    {
        UseActionManager.Instance().Unreg(OnPreUseAction);
        UseActionManager.Instance().Unreg(OnPostUseAction);

        _onActionUsedHandlers.Clear();
        _onUseActionHandlers.Clear();
        _onActionCompletedHandlers.Clear();
        _onTargetChangedHandlers.Clear();
        _lastTargetAddress = nint.Zero;

        _initialized = false;
    }

    /// <summary>检查目标变更</summary>
    public static void CheckTargetChanged()
    {
        var currentTargetAddress = ReadCurrentTargetAddress();
        if (ShouldPublishTargetChanged(_lastTargetAddress, currentTargetAddress))
        {
            _lastTargetAddress = currentTargetAddress;
            var currentTarget = DService.Instance().ObjectTable.CreateObjectReference(currentTargetAddress);
            Action<IGameObject?>[] snapshot;
            lock (_lock) { snapshot = _onTargetChangedHandlers.ToArray(); }
            foreach (var handler in snapshot)
                handler(currentTarget);
        }
    }

    #region 注册 / 注销

    /// <summary>注册技能使用回调</summary>
    public static void RegisterOnActionUsed(Action<uint, ulong> handler)
    { lock (_lock) _onActionUsedHandlers.Add(handler); }

    /// <summary>注销技能使用回调</summary>
    public static void UnregisterOnActionUsed(Action<uint, ulong> handler)
    { lock (_lock) _onActionUsedHandlers.Remove(handler); }

    /// <summary>注册底层 UseAction 返回回调（包含 result/queueState）。</summary>
    public static void RegisterOnUseAction(Action<UseActionEvent> handler)
    { lock (_lock) _onUseActionHandlers.Add(handler); }

    /// <summary>注销底层 UseAction 返回回调。</summary>
    public static void UnregisterOnUseAction(Action<UseActionEvent> handler)
    { lock (_lock) _onUseActionHandlers.Remove(handler); }

    /// <summary>注册技能完成回调</summary>
    public static void RegisterOnActionCompleted(Action<uint> handler)
    { lock (_lock) _onActionCompletedHandlers.Add(handler); }

    /// <summary>注销技能完成回调</summary>
    public static void UnregisterOnActionCompleted(Action<uint> handler)
    { lock (_lock) _onActionCompletedHandlers.Remove(handler); }

    /// <summary>注册目标变更回调</summary>
    public static void RegisterOnTargetChanged(Action<IGameObject?> handler)
    { lock (_lock) _onTargetChangedHandlers.Add(handler); }

    /// <summary>注销目标变更回调</summary>
    public static void UnregisterOnTargetChanged(Action<IGameObject?> handler)
    { lock (_lock) _onTargetChangedHandlers.Remove(handler); }

    #endregion

    #region Hook 回调

    private static void OnPreUseAction(
        ref bool isPrevented, ref ActionType actionType, ref uint actionId, ref ulong targetId,
        ref uint extraParam, ref ActionManager.UseActionMode queueState, ref uint comboRouteId)
    {
    }

    private static void OnPostUseAction(
        bool result, ActionType actionType, uint actionId, ulong targetId,
        uint extraParam, ActionManager.UseActionMode queueState, uint comboRouteId)
    {
        Hi.AcrRuntimeDebug($"[EventSystem] OnPostUseAction: result={result} actionId={actionId} targetId={targetId:X} comboRouteId={comboRouteId}");

        if (result)
        {
            // 去重：已通过 OnUseActionSuccess 自行记录过的 ID 不再更新 Combo/History
            if (_selfRecordedIds.Remove(actionId))
            {
                Hi.AcrRuntimeDebug($"[EventSystem] OnPostUseAction: id={actionId} 已自行记录, 跳过状态更新");
            }
            else
            {
                LastComboSpellId = LastCompletedActionId;
                LastCompletedActionId = actionId;
                HiAuRo.ACR.SpellHistoryHelper.RecordSpell(actionId);
            }

            Hi.AcrRuntimeDebug($"[EventSystem] 技能成功: id={actionId} type={actionType} target={targetId:X} LastCombo={LastComboSpellId} LastCompleted={LastCompletedActionId}");
            Action<uint>[] completedSnapshot;
            lock (_lock) { completedSnapshot = _onActionCompletedHandlers.ToArray(); }
            foreach (var handler in completedSnapshot)
            {
                try { handler(actionId); }
                catch (Exception ex) { DService.Instance().Log.Error($"[EventSystem] OnActionCompleted handler 异常: {ex}"); }
            }

            // 通知执行轴/辅助轴：技能执行成功事件
            try
            {
                var afterSpell = new AfterSpellCondParams { SpellId = actionId };
                ExecutionAxis.Instance.UseCondParams(afterSpell);
                AssistAxis.Instance.UseCondParams(afterSpell);
            }
            catch (Exception ex)
            {
                DService.Instance().Log.Error($"[EventSystem] UseCondParams 异常: {ex}");
            }
        }

        Action<uint, ulong>[] usedSnapshot;
        Action<UseActionEvent>[] useActionSnapshot;
        lock (_lock)
        {
            usedSnapshot = _onActionUsedHandlers.ToArray();
            useActionSnapshot = _onUseActionHandlers.ToArray();
        }
        foreach (var handler in usedSnapshot)
        {
            try { handler(actionId, targetId); }
            catch (Exception ex) { DService.Instance().Log.Error($"[EventSystem] OnActionUsed handler 异常: {ex}"); }
        }

        var ev = new UseActionEvent(result, actionType, actionId, targetId, queueState, comboRouteId);
        foreach (var handler in useActionSnapshot)
        {
            try { handler(ev); }
            catch (Exception ex) { DService.Instance().Log.Error($"[EventSystem] OnUseAction handler 异常: {ex}"); }
        }
    }

    #endregion

    internal static bool ShouldPublishTargetChangedForTests(nint previousTargetAddress, nint currentTargetAddress)
        => ShouldPublishTargetChanged(previousTargetAddress, currentTargetAddress);

    private static bool ShouldPublishTargetChanged(nint previousTargetAddress, nint currentTargetAddress)
        => previousTargetAddress != currentTargetAddress;

    private static unsafe nint ReadCurrentTargetAddress()
        => (nint)TargetManager.ToStruct()->GetHardTarget();
}
