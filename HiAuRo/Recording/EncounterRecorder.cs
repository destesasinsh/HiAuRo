using System.Text.Json;
using HiAuRo.ACR;
using HiAuRo.Execution.Events;
using HiAuRo.Runtime;
using OmenTools.OmenService;
using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;

namespace HiAuRo.Recording;

/// <summary>副本录制器 — 录制战斗事件用于调试/可视化回放</summary>
public sealed class EncounterRecorder
{
    /// <summary>录制器单例</summary>
    public static EncounterRecorder Instance { get; } = new();

    private readonly CombatClock _clock = new();
    private readonly Dictionary<Type, Func<ITriggerCondParams, long, EncounterEvent>> _serializers = [];
    private readonly HashSet<uint> _bossNpcIds = [];
    private readonly object _lock = new();
    private EncounterRecord? _current;
    private bool _initialized;
    private string _saveDir = "";

    private EncounterRecorder() { }

    #region 生命周期

    /// <summary>初始化录制器</summary>
    public void Init()
    {
        if (_initialized) return;
        _initialized = true;

        _saveDir = Path.Combine(
            DService.Instance().PI.ConfigDirectory.FullName, "Recordings");
        Directory.CreateDirectory(_saveDir);

        GameEventHook.Instance.OnEventFired += OnGameEvent;
        CombatContext.StateChanged += OnCombatStateChanged;
        RegisterSerializers();

        _bossNpcIds.Clear();
    }

    /// <summary>关闭录制器</summary>
    public void Shutdown()
    {
        if (!_initialized) return;
        _initialized = false;

        GameEventHook.Instance.OnEventFired -= OnGameEvent;
        CombatContext.StateChanged -= OnCombatStateChanged;

        bool hasPending;
        lock (_lock)
        {
            hasPending = _current != null && _current.Events.Count > 0;
        }
        if (hasPending)
            SaveRecord();

        _serializers.Clear();
        lock (_lock) { _bossNpcIds.Clear(); }
    }

    #endregion

    #region 战斗状态回调

    private void OnCombatStateChanged(CombatContext.State oldState, CombatContext.State newState)
    {
        if (newState == CombatContext.State.InCombat)
        {
            if (!PluginConfig.Instance.RecordingEnabled) return;
            StartRecording();
        }
        else if (newState == CombatContext.State.OutOfCombat)
        {
            bool shouldSave;
            lock (_lock) { shouldSave = _current != null; }
            if (shouldSave)
            {
                _clock.Pause();
                SaveRecord();
            }
        }
    }

    private void StartRecording()
    {
        _clock.Reset();

        var rec = new EncounterRecord
        {
            TerritoryId = GameState.TerritoryType,
            TerritoryName = GetTerritoryName(),
            RecordedAt = DateTime.UtcNow.ToString("O"),
            Events = []
        };

        lock (_lock) { _current = rec; }

        // 采集队伍信息
        try
        {
            var pt = DService.Instance().PartyList;
            if (pt != null)
            {
                rec.PartySize = pt.Length;
                var jobs = new List<string>();
                foreach (var member in pt)
                {
                    if (member?.ClassJob != null)
                    {
                        var name = member.ClassJob.ToString();
                        if (!string.IsNullOrEmpty(name))
                            jobs.Add(name);
                    }
                }
                rec.JobComposition = jobs;
            }
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Debug($"[Recording] 队伍信息采集异常: {ex.Message}");
        }
    }

    private string GetTerritoryName()
    {
        try
        {
            var sheet = DService.Instance().Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            if (sheet != null)
            {
                var row = sheet.GetRow(GameState.TerritoryType);
                var placeName = row.PlaceName.Value;
                return placeName.Name.ToString();
            }
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Debug($"[Recording] Territory名称获取异常: {ex.Message}");
        }
        return $"Territory_{GameState.TerritoryType}";
    }

