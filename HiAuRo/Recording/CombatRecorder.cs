using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.Game;
using HiAuRo.ACR;
using HiAuRo.Execution.Events;
using HiAuRo.Runtime;
using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;
using OmenTools.OmenService;
using ActionTypeFF = FFXIVClientStructs.FFXIV.Client.Game.ActionType;

namespace HiAuRo.Recording;

/// <summary>战斗操作录制器：只在技能相关事件点写 jsonl，平时只维护最近帧缓存。</summary>
public sealed class CombatRecorder
{
    public static CombatRecorder Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();
    private readonly CombatFrameWindowBuffer _frames = new(1000);
    private string _sessionId = "";
    private string _sessionPath = "";
    private long _sampleIndex;
    private long _frameIndex;
    private bool _initialized;
    private bool _lastManualPauseApplied;
    private uint _lastEffectActionId;
    private long _lastEffectAt;
    private uint _pendingCastGcdActionId;
    private long _pendingCastGcdRecordedAt;
    private readonly Queue<PendingCombatRecorderEvent> _pendingEvents = new();
    private float _lastObservedGcdCooldown = -1;
    private long _lastGcdCooldownChangedAt;
    private long _lastStallReportedAt;

    private CombatRecorder() { }

    public bool IsRecording => IsEnabledForCurrentState();

    public string SessionId => _sessionId;

    public string CurrentLogPath => _sessionPath;

    public string LogRoot => ResolveLogRoot(
        PluginConfig.Instance.CombatRecorderLogRoot,
        @"E:\DalamudPlugins\HiAuRo");

    public void Init()
    {
        if (_initialized) return;
        _initialized = true;

        EventSystem.RegisterOnUseAction(OnUseAction);
        GameEventHook.Instance.OnEventFired += OnGameEvent;
    }

    public void Shutdown()
    {
        if (!_initialized) return;
        _initialized = false;

        EventSystem.UnregisterOnUseAction(OnUseAction);
        GameEventHook.Instance.OnEventFired -= OnGameEvent;
        _frames.Clear();
    }

    public void UpdateFrameCache()
    {
        if (!_initialized) return;

        ApplyManualSourcePause();

        if (!IsEnabledForCurrentState())
        {
            _frames.Clear();
            ResetTransientState();
            return;
        }

        var now = Environment.TickCount64;
        _frames.Push(CaptureCacheSample(now));
        ProcessPendingEvent(now);
        TryEmitStallEvent(now);
    }

    public int ClearOldLogs(int keepDays)
    {
        if (!Directory.Exists(LogRoot)) return 0;
        var cutoff = DateTime.Now.AddDays(-Math.Max(1, keepDays));
        var deleted = 0;

        foreach (var file in Directory.GetFiles(LogRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime >= cutoff) continue;
                info.Delete();
                deleted++;
            }
            catch (Exception ex)
            {
                DService.Instance().Log.Warning($"[CombatRecorder] 清理日志失败 {file}: {ex.Message}");
            }
        }