    private void SaveRecord()
    {
        if (!_initialized) return;

        EncounterRecord? record;
        uint[] bossIds;

        lock (_lock)
        {
            if (_current == null) return;

            _current.TotalTimeMs = _clock.Now;
            bossIds = _bossNpcIds.ToArray();
            record = _current;
            _current = null;
            _bossNpcIds.Clear();
        }

        record.Bosses = bossIds.Select(id =>
        {
            var obj = DService.Instance().ObjectTable
                .FirstOrDefault(o => o != null && o.EntityID == id);
            var bn = obj as IBattleNPC;
            return new BossInfo
            {
                NpcId = bn?.DataID ?? 0,
                Name = obj?.Name.ToString() ?? "",
                HpMax = bn?.MaxHp ?? 0
            };
        }).ToList();

        var json = JsonSerializer.Serialize(record,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var safeName = SanitizeFileName(record.TerritoryName) + "_" +
                       DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
        var path = Path.Combine(_saveDir, safeName);

        File.WriteAllText(path, json);
        DService.Instance().Log.Information($"[Recording] 已保存: {path} ({record.Events.Count} 事件)");
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var arr = name.Where(c => !invalid.Contains(c)).ToArray();
        return new string(arr);
    }

    #endregion

    #region Public properties (for ImGui panel)

    /// <summary>是否正在录制</summary>
    public bool IsRecording
    {
        get { lock (_lock) { return _current != null; } }
    }

    /// <summary>已录制秒数</summary>
    public int ElapsedSeconds => (int)(_clock.Now / 1000);

    /// <summary>当前录制文件名</summary>
    public string CurrentFileName
    {
        get
        {
            lock (_lock)
            {
                if (_current == null) return "";
                var safe = SanitizeFileName(_current.TerritoryName) + "_" +
                           DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
                return safe;
            }
        }
    }

    /// <summary>获取录制文件列表</summary>
    public (string Name, string Path)[] GetRecordFiles()
    {
        if (!Directory.Exists(_saveDir)) return [];
        return Directory.GetFiles(_saveDir, "*.json")
            .Select(f => (Path.GetFileName(f), f))
            .OrderByDescending(x => x.Item2)
            .ToArray();
    }

    /// <summary>强制停止录制并保存</summary>
    public void ForceStop()
    {
        bool shouldSave;
        lock (_lock) { shouldSave = _current != null; }
        if (shouldSave)
        {
            _clock.Pause();
            SaveRecord();
        }
    }

    #endregion

    #region 事件序列化

    private void RegisterSerializers()
    {
        // ---- cast ----
        _serializers[typeof(EnemyCastSpellCondParams)] = (p, t) =>
        {
            var cp = (EnemyCastSpellCondParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(EnemyCastSpellCondParams),
                Category = "cast",
                Data = new()
                {
                    ["spellId"] = cp.SpellId,
                    ["castTime"] = cp.TotalCastTimeInSec,
                    ["sourceId"] = cp.SourceID,
                    ["posX"] = cp.CastPos.X,
                    ["posY"] = cp.CastPos.Y,
                    ["posZ"] = cp.CastPos.Z,
                }
            };
        };

        _serializers[typeof(AfterSpellCondParams)] = (p, t) =>
        {
            var ap = (AfterSpellCondParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(AfterSpellCondParams),
                Category = "cast",
                Data = new() { ["spellId"] = ap.SpellId }
            };
        };

        // ---- ability ----
        _serializers[typeof(ReceviceAbilityEffectCondParams)] = (p, t) =>
        {
            var ap = (ReceviceAbilityEffectCondParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(ReceviceAbilityEffectCondParams),
                Category = "ability",
                Data = new()
                {
                    ["actionId"] = ap.ActionId,
                    ["sourceId"] = ap.SourceID,
                    ["targetOid"] = ap.TargetID,
                    ["animationId"] = ap.AnimationID,
                    ["effectType"] = ap.EffectType,
                }
            };
        };

        _serializers[typeof(ReceviceNoTargetAbilityEffectCondParams)] = (p, t) =>
        {
            var np = (ReceviceNoTargetAbilityEffectCondParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(ReceviceNoTargetAbilityEffectCondParams),
                Category = "ability",
                Data = new()
                {
                    ["sourceId"] = np.SourceID,
                    ["actionId"] = np.ActionId,
                    ["posX"] = np.pos.X,
                    ["posY"] = np.pos.Y,
                    ["posZ"] = np.pos.Z,
                }
            };
        };

        // ---- buff ----
        _serializers[typeof(AddStatusCondParams)] = (p, t) =>
        {
            var bp = (AddStatusCondParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(AddStatusCondParams),
                Category = "buff",
                Data = new()
                {
                    ["sourceId"] = bp.SourceID,
                    ["targetId"] = bp.TargetID,
                    ["statusId"] = bp.StatusId,
                    ["count"] = bp.count,
                    ["param"] = bp.Param,
                    ["remainingTime"] = bp.RemainingTime,
                }
            };
        };

        _serializers[typeof(RemoveStatusCondParams)] = (p, t) =>
        {
            var bp = (RemoveStatusCondParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(RemoveStatusCondParams),
                Category = "buff",
                Data = new()
                {
                    ["targetId"] = bp.TargetID,
                    ["statusId"] = bp.StatusId,
                }
            };
        };

        // ---- tether ----
        _serializers[typeof(TetherCreateParams)] = (p, t) =>
        {
            var tp = (TetherCreateParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(TetherCreateParams),
                Category = "tether",
                Data = new()
                {
                    ["tetherId"] = tp.TetherID,
                    ["sourceId"] = tp.SourceID,
                    ["targetOid"] = tp.TargetID,
                    ["param2"] = tp.Param2,
                    ["param3"] = tp.Param3,
                    ["param5"] = tp.Param5,
                }
            };
        };

        _serializers[typeof(TetherSnapshotParams)] = (p, t) =>
        {
            var tp = (TetherSnapshotParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(TetherSnapshotParams),
                Category = "tether",
                Data = new()
                {
                    ["tetherId"] = tp.TetherID,
                    ["sourceId"] = tp.SourceID,
                    ["targetOid"] = tp.TargetID,
                    ["slotIndex"] = tp.SlotIndex,
                    ["progress"] = tp.Progress,
                    ["isActive"] = tp.IsActive,
                }
            };
        };

        _serializers[typeof(TetherRemoveParams)] = (p, t) =>
        {
            var tp = (TetherRemoveParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(TetherRemoveParams),
                Category = "tether",
                Data = new()
                {
                    ["sourceId"] = tp.SourceID,
                    ["param2"] = tp.Param2,
                    ["param3"] = tp.Param3,
                    ["param5"] = tp.Param5,
                }
            };
        };

        // ---- spawn ----
        _serializers[typeof(UnitCreateCondParams)] = (p, t) =>
        {
            var up = (UnitCreateCondParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(UnitCreateCondParams),
                Category = "spawn",
                Data = new()
                {
                    ["entityId"] = up.EntityId,
                    ["dataId"] = up.DataId,
                    ["name"] = up.Name,
                    ["position"] = new[] { up.Position.X, up.Position.Y, up.Position.Z },
                }
            };
        };

        _serializers[typeof(UnitDeleteCondParams)] = (p, t) =>
        {
            var up = (UnitDeleteCondParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(UnitDeleteCondParams),
                Category = "spawn",
                Data = new()
                {
                    ["entityId"] = up.EntityId,
                    ["dataId"] = up.DataId,
                    ["name"] = up.Name,
                }
            };
        };

        // ---- death ----
        _serializers[typeof(ActorDeathParams)] = (p, t) =>
        {
            var dp = (ActorDeathParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(ActorDeathParams),
                Category = "death",
                Data = new()
                {
                    ["sourceId"] = dp.SourceID,
                    ["targetId"] = dp.TargetID,
                    ["dataId"] = dp.DataId,
                    ["name"] = dp.Name,
                }
            };
        };

        // ---- target ----
        _serializers[typeof(TargetIconEffectTestCondParams)] = (p, t) =>
        {
            var tp = (TargetIconEffectTestCondParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(TargetIconEffectTestCondParams),
                Category = "target",
                Data = new()
                {
                    ["sourceId"] = tp.SourceID,
                    ["targetId"] = tp.TargetID,
                    ["iconId"] = tp.IconId,
                }
            };
        };

        _serializers[typeof(TargetAbleCondParams)] = (p, t) =>
        {
            var tp = (TargetAbleCondParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(TargetAbleCondParams),
                Category = "target",
                Data = new()
                {
                    ["sourceId"] = tp.SourceID,
                    ["targetId"] = tp.TargetID,
                    ["isTargetable"] = tp.IsTargetable,
                }
            };
        };

        // ---- combat ----
        _serializers[typeof(CombatStateParams)] = (p, t) =>
        {
            var cp = (CombatStateParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(CombatStateParams),
                Category = "combat",
                Data = new() { ["isEntering"] = cp.IsEntering }
            };
        };

        _serializers[typeof(ActorControlCombatParams)] = (p, t) =>
        {
            var cp = (ActorControlCombatParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(ActorControlCombatParams),
                Category = "combat",
                Data = new() { ["isEntering"] = cp.IsEntering }
            };
        };

        // ---- environment ----
        _serializers[typeof(OnMapEffectCreateEvent)] = (p, t) =>
        {
            var mp = (OnMapEffectCreateEvent)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(OnMapEffectCreateEvent),
                Category = "environment",
                Data = new()
                {
                    ["pos"] = mp.Pos,
                    ["arg0"] = mp.arg0,
                    ["arg1"] = mp.arg1,
                }
            };
        };

        _serializers[typeof(OnEnvControlEvent)] = (p, t) =>
        {
            var ep = (OnEnvControlEvent)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(OnEnvControlEvent),
                Category = "environment",
                Data = new()
                {
                    ["index"] = ep.Index,
                    ["flag"] = ep.Flag,
                }
            };
        };