        return deleted;
    }

    public void OpenLogDirectory()
    {
        Directory.CreateDirectory(LogRoot);
        try { Process.Start("explorer.exe", LogRoot); }
        catch (Exception ex) { DService.Instance().Log.Warning($"[CombatRecorder] 打开日志目录失败: {ex.Message}"); }
    }

    public void StartNewSession()
    {
        lock (_lock)
        {
            var rotated = RotateSession(_sessionId, DateTime.Now, Guid.NewGuid().ToString("N")[..8]);
            _sessionId = rotated.SessionId;
            _sessionPath = BuildSessionPath(_sessionId);
            _sampleIndex = rotated.SampleIndex;
            _frameIndex = rotated.FrameIndex;
            _frames.Clear();
            ResetTransientState();
        }
    }

    private void OnUseAction(EventSystem.UseActionEvent ev)
    {
        if (ev is not { Result: true, ActionId: not 0, ActionType: ActionTypeFF.Action })
            return;

        var isAbilityAction = IsAbilityAction(ev.ActionId);
        if (!ShouldCaptureEvent(CombatRecorderEventType.UseActionSuccess, isAbilityAction, gcdJustActivated: false))
            return;

        CaptureEvent(CombatRecorderEventType.UseActionSuccess, ev.ActionId, success: true);
    }

    private void OnGameEvent(ITriggerCondParams p)
    {
        switch (p)
        {
            case SelfCastStartCondParams sc when IsSelfSource(sc.SourceID):
                if (IsAbilityAction(sc.SpellId)) break;
                _pendingCastGcdActionId = sc.SpellId;
                _pendingCastGcdRecordedAt = sc.StartCastTime;
                break;
            case ReceviceAbilityEffectCondParams ae when IsSelfSource(ae.SourceID):
                if (!ShouldCaptureActionEffect(ae.ActionId)) break;
                if (IsDuplicateEffect(ae.ActionId)) break;
                if (IsAbilityAction(ae.ActionId))
                {
                    if (!ShouldCaptureEvent(CombatRecorderEventType.AbilityEffect, isAbilityAction: true, gcdJustActivated: false)) break;
                    CaptureEvent(CombatRecorderEventType.AbilityEffect, ae.ActionId, success: true);
                    break;
                }

                if (!ShouldCaptureGcdEffect(ae.ActionId, _pendingCastGcdActionId, _pendingCastGcdRecordedAt, Environment.TickCount64)) break;
                CaptureEvent(CombatRecorderEventType.GcdReadyAndAction, ae.ActionId, success: true);
                break;
            case ReceviceNoTargetAbilityEffectCondParams ne when IsSelfSource(ne.SourceID):
                if (!ShouldCaptureActionEffect(ne.ActionId)) break;
                if (IsDuplicateEffect(ne.ActionId)) break;
                if (IsAbilityAction(ne.ActionId))
                {
                    if (!ShouldCaptureEvent(CombatRecorderEventType.AbilityEffect, isAbilityAction: true, gcdJustActivated: false)) break;
                    CaptureEvent(CombatRecorderEventType.AbilityEffect, ne.ActionId, success: true);
                    break;
                }

                if (!ShouldCaptureGcdEffect(ne.ActionId, _pendingCastGcdActionId, _pendingCastGcdRecordedAt, Environment.TickCount64)) break;
                CaptureEvent(CombatRecorderEventType.GcdReadyAndAction, ne.ActionId, success: true);
                break;
        }
    }

    private void CaptureEvent(CombatRecorderEventType eventType, uint actionId, bool? success, bool cancelled = false)
    {
        if (!IsEnabledForCurrentState()) return;

        _pendingEvents.Enqueue(new PendingCombatRecorderEvent(
            eventType,
            actionId,
            success,
            cancelled,
            ResolveGcdKind(eventType, _pendingCastGcdActionId, actionId),
            EventTick: Environment.TickCount64));
    }

    private CombatFrameSnapshot CaptureSnapshot(
        CombatRecorderEventType eventType,
        uint actionId,
        bool? success,
        bool cancelled = false,
        CombatRecorderGcdKind gcdKind = CombatRecorderGcdKind.Unknown)
    {
        EnsureSession();

        var self = Data.Me.Object as IBattleChara;
        var target = Data.Target.Current;
        var targetBattle = target as IBattleChara;
        var source = GetConfiguredSource();
        var includeBuffs = PluginConfig.Instance.CombatRecorderIncludeBuffDetails;

        var snapshot = new CombatFrameSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            FrameIndex = Interlocked.Increment(ref _frameIndex),
            SampleIndex = _sampleIndex,
            SessionId = _sessionId,
            EventType = eventType.ToString(),
            Source = source,
            Job = Data.Me.ClassJob,
            ActionId = actionId,
            ActionName = GetActionName(actionId),
            QueuedActionId = ReadQueuedActionId(),
            CastActionId = self?.CastActionID ?? 0,
            IsAbility = actionId != 0 && IsAbilityAction(actionId),
            GcdKind = gcdKind,
            Success = success,
            Failed = success == false && !cancelled,
            Cancelled = cancelled,
            Hp = self?.CurrentHp ?? 0,
            MaxHp = self?.MaxHp ?? 0,
            Mp = self?.CurrentMp ?? 0,
            MaxMp = self?.MaxMp ?? 0,
            Level = Data.Me.CurrentLevel,
            InCombat = Data.Combat.InCombat,
            IsMoving = Data.Me.IsMoving,
            IsCasting = self?.IsCasting ?? Data.Combat.IsCasting,
            TotalCastTime = self?.TotalCastTime ?? 0,
            CurrentCastTime = self?.CurrentCastTime ?? 0,
            GcdCooldown = GCDHelper.GetGCDCooldown(),
            GcdDuration = GCDHelper.GetGCDDuration(),
            LastComboSpellId = EventSystem.LastComboSpellId,
            TargetId = target?.EntityID ?? 0,
            TargetName = target?.Name ?? "",
            Distance = target != null ? Data.Me.DistanceToObject2D(target) : 0,
            IsBoss = targetBattle?.MaxHp > 100000,
            IsDead = target?.IsDead ?? false,
            IsTargetable = target?.IsTargetable ?? false,
            TargetHp = targetBattle?.CurrentHp ?? 0,
            TargetMaxHp = targetBattle?.MaxHp ?? 0,
            QtStates = QTHelper.GetAll().ToDictionary(q => q.Id, q => q.Value),
            CurrentAcrName = ACRLifecycle.CurrentAcrName,
            CurrentRotationName = ACRLifecycle.Runner.CurrentRotation?.GetType().Name ?? "",
            JobGauge = CaptureJobGauge(self),
            ResolverResults = CaptureResolvers(),
            TrackedSkills = CaptureTrackedSkills()
        };

        snapshot.IsGcd = actionId != 0 && !snapshot.IsAbility;
        snapshot.HpPercent = Percent(snapshot.Hp, snapshot.MaxHp);
        snapshot.MpPercent = Percent(snapshot.Mp, snapshot.MaxMp);
        snapshot.TargetHpPercent = Percent(snapshot.TargetHp, snapshot.TargetMaxHp);

        if (includeBuffs)
        {
            snapshot.SelfBuffs = CaptureBuffs(self);
            snapshot.TargetBuffs = CaptureBuffs(targetBattle);
        }

        FillEnemyCounts(snapshot, self, target);
        return snapshot;
    }

    private CombatFrameCacheSample CaptureCacheSample(long nowTick)
    {
        EnsureSession();

        var self = Data.Me.Object as IBattleChara;
        var target = Data.Target.Current;
        var targetBattle = target as IBattleChara;
        var sample = new CombatFrameCacheSample
        {
            SampleIndex = _sampleIndex,
            Tick = nowTick,
            Timestamp = DateTimeOffset.UtcNow,
            SessionId = _sessionId,
            Job = Data.Me.ClassJob,
            Hp = self?.CurrentHp ?? 0,
            MaxHp = self?.MaxHp ?? 0,
            Mp = self?.CurrentMp ?? 0,
            MaxMp = self?.MaxMp ?? 0,
            Level = Data.Me.CurrentLevel,
            InCombat = Data.Combat.InCombat,
            IsMoving = Data.Me.IsMoving,
            IsCasting = self?.IsCasting ?? Data.Combat.IsCasting,
            TotalCastTime = self?.TotalCastTime ?? 0,
            CurrentCastTime = self?.CurrentCastTime ?? 0,
            GcdCooldown = GCDHelper.GetGCDCooldown(),
            GcdDuration = GCDHelper.GetGCDDuration(),
            LastComboSpellId = EventSystem.LastComboSpellId,
            TargetId = target?.EntityID ?? 0,
            TargetName = target?.Name ?? "",
            Distance = target != null ? Data.Me.DistanceToObject2D(target) : 0,
            IsBoss = targetBattle?.MaxHp > 100000,
            IsDead = target?.IsDead ?? false,
            IsTargetable = target?.IsTargetable ?? false,
            TargetHp = targetBattle?.CurrentHp ?? 0,
            TargetMaxHp = targetBattle?.MaxHp ?? 0,
            QtStates = QTHelper.GetAll().ToDictionary(q => q.Id, q => q.Value),
            JobGauge = CaptureJobGauge(self),
            SelfBuffs = CaptureBuffs(self),
            TargetBuffs = CaptureBuffs(targetBattle)
        };
        return sample;
    }

    private List<CombatResolverSnapshot> CaptureResolvers(bool forceInclude = false)
    {
        if (!forceInclude && !PluginConfig.Instance.CombatRecorderIncludeResolverResults) return [];
        return ACRLifecycle.Runner.Debug.Resolvers.Select(r => new CombatResolverSnapshot
        {
            Name = r.Name,
            Mode = r.Mode,
            CheckResult = r.CheckResult,
            PassedWindow = r.PassedWindow
        }).ToList();
    }

    private static CombatRunnerDebugSnapshot CaptureRunnerDebug()
    {
        var debug = ACRLifecycle.Runner.Debug;
        return new CombatRunnerDebugSnapshot
        {
            Phase = debug.Phase,
            SlotSource = debug.SlotSource,
            CanGcd = debug.CanGcd,
            CanOgcd = debug.CanOgcd,
            HasNextSlot = debug.HasNextSlot,
            HasWaitGcdSlot = debug.HasWaitGcdSlot,
            HasCurrSlot = debug.HasCurrSlot
        };
    }

    private static List<CombatTrackedSkillSnapshot> CaptureTrackedSkills()
    {
        var actionIds = ResolveTrackedSkillIds(
            PluginConfig.Instance.CombatRecorderTrackedSkillsByJob,
            Data.Me.ClassJob);
        if (actionIds.Count == 0) return [];

        var result = new List<CombatTrackedSkillSnapshot>(actionIds.Count);
        foreach (var actionId in actionIds)
        {
            if (actionId == 0) continue;
            result.Add(new CombatTrackedSkillSnapshot
            {
                ActionId = actionId,
                ActionName = GetActionName(actionId),
                CooldownRemainingMs = SpellHelper.GetCooldownRemaining(actionId),
                Charges = SpellHelper.GetCharges(actionId),
                MaxCharges = SpellHelper.GetMaxCharges(actionId)
            });
        }

        return result;
    }

    private static List<CombatBuffSnapshot> CaptureBuffs(IBattleChara? chara)
    {
        if (chara == null) return [];

        var result = new List<CombatBuffSnapshot>(chara.StatusList.Length);
        foreach (var status in chara.StatusList)
        {
            if (status.StatusID == 0) continue;
            var name = "";
            try { name = status.GameData.Value.Name.ToString(); }
            catch { }
            result.Add(new CombatBuffSnapshot
            {
                Id = status.StatusID,
                Name = name,
                Stack = status.Param,
                RemainMs = Math.Max(0, (int)(status.RemainingTime * 1000f))
            });
        }
        return result;
    }

    private static Dictionary<string, object?> CaptureJobGauge(IBattleChara? self)
    {
        return CaptureJobGaugeByJob(Data.Me.ClassJob, self);
    }

    private static ushort GetStack(IBattleChara self, uint statusId)
    {
        return self.StatusList.TryGetStatus(statusId, out var status, out _) ? status?.Param ?? (ushort)1 : (ushort)0;
    }

    private static bool TryGetRemainMs(IBattleChara self, uint statusId, out int remainMs)
    {
        if (self.StatusList.TryGetStatus(statusId, out var status, out _) && status != null)
        {
            remainMs = Math.Max(0, (int)(status.RemainingTime * 1000f));
            return true;
        }
        remainMs = 0;
        return false;
    }

    private static void FillEnemyCounts(CombatFrameSnapshot snapshot, IBattleChara? self, IGameObject? target)
    {
        foreach (var enemy in Data.Objects.Enemies)
        {
            if (enemy is not IBattleChara bc || bc.IsDead || !bc.IsTargetable) continue;

            if (self != null)
            {
                var distSelf = Data.Me.DistanceToObject2D(bc);
                if (distSelf <= 5) snapshot.EnemyCountNearSelf5++;
                if (distSelf <= 10) snapshot.EnemyCountNearSelf10++;
                if (distSelf <= 15) snapshot.EnemyCountNearSelf15++;
                if (distSelf <= 25) snapshot.EnemyCountNearSelf25++;
            }

            if (target != null)
            {
                var distTarget = Distance2D(target, bc);
                if (distTarget <= 5) snapshot.EnemyCountNearTarget5++;
                if (distTarget <= 10) snapshot.EnemyCountNearTarget10++;
                if (distTarget <= 15) snapshot.EnemyCountNearTarget15++;
                if (distTarget <= 25) snapshot.EnemyCountNearTarget25++;
            }
        }
    }

    private static float Distance2D(IGameObject a, IGameObject b)
    {
        var dx = a.Position.X - b.Position.X;
        var dz = a.Position.Z - b.Position.Z;
        return Math.Max(0, MathF.Sqrt(dx * dx + dz * dz) - a.HitboxRadius - b.HitboxRadius);
    }

    private void WriteSamples(IReadOnlyList<CombatFrameSnapshot> samples)
    {
        if (samples.Count == 0) return;

        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_sessionPath)!);
            using var writer = new StreamWriter(_sessionPath, append: true);
            foreach (var sample in samples)
            {
                writer.WriteLine(JsonSerializer.Serialize(sample, JsonOptions));
            }
        }
    }

    private void ProcessPendingEvent(long nowTick)
    {
        if (_pendingEvents.Count == 0) return;

        var afterCache = CaptureCacheSample(nowTick);
        while (_pendingEvents.Count > 0)
        {
            var pending = _pendingEvents.Dequeue();
            var current = CaptureSnapshot(
                pending.EventType,
                pending.ActionId,
                pending.Success,
                pending.Cancelled,
                pending.GcdKind);

            var beforeSample = SelectDiffBaseSample(_frames.GetWindowSnapshot(), afterCache, pending.EventTick);
            var before = beforeSample == null ? null : CombatRecorderEventWriter.CreateFromCacheSample(beforeSample);
            var eventGroupId = $"{_sessionId}-{Interlocked.Increment(ref _sampleIndex):D8}";
            var line = CombatRecorderEventWriter.BuildEventSample(before, current, eventGroupId);

            if (pending.EventType == CombatRecorderEventType.StallDetected)
            {
                line.RunnerDebug = CaptureRunnerDebug();
                line.ResolverResults = CaptureResolvers(forceInclude: true);
            }

            WriteSamples([line]);
        }
    }

    private void TryEmitStallEvent(long nowTick)
    {
        var gcdCooldown = GCDHelper.GetGCDCooldown();
        if (gcdCooldown != _lastObservedGcdCooldown)
        {
            _lastObservedGcdCooldown = gcdCooldown;
            _lastGcdCooldownChangedAt = nowTick;
            return;
        }

        var unchangedForMs = _lastGcdCooldownChangedAt == 0 ? 0 : nowTick - _lastGcdCooldownChangedAt;
        if (!ShouldEmitStallEvent(
                inCombat: Data.Combat.InCombat,
                runtimeRunning: RuntimeCore.IsRunning,
                acrAvailable: ACRLifecycle.CurrentEntry != null,
                isPaused: MainControlHelper.IsPaused,
                gcdCooldown: gcdCooldown,
                lastObservedGcdCooldown: _lastObservedGcdCooldown,
                unchangedForMs: unchangedForMs,
                lastReportedAt: _lastStallReportedAt,
                nowTicks: nowTick))
        {
            return;
        }

        _lastStallReportedAt = nowTick;
        _pendingEvents.Enqueue(new PendingCombatRecorderEvent(
            CombatRecorderEventType.StallDetected,
            ActionId: 0,
            Success: null,
            Cancelled: false,
            GcdKind: CombatRecorderGcdKind.Unknown,
            EventTick: nowTick));
    }

    private void EnsureSession()
    {
        if (!string.IsNullOrEmpty(_sessionId) && !string.IsNullOrEmpty(_sessionPath))
            return;

        var rotated = RotateSession(_sessionId, DateTime.Now, Guid.NewGuid().ToString("N")[..8]);
        _sessionId = rotated.SessionId;
        _sessionPath = BuildSessionPath(_sessionId);
        _sampleIndex = rotated.SampleIndex;
        _frameIndex = rotated.FrameIndex;
    }

    private bool IsEnabledForCurrentState()
    {
        var cfg = PluginConfig.Instance;
        if (!cfg.CombatRecorderEnabled) return false;
        if (!HiAuRo.Data.IsReady) return false;
        if (cfg.CombatRecorderOnlyInCombat && !Data.Combat.InCombat) return false;
        if (cfg.CombatRecorderOnlyCurrentJob
            && ACRLifecycle.CurrentJobId != 0
            && Data.Me.ClassJob != ACRLifecycle.CurrentJobId)
            return false;
        return Data.Me.Object != null;
    }

    private void ApplyManualSourcePause()
    {
        var shouldPause = PluginConfig.Instance.CombatRecorderEnabled
            && PluginConfig.Instance.CombatRecorderSourceMode == CombatRecorderSourceMode.Manual;

        if (!shouldPause)
        {
            if (_lastManualPauseApplied && MainControlHelper.IsPaused)
                MainControlHelper.Unpause();
            _lastManualPauseApplied = false;
            return;
        }

        if (!MainControlHelper.IsPaused)
        {
            MainControlHelper.Pause();
            _lastManualPauseApplied = true;
        }
    }

    private static string GetConfiguredSource()
    {
        return PluginConfig.Instance.CombatRecorderSourceMode switch
        {
            CombatRecorderSourceMode.Manual => "manual",
            CombatRecorderSourceMode.Acr => "acr",
            _ => "unknown"
        };
    }

    private static bool IsSelfSource(uint sourceId) => sourceId == (Data.Me.Object?.EntityID ?? 0);

    internal static bool ShouldCaptureEventForTests(
        CombatRecorderEventType eventType,
        bool isAbilityAction,
        bool gcdJustActivated)
        => ShouldCaptureEvent(eventType, isAbilityAction, gcdJustActivated);

    internal static bool ShouldCaptureGcdEffectForTests(
        uint actionId,
        uint pendingCastGcdActionId,
        long pendingCastRecordedAtTicks,
        long nowTicks)
        => ShouldCaptureGcdEffect(actionId, pendingCastGcdActionId, pendingCastRecordedAtTicks, nowTicks);

    internal static bool ShouldCaptureActionEffectForTests(uint actionId)
        => ShouldCaptureActionEffect(actionId);

    internal static CombatRecorderGcdKind ResolveGcdKindForTests(
        CombatRecorderEventType eventType,
        uint pendingCastGcdActionId,
        uint actionId)
        => ResolveGcdKind(eventType, pendingCastGcdActionId, actionId);

    internal static CombatRecorderSessionState RotateSessionForTests(
        string existingSessionId,
        long existingFrameIndex,
        long existingSampleIndex,
        DateTime now,
        string suffix)
        => RotateSession(existingSessionId, now, suffix);

    internal static CombatFrameCacheSample? SelectDiffBaseSampleForTests(
        IReadOnlyList<CombatFrameCacheSample> window,
        CombatFrameCacheSample after,
        long eventTick)
        => SelectDiffBaseSample(window, after, eventTick);

    internal static bool ShouldEmitStallEventForTests(
        bool inCombat,
        bool runtimeRunning,
        bool acrAvailable,
        bool isPaused,
        float gcdCooldown,
        float lastObservedGcdCooldown,
        long unchangedForMs,
        long lastReportedAt,
        long nowTicks)
        => ShouldEmitStallEvent(inCombat, runtimeRunning, acrAvailable, isPaused, gcdCooldown, lastObservedGcdCooldown, unchangedForMs, lastReportedAt, nowTicks);

    public static string ResolveLogRootForTests(string configuredPath, string fallbackRoot)
        => ResolveLogRoot(configuredPath, fallbackRoot);

    public static IReadOnlyList<uint> ResolveTrackedSkillIdsForTests(
        Dictionary<uint, List<uint>> byJob,
        uint currentJob)
        => ResolveTrackedSkillIds(byJob, currentJob);

    public static Dictionary<string, object?> ExportGaugeObjectForTests(object gauge)
        => ExportGaugeObject(gauge);

    private static bool ShouldCaptureEvent(
        CombatRecorderEventType eventType,
        bool isAbilityAction,
        bool gcdJustActivated)
    {
        return eventType switch
        {
            CombatRecorderEventType.UseActionSuccess => false,
            CombatRecorderEventType.AbilityEffect => isAbilityAction,
            CombatRecorderEventType.GcdReadyAndAction => !isAbilityAction && gcdJustActivated,
            _ => false
        };
    }

    private static bool ShouldCaptureActionEffect(uint actionId)
        => actionId != 7;

    private static CombatFrameCacheSample? SelectDiffBaseSample(
        IReadOnlyList<CombatFrameCacheSample> window,
        CombatFrameCacheSample after,
        long eventTick)
    {
        for (var i = window.Count - 1; i >= 0; i--)
        {
            var sample = window[i];
            if (sample.Tick > eventTick) continue;
            if (!HasMeaningfulDiff(sample, after)) continue;
            return sample;
        }

        return window.LastOrDefault(sample => sample.Tick <= eventTick);
    }

    private static bool HasMeaningfulDiff(CombatFrameCacheSample before, CombatFrameCacheSample after)
    {
        if (before.Hp != after.Hp || before.Mp != after.Mp)
            return true;
        if (before.IsMoving != after.IsMoving || before.IsCasting != after.IsCasting)
            return true;
        if (before.GcdCooldown != after.GcdCooldown || before.LastComboSpellId != after.LastComboSpellId)
            return true;
        if (before.TargetHp != after.TargetHp || before.TargetId != after.TargetId)
            return true;
        if (!before.QtStates.OrderBy(x => x.Key).SequenceEqual(after.QtStates.OrderBy(x => x.Key)))
            return true;
        if (!before.JobGauge.OrderBy(x => x.Key).SequenceEqual(after.JobGauge.OrderBy(x => x.Key)))
            return true;
        if (before.SelfBuffs.Count != after.SelfBuffs.Count || before.TargetBuffs.Count != after.TargetBuffs.Count)
            return true;
        return false;
    }

    private static bool ShouldEmitStallEvent(
        bool inCombat,
        bool runtimeRunning,
        bool acrAvailable,
        bool isPaused,
        float gcdCooldown,
        float lastObservedGcdCooldown,
        long unchangedForMs,
        long lastReportedAt,
        long nowTicks)
    {
        if (!inCombat || !runtimeRunning || !acrAvailable || isPaused)
            return false;
        if (gcdCooldown <= 0 || gcdCooldown != lastObservedGcdCooldown)
            return false;
        if (unchangedForMs < 1000)
            return false;
        if (lastReportedAt != 0 && nowTicks - lastReportedAt < 1000)
            return false;
        return true;
    }

    private static CombatRecorderGcdKind ResolveGcdKind(
        CombatRecorderEventType eventType,
        uint pendingCastGcdActionId,
        uint actionId)
    {
        if (eventType != CombatRecorderEventType.GcdReadyAndAction || actionId == 0)
            return CombatRecorderGcdKind.Unknown;

        return pendingCastGcdActionId == actionId
            ? CombatRecorderGcdKind.Casted
            : CombatRecorderGcdKind.Instant;
    }

    private static CombatRecorderSessionState RotateSession(string existingSessionId, DateTime now, string suffix)
    {
        return new CombatRecorderSessionState(
            $"{now:yyyyMMdd-HHmmss}-{suffix}",
            FrameIndex: 0,
            SampleIndex: 0);
    }

    private static string ResolveLogRoot(string configuredPath, string fallbackRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return configuredPath;

        return Path.Combine(fallbackRoot, "Recordings", "CombatRecorder");
    }

    private static Dictionary<string, object?> CaptureJobGaugeByJob(uint jobId, IBattleChara? self)
    {
        if (self == null) return [];
        var gauge = TryCaptureGaugeFromHelper(jobId);
        return gauge ?? [];
    }

    private static IReadOnlyList<uint> ResolveTrackedSkillIds(Dictionary<uint, List<uint>> byJob, uint currentJob)
    {
        if (byJob.TryGetValue(currentJob, out var ids))
            return ids;
        return [];
    }

    private static Dictionary<string, object?>? TryCaptureGaugeFromHelper(uint jobId)
    {
        try
        {
            var helperType = ResolveHelperType(jobId);
            if (helperType == null) return null;

            var gaugeProperty = helperType.GetProperty("Gauge", BindingFlags.Public | BindingFlags.Static)
                ?? helperType.GetProperty("量谱", BindingFlags.Public | BindingFlags.Static)
                ?? helperType.GetProperties(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(p => p.PropertyType.Name.EndsWith("Gauge", StringComparison.Ordinal));
            if (gaugeProperty == null) return null;

            var gauge = gaugeProperty.GetValue(null);
            if (gauge == null) return null;

            return ExportGaugeObject(gauge);
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Debug($"[CombatRecorder] JobGauge 读取失败(job={jobId}): {ex.Message}");
            return null;
        }
    }

    private static Type? ResolveHelperType(uint jobId)
    {
        var helperTypeName = jobId switch
        {
            (uint)Jobs.AST => "HiAuRo.Helper.ASTHelper",
            (uint)Jobs.BLM => "HiAuRo.Helper.BLMHelper",
            (uint)Jobs.BRD => "HiAuRo.Helper.BRDHelper",
            (uint)Jobs.DNC => "HiAuRo.Helper.DNCHelper",
            (uint)Jobs.DRG => "HiAuRo.Helper.DRGHelper",
            (uint)Jobs.DRK => "HiAuRo.Helper.DRKHelper",
            (uint)Jobs.GNB => "HiAuRo.Helper.GNBHelper",
            (uint)Jobs.MCH => "HiAuRo.Helper.MCHHelper",
            (uint)Jobs.MNK => "HiAuRo.Helper.MNKHelper",
            (uint)Jobs.NIN => "HiAuRo.Helper.NINHelper",
            (uint)Jobs.PCT => "HiAuRo.Helper.PCTHelper",
            (uint)Jobs.PLD => "HiAuRo.Helper.PLDHelper",
            (uint)Jobs.RDM => "HiAuRo.Helper.RDMHelper",
            (uint)Jobs.RPR => "HiAuRo.Helper.RPRHelper",
            (uint)Jobs.SAM => "HiAuRo.Helper.SAMHelper",
            (uint)Jobs.SCH => "HiAuRo.Helper.SCHHelper",
            (uint)Jobs.SGE => "HiAuRo.Helper.SGEHelper",
            (uint)Jobs.SMN => "HiAuRo.Helper.SMNHelper",
            (uint)Jobs.VPR => "HiAuRo.Helper.VPRHelper",
            (uint)Jobs.WAR => "HiAuRo.Helper.WARHelper",
            (uint)Jobs.WHM => "HiAuRo.Helper.WHMHelper",
            _ => null
        };

        if (helperTypeName == null) return null;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType(helperTypeName, throwOnError: false, ignoreCase: false);
            if (type != null) return type;
        }

        return null;
    }

    private static Dictionary<string, object?> ExportGaugeObject(object gauge)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in gauge.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
                continue;

            var value = prop.GetValue(gauge);
            if (value == null)
                continue;

            var type = value.GetType();
            if (!IsGaugeScalar(type))
                continue;

            result[ToCamelCase(prop.Name)] = value;
        }

        return result;
    }

    private static bool IsGaugeScalar(Type type)
    {
        var actual = Nullable.GetUnderlyingType(type) ?? type;
        return actual.IsEnum
            || actual == typeof(bool)
            || actual == typeof(byte)
            || actual == typeof(sbyte)
            || actual == typeof(short)
            || actual == typeof(ushort)
            || actual == typeof(int)
            || actual == typeof(uint)
            || actual == typeof(long)
            || actual == typeof(ulong)
            || actual == typeof(float)
            || actual == typeof(double)
            || actual == typeof(string);
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0]))
            return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private string BuildSessionPath(string sessionId)
    {
        var job = ((Jobs)Data.Me.ClassJob).ToString();
        var dir = Path.Combine(LogRoot, job, DateTime.Now.ToString("yyyy-MM-dd"));
        return Path.Combine(dir, $"session-{sessionId}.jsonl");
    }

    private static bool ShouldCaptureGcdEffect(
        uint actionId,
        uint pendingCastGcdActionId,
        long pendingCastRecordedAtTicks,
        long nowTicks)
        => true;

    private void ResetTransientState()
    {
        _pendingCastGcdActionId = 0;
        _pendingCastGcdRecordedAt = 0;
        _lastEffectActionId = 0;
        _lastEffectAt = 0;
        _pendingEvents.Clear();
        _lastObservedGcdCooldown = -1;
        _lastGcdCooldownChangedAt = 0;
        _lastStallReportedAt = 0;
    }

    private bool IsDuplicateEffect(uint actionId)
    {
        var now = Environment.TickCount64;
        if (_lastEffectActionId == actionId && now - _lastEffectAt < 200)
            return true;
        _lastEffectActionId = actionId;
        _lastEffectAt = now;
        return false;
    }

    private static float Percent(uint value, uint max) => max > 0 ? (float)value / max : 0f;

    private static unsafe uint ReadQueuedActionId()
    {
        var am = ActionManager.Instance();
        return am == null ? 0 : am->QueuedActionId;
    }

    private static string GetActionName(uint actionId)
    {
        if (actionId == 0) return "";
        return OmenTools.Interop.Game.Lumina.LuminaWrapper.GetActionName(actionId);
    }

    private static unsafe bool IsAbilityAction(uint actionId)
    {
        if (actionId == 0) return false;

        var row = SpellHelper.GetActionRow(actionId);
        if (row.HasValue)
        {
            var cat = row.Value.ActionCategory.RowId;
            if (cat == 4) return true;
            if (cat is 2 or 3) return false;
        }

        var am = ActionManager.Instance();
        if (am == null) return false;
        var gcdGroup = am->GetRecastGroup((int)ActionTypeFF.Action, 9);
        var actionGroup = am->GetRecastGroup((int)ActionTypeFF.Action, actionId);
        return actionGroup != 0 && actionGroup != gcdGroup;
    }
}

internal readonly record struct CombatRecorderSessionState(string SessionId, long FrameIndex, long SampleIndex);

internal sealed record PendingCombatRecorderEvent(
    CombatRecorderEventType EventType,
    uint ActionId,
    bool? Success,
    bool Cancelled,
    CombatRecorderGcdKind GcdKind,
    long EventTick);