        _serializers[typeof(WeatherChangedCondParams)] = (p, t) =>
        {
            var wp = (WeatherChangedCondParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(WeatherChangedCondParams),
                Category = "environment",
                Data = new() { ["newWeatherId"] = wp.NewWeatherId }
            };
        };

        // ---- npc ----
        _serializers[typeof(NpcYellCondParams)] = (p, t) =>
        {
            var np = (NpcYellCondParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(NpcYellCondParams),
                Category = "npc",
                Data = new()
                {
                    ["sourceId"] = np.sourceId,
                    ["sourceName"] = np.sourceName,
                    ["yellId"] = np.yellId,
                    ["yellMsg"] = np.yellMsg,
                }
            };
        };

        // ---- director ----
        _serializers[typeof(DirectorUpdateParams)] = (p, t) =>
        {
            var dp = (DirectorUpdateParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(DirectorUpdateParams),
                Category = "director",
                Data = new()
                {
                    ["category"] = dp.Category.ToString(),
                    ["param1"] = dp.Param1,
                    ["param2"] = dp.Param2,
                    ["param3"] = dp.Param3,
                    ["param4"] = dp.Param4,
                    ["a6"] = dp.A6,
                    ["a7"] = dp.A7,
                    ["a8"] = dp.A8,
                    ["a9"] = dp.A9,
                }
            };
        };

        _serializers[typeof(PlayActionTimelineParams)] = (p, t) =>
        {
            var tp = (PlayActionTimelineParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(PlayActionTimelineParams),
                Category = "director",
                Data = new()
                {
                    ["sourceId"] = tp.SourceID,
                    ["id"] = tp.Id,
                }
            };
        };

        // ---- actorControl ----
        _serializers[typeof(ActorControlParams)] = (p, t) =>
        {
            var ap = (ActorControlParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(ActorControlParams),
                Category = "actorControl",
                Data = new()
                {
                    ["sourceId"] = ap.SourceID,
                    ["command"] = ap.Command,
                    ["p1"] = ap.P1,
                    ["p2"] = ap.P2,
                    ["p3"] = ap.P3,
                    ["p4"] = ap.P4,
                    ["p5"] = ap.P5,
                    ["p6"] = ap.P6,
                    ["targetId"] = ap.TargetID,
                }
            };
        };

        // ---- chat ----
        _serializers[typeof(GameLogCondParams)] = (p, t) =>
        {
            var cp = (GameLogCondParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(GameLogCondParams),
                Category = "chat",
                Data = new() { ["log"] = cp.Log }
            };
        };

        /*_serializers[typeof(ObjectChangeParams)] = (p, t) =>
        {
            var op = (ObjectChangeParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(ObjectChangeParams),
                Category = "actorControl",
                Data = new()
                {
                    ["sourceId"] = op.SourceID,
                    ["targetId"] = op.TargetID,
                    ["command"]  = op.Command,
                    ["p1"] = op.P1, ["p2"] = op.P2, ["p3"] = op.P3, ["p4"] = op.P4
                }
            };
        };*/

        _serializers[typeof(ObjectEffectParams)] = (p, t) =>
        {
            var op = (ObjectEffectParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(ObjectEffectParams),
                Category = "environment",
                Data = new()
                {
                    ["objectId"] = op.ObjectID,
                    ["data1"]    = op.Data1,
                    ["data2"]    = op.Data2
                }
            };
        };

        _serializers[typeof(ObjectSpawnParams)] = (p, t) =>
        {
            var sp = (ObjectSpawnParams)p;
            return new EncounterEvent
            {
                TimeMs = t,
                Type = nameof(ObjectSpawnParams),
                Category = "spawn",
                Data = new()
                {
                    ["entityId"] = sp.EntityId,
                    ["dataId"]   = sp.DataId,
                    ["layoutId"] = sp.LayoutId,
                }
            };
        };
    }

    private void OnGameEvent(ITriggerCondParams condParams)
    {
        lock (_lock)
        {
            if (_current == null) return;

            // 尝试识别 Boss NPC (来自 cast 事件的高HP敌人)
            if (condParams is EnemyCastSpellCondParams acp && acp.SourceID != 0)
            {
                try
                {
                    var obj = DService.Instance().ObjectTable.SearchByID(acp.SourceID);
                    if (obj is IBattleNPC bn && bn.MaxHp > 1000)
                    {
                        _bossNpcIds.Add(acp.SourceID);
                    }
                }
                catch (Exception ex)
                {
                    DService.Instance().Log.Debug($"[Recording] Boss检测异常: {ex.Message}");
                }
            }

            var type = condParams.GetType();
            if (!_serializers.TryGetValue(type, out var serializer)) return;

            try
            {
                var evt = serializer(condParams, _clock.Now);
                _current.Events.Add(evt);
            }
            catch (Exception ex)
            {
                DService.Instance().Log.Warning($"[Recording] 序列化失败 {type.Name}: {ex}");
            }
        }
    }

    #endregion
}
