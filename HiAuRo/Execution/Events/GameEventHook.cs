using System.Numerics;
using HiAuRo.ACR;
using static HiAuRo.Data;
using HiAuRo.Infrastructure;
using Dalamud.Game.Text;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;
using OmenTools.OmenService;
using IFramework = Dalamud.Plugin.Services.IFramework;

namespace HiAuRo.Execution.Events;

/// <summary>游戏事件钩子 — 通过 Hook + 回调监听游戏内事件并分发到执行轴</summary>
public sealed class GameEventHook
{
    /// <summary>单例</summary>
    public static GameEventHook Instance { get; } = new();
    /// <summary>事件触发回调</summary>
    public event Action<ITriggerCondParams>? OnEventFired;

    internal void Fire(ITriggerCondParams p)
    {
        LogManager.Instance.Log(p);
        OnEventFired?.Invoke(p);
    }

    private bool _initialized;

    private delegate void ActionEffectDelegate(
        uint sourceId, nint sourceCharacter, nint pos,
        nint effectHeader, nint effectArray, nint effectTail);
    private Hook<ActionEffectDelegate>? _actionEffectHook;

    private unsafe delegate void ObjectEffectDelegate(EventObject* eventObject, uint data1, uint data2, ulong a4);
    private Hook<ObjectEffectDelegate>? _objectEffectHook;

    private unsafe delegate void ActorCastDetourDelegate(uint sourceId, ActorCastPacket* packetPtr);
    private Hook<ActorCastDetourDelegate>? _actorCastHook;

    private delegate void ActorControlDelegate(
        uint entityId, uint category,
        uint p1, uint p2, uint p3, uint p4,
        uint p5, uint p6, uint p7, uint p8,
        ulong targetId, byte replaying);
    private Hook<ActorControlDelegate>? _actorControlHook;

    private delegate void MapEffectDelegate(nint self, uint index, ushort s1, ushort s2);
    private Hook<MapEffectDelegate>? _mapEffectHook;

    private delegate nint NpcYellDelegate(nint packetPtr);
    private Hook<NpcYellDelegate>? _npcYellHook;

    private delegate void SpawnNpcDelegate(uint targetId, nint packetPtr);
    private Hook<SpawnNpcDelegate>? _spawnNpcHook;

    private delegate void SpawnObjectDelegate(uint targetId, nint packetPtr);
    private Hook<SpawnObjectDelegate>? _spawnObjectHook;

    private readonly Dictionary<(uint SourceId, ushort ActionId), long> _actorCastDedup = [];
    private readonly Dictionary<TetherSnapshotKey, TetherSnapshotState> _tetherSnapshots = [];
    private long _nextTetherSnapshotPollAt;

    /// <summary>初始化游戏事件钩子（注册 Hook + 回调）</summary>
    public void Init()
    {
        if (_initialized) return;
        _initialized = true;

        var csm = CharacterStatusManager.Instance();
        csm.RegGain(OnGainStatus);
        csm.RegLose(OnLoseStatus);

        var gpm = GamePacketManager.Instance();
        gpm.RegPostReceivePacket(OnReceivePacket);

        DService.Instance().Chat.ChatMessage += OnChatMessage;

        FrameworkManager.Instance().Reg(OnFrameworkUpdate);

        try
        {
            var sigScanner = DService.Instance().SigScanner;
            var actionEffectAddr = sigScanner.ScanText("40 55 53 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 70 4C 8B BD");
            if (actionEffectAddr != nint.Zero)
            {
                _actionEffectHook = DService.Instance().Hook.HookFromAddress<ActionEffectDelegate>(
                    actionEffectAddr, OnActionEffect);
                _actionEffectHook.Enable();
            }
            else
            {
                DService.Instance().Log.Warning("[GameEventHook] ActionEffect 签名未找到，跳过 Hook 挂载");
            }
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[GameEventHook] ActionEffect Hook 挂载失败 (可能是版本更新): {ex.Message}");
        }

        try
        {
            var sigScanner = DService.Instance().SigScanner;
            var objectEffectAddr = sigScanner.ScanText("4C 8B DC 53 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 49 89 6B F0 48 8B D9 49 89 7B E0");
            if (objectEffectAddr != nint.Zero)
            {
                unsafe
                {
                    _objectEffectHook = DService.Instance().Hook.HookFromAddress<ObjectEffectDelegate>(
                        objectEffectAddr, OnObjectEffect);
                }
                _objectEffectHook.Enable();
            }
            else
            {
                DService.Instance().Log.Warning("[GameEventHook] ObjectEffect 签名未找到，跳过 Hook 挂载");
            }
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[GameEventHook] ObjectEffect Hook 挂载失败 (可能是版本更新): {ex.Message}");
        }

        try
        {
            var sigScanner = DService.Instance().SigScanner;
            var actorCastAddr = sigScanner.ScanText("40 53 57 48 81 EC ?? ?? ?? ?? 48 8B FA 8B D1");
            if (actorCastAddr != nint.Zero)
            {
                unsafe
                {
                    _actorCastHook = DService.Instance().Hook.HookFromAddress<ActorCastDetourDelegate>(
                        actorCastAddr, OnActorCastDetour);
                }
                _actorCastHook.Enable();
            }
            else
            {
                DService.Instance().Log.Warning("[GameEventHook] ActorCast 签名未找到，跳过 Hook 挂载");
            }
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[GameEventHook] ActorCast Hook 挂载失败 (可能是版本更新): {ex.Message}");
        }

        try
        {
            var sigScanner = DService.Instance().SigScanner;
            var actorControlAddr = sigScanner.ScanText("E8 ?? ?? ?? ?? 0F B7 0B 83 E9 64");
            if (actorControlAddr != nint.Zero)
            {
                _actorControlHook = DService.Instance().Hook.HookFromAddress<ActorControlDelegate>(
                    actorControlAddr, OnActorControlDetour);
                _actorControlHook.Enable();
            }
            else
            {
                DService.Instance().Log.Warning("[GameEventHook] ActorControl 签名未找到，跳过 Hook 挂载");
            }
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[GameEventHook] ActorControl Hook 挂载失败 (可能是版本更新): {ex.Message}");
        }

        try
        {
            var sigScanner = DService.Instance().SigScanner;
            var mapEffectAddr = sigScanner.ScanText(
                "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 8B FA 41 0F B7 E8");
            if (mapEffectAddr != nint.Zero)
            {
                _mapEffectHook = DService.Instance().Hook.HookFromAddress<MapEffectDelegate>(
                    mapEffectAddr, OnMapEffectDetour);
                _mapEffectHook.Enable();
            }
            else
            {
                DService.Instance().Log.Warning("[GameEventHook] MapEffect 签名未找到，跳过 Hook 挂载");
            }
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[GameEventHook] MapEffect Hook 挂载失败 (可能是版本更新): {ex.Message}");
        }

        try
        {
            var sigScanner = DService.Instance().SigScanner;
            var npcYellAddr = sigScanner.ScanText("48 83 EC ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? 0F 10 41");
            if (npcYellAddr != nint.Zero)
            {
                _npcYellHook = DService.Instance().Hook.HookFromAddress<NpcYellDelegate>(
                    npcYellAddr, OnNpcYellDetour);
                _npcYellHook.Enable();
            }
            else
            {
                DService.Instance().Log.Warning("[GameEventHook] NpcYell 签名未找到，跳过 Hook 挂载");
            }
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[GameEventHook] NpcYell Hook 挂载失败 (可能是版本更新): {ex.Message}");
        }

        try
        {
            var sigScanner = DService.Instance().SigScanner;
            var spawnNpcAddr = sigScanner.ScanText(
                "48 89 5C 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B DA 8B F9 E8 ?? ?? ?? ?? 3C ?? 75 ?? E8 ?? ?? ?? ?? 3C ?? 75 ?? 80 BB");
            if (spawnNpcAddr != nint.Zero)
            {
                _spawnNpcHook = DService.Instance().Hook.HookFromAddress<SpawnNpcDelegate>(
                    spawnNpcAddr, OnSpawnNpcDetour);
                _spawnNpcHook.Enable();
            }
            else
            {
                DService.Instance().Log.Warning("[GameEventHook] SpawnNpc 签名未找到，跳过 Hook 挂载");
            }
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[GameEventHook] SpawnNpc Hook 挂载失败 (可能是版本更新): {ex.Message}");
        }

        try
        {
            var sigScanner = DService.Instance().SigScanner;
            var spawnObjAddr = sigScanner.ScanText("40 53 57 48 83 EC ?? F6 42");
            if (spawnObjAddr != nint.Zero)
            {
                _spawnObjectHook = DService.Instance().Hook.HookFromAddress<SpawnObjectDelegate>(
                    spawnObjAddr, OnSpawnObjectDetour);
                _spawnObjectHook.Enable();
            }
            else
            {
                DService.Instance().Log.Warning("[GameEventHook] SpawnObject 签名未找到，跳过 Hook 挂载");
            }
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[GameEventHook] SpawnObject Hook 挂载失败 (可能是版本更新): {ex.Message}");
        }
    }

    /// <summary>关闭游戏事件钩子（清理 Hook + 回调）</summary>
    public void Shutdown()
    {
        if (!_initialized) return;
        _initialized = false;

        FrameworkManager.Instance().Unreg(OnFrameworkUpdate);
        _tetherSnapshots.Clear();

        _actionEffectHook?.Disable();
        _actionEffectHook?.Dispose();
        _actionEffectHook = null;

        _objectEffectHook?.Disable();
        _objectEffectHook?.Dispose();
        _objectEffectHook = null;

        _actorCastHook?.Disable();
        _actorCastHook?.Dispose();
        _actorCastHook = null;

        _actorControlHook?.Disable();
        _actorControlHook?.Dispose();
        _actorControlHook = null;

        _mapEffectHook?.Disable();
        _mapEffectHook?.Dispose();
        _mapEffectHook = null;

        _npcYellHook?.Disable();
        _npcYellHook?.Dispose();
        _npcYellHook = null;

        _spawnNpcHook?.Disable();
        _spawnNpcHook?.Dispose();
        _spawnNpcHook = null;

        _spawnObjectHook?.Disable();
        _spawnObjectHook?.Dispose();
        _spawnObjectHook = null;

        var csm = CharacterStatusManager.Instance();
        csm.Unreg(OnGainStatus);
        csm.Unreg(OnLoseStatus);

        var gpm = GamePacketManager.Instance();
        gpm.Unreg(OnReceivePacket);

        DService.Instance().Chat.ChatMessage -= OnChatMessage;
    }

    private void OnGainStatus(IBattleChara player, ushort id, ushort param, ushort stackCount, TimeSpan remainingTime, ulong sourceID)
    {
        var srcObj = DService.Instance().ObjectTable.SearchByID(sourceID);
        if (srcObj is { ObjectKind: ObjectKind.Pc }) return;

        Fire(new AddStatusCondParams
        {
            SourceID = (uint)sourceID,
            TargetID = player.EntityID,
            StatusId = id,
            count = stackCount,
            Param = param,
            RemainingTime = (float)remainingTime.TotalSeconds,
        });
    }

    private void OnLoseStatus(IBattleChara player, ushort id, ushort param, ushort stackCount, ulong sourceID)
    {
        Fire(new RemoveStatusCondParams
        {
            TargetID = player.EntityID,
            StatusId = id
        });
    }

    private void OnReceivePacket(int opcode, nint packet)
    {
    }

    private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
    }

    private void OnActorControlDetour(
        uint entityId, uint category,
        uint p1, uint p2, uint p3, uint p4,
        uint p5, uint p6, uint p7, uint p8,
        ulong targetId, byte replaying)
    {
        _actorControlHook!.Original(entityId, category, p1, p2, p3, p4, p5, p6, p7, p8, targetId, replaying);

        var targetId32 = (uint)(targetId & 0xFFFFFFFF);

        Fire(new ActorControlParams
        {
            SourceID = entityId,
            Command = (ushort)category,
            P1 = p1,
            P2 = p2,
            P3 = p3,
            P4 = p4,
            P5 = p5,
            P6 = p6,
            TargetID = targetId32
        });

        switch (category)
        {
            case 6:
            case 2 when p3 == 2:
                var deathObj = DService.Instance().ObjectTable.SearchByID(entityId);
                Fire(new ActorDeathParams
                {
                    SourceID = entityId,
                    TargetID = targetId32,
                    DataId = (deathObj as IBattleNPC)?.DataID ?? 0,
                    Name = deathObj?.Name?.ToString() ?? ""
                });
                break;
            case 34:
                Fire(new TargetIconEffectTestCondParams
                {
                    SourceID = entityId,
                    TargetID = p2,
                    IconId = p1
                });
                break;
            case 35:
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    BattleEvents.OnTetherAdded(p2, entityId, p3, now);
                    Fire(new TetherCreateParams
                    {
                        TetherID = p2,
                        SourceID = entityId,
                        TargetID = p3
                    });
                    break;
                }
            case 47:
                BattleEvents.RemoveTethers(e => e.TetherId == p2 && e.SourceId == entityId);
                Fire(new TetherRemoveParams
                {
                    SourceID = entityId
                });
                break;
            case 54:
                Fire(new TargetAbleCondParams
                {
                    SourceID = entityId,
                    TargetID = targetId32,
                    IsTargetable = p3 != 0
                });
                break;
            case 109:
                if (p3 == 0x40000001)
                    Fire(new BattleStartCondParams());
                if (p3 == 0x40000003)
                    Fire(new BattleEndCondParams());
                break;
            case 407:
                Fire(new PlayActionTimelineParams
                {
                    SourceID = entityId,
                    Id = p2
                });
                break;
            case 15: // CancelCast — p3=actionId（仅自身）
                if (entityId == (Data.Me.Object?.EntityID ?? 0))
                    Runtime.SpellActionTracker.Instance.Notify(Runtime.SpellActionType.CancelCast, p3);
                break;
            case 700: // ActionRejected — p3=actionId（仅自身）
                if (entityId == (Data.Me.Object?.EntityID ?? 0))
                    Runtime.SpellActionTracker.Instance.Notify(Runtime.SpellActionType.ActionRejected, p3);
                break;
            default:
                if (DService.Instance().ObjectTable.SearchByID(entityId) is not IPlayerCharacter)
                {
                    Fire(new ObjectChangeParams
                    {
                        SourceID = entityId,
                        TargetID = targetId32,
                        Command = (ushort)category,
                        P1 = p1,
                        P2 = p2,
                        P3 = p3,
                        P4 = p4
                    });
                }
                break;
        }
    }

    private void OnMapEffectDetour(nint self, uint index, ushort s1, ushort s2)
    {
        _mapEffectHook!.Original(self, index, s1, s2);

        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var state = s1 | ((uint)s2 << 16);
            BattleEvents.OnMapEffect((byte)index, new System.Numerics.Vector3(), now);
            Fire(new OnMapEffectCreateEvent
            {
                Pos = index,
                arg0 = s1,
                arg1 = s2
            });
            Fire(new OnEnvControlEvent
            {
                Index = index,
                Flag = s2
            });
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[GameEventHook] MapEffect 处理异常: {ex.Message}");
        }
    }

    #region SpawnNpc 签名 Hook（FFXIVClientStructs HandleSpawnNpcPacket）

    private void OnSpawnNpcDetour(uint targetId, nint packetPtr)
    {
        _spawnNpcHook!.Original(targetId, packetPtr);

        try
        {
            if (packetPtr == nint.Zero) return;

            unsafe
            {
                var packet = (PacketSpawnNpc*)packetPtr;
                var name = System.Text.Encoding.UTF8.GetString(packet->Name, 32).TrimEnd('\0');

                Fire(new UnitCreateCondParams
                {
                    EntityId = targetId,
                    DataId = packet->BaseId,
                    Name = name,
                    Position = packet->Position
                });
            }
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[GameEventHook] SpawnNpc 处理异常: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 0x290)]
    private unsafe struct PacketSpawnNpc
    {
        [System.Runtime.InteropServices.FieldOffset(0x40)] public uint BaseId;
        [System.Runtime.InteropServices.FieldOffset(0x200)] public Vector3 Position;
        [System.Runtime.InteropServices.FieldOffset(0x242)] public fixed byte Name[32];
    }

    #endregion

    #region SpawnObject 签名 Hook（FFXIVClientStructs HandleSpawnObjectPacket）

    private void OnSpawnObjectDetour(uint targetId, nint packetPtr)
    {
        _spawnObjectHook!.Original(targetId, packetPtr);

        try
        {
            if (packetPtr == nint.Zero) return;

            unsafe
            {
                var packet = (PacketSpawnObject*)packetPtr;

                Fire(new ObjectSpawnParams
                {
                    EntityId = targetId,
                    DataId = packet->BaseId,
                    LayoutId = packet->LayoutId,
                    Position = new Vector3(packet->PositionX, packet->PositionY, packet->PositionZ)
                });
            }
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[GameEventHook] SpawnObject 处理异常: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 0x40)]
    private struct PacketSpawnObject
    {
        [System.Runtime.InteropServices.FieldOffset(0x04)] public uint BaseId;
        [System.Runtime.InteropServices.FieldOffset(0x08)] public uint EntityId;
        [System.Runtime.InteropServices.FieldOffset(0x0C)] public uint LayoutId;
        [System.Runtime.InteropServices.FieldOffset(0x34)] public float PositionX;
        [System.Runtime.InteropServices.FieldOffset(0x38)] public float PositionY;
        [System.Runtime.InteropServices.FieldOffset(0x3C)] public float PositionZ;
    }

    #endregion

    #region 轮询事件 (Unit消失, 天气变化)

    private readonly Dictionary<uint, (uint DataId, string Name)> _knownEntities = [];
    private uint _lastWeatherId;
    private long _nextPollAt;
    private const long TetherSnapshotPollIntervalMs = 100;
    private const float TetherSnapshotRange = 40f;

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = Environment.TickCount64;
        PollTetherSnapshots(now);

        if (now < _nextPollAt) return;
        _nextPollAt = now + 500;

        var objectTable = DService.Instance().ObjectTable;
        if (objectTable == null) return;

        var currentIds = new HashSet<uint>();
        foreach (var obj in objectTable)
        {
            if (obj == null || obj.EntityID == 0) continue;
            currentIds.Add(obj.EntityID);
        }

        if (_knownEntities.Count > 0)
        {
            foreach (var (id, info) in _knownEntities)
            {
                if (!currentIds.Contains(id))
                {
                    Fire(new UnitDeleteCondParams
                    {
                        EntityId = id,
                        DataId = info.DataId,
                        Name = info.Name
                    });
                }
            }
        }

        var oldSnapshot = new Dictionary<uint, (uint, string)>(_knownEntities);
        _knownEntities.Clear();
        foreach (var id in currentIds)
        {
            if (oldSnapshot.TryGetValue(id, out var cached))
                _knownEntities[id] = cached;
            else
                _knownEntities[id] = (((objectTable.SearchByID(id) as IBattleNPC)?.DataID ?? 0,
                    objectTable.SearchByID(id)?.Name.ToString() ?? ""));
        }

        var currentWeather = OmenTools.OmenService.GameState.Weather;
        if (_lastWeatherId != 0 && currentWeather != _lastWeatherId)
        {
            Fire(new WeatherChangedCondParams
            {
                NewWeatherId = (int)currentWeather
            });
        }
        _lastWeatherId = currentWeather;
    }

    private void PollTetherSnapshots(long now)
    {
        if (now < _nextTetherSnapshotPollAt) return;
        _nextTetherSnapshotPollAt = now + TetherSnapshotPollIntervalMs;

        var objectTable = DService.Instance().ObjectTable;
        if (objectTable == null) return;
        if (objectTable.LocalPlayer is null)
        {
            _tetherSnapshots.Clear();
            return;
        }

        var current = new Dictionary<TetherSnapshotKey, TetherSnapshotState>();
        foreach (var obj in objectTable.SearchObjects(o => o is ICharacter && Me.DistanceToObject2D(o) <= TetherSnapshotRange, IObjectTable.CharactersRange))
        {
            if (obj is not ICharacter character) continue;
            unsafe
            {
                var tethers = character.ToStruct()->Vfx.Tethers;
                for (byte i = 0; i < tethers.Length; i++)
                {
                    var tether = tethers[i];
                    if (tether.Id == 0) continue;
                    var targetId = tether.TargetId.ObjectId;
                    if (targetId is 0 or 0xE0000000) continue;
                    current[new TetherSnapshotKey(character.EntityID, targetId, tether.Id, i)] = new TetherSnapshotState(tether.Progress);
                }
            }
        }

        foreach (var (key, state) in _tetherSnapshots)
        {
            if (!current.ContainsKey(key))
                FireTetherSnapshot(key, state.Progress, false);
        }

        foreach (var (key, state) in current)
        {
            if (!_tetherSnapshots.ContainsKey(key))
                FireTetherSnapshot(key, state.Progress, true);
        }

        _tetherSnapshots.Clear();
        foreach (var (key, state) in current)
            _tetherSnapshots[key] = state;
    }

    private void FireTetherSnapshot(TetherSnapshotKey key, byte progress, bool isActive)
    {
        Fire(new TetherSnapshotParams
        {
            TetherID = key.TetherID,
            SourceID = key.SourceID,
            TargetID = key.TargetID,
            SlotIndex = key.SlotIndex,
            Progress = progress,
            IsActive = isActive
        });
    }

    private readonly record struct TetherSnapshotKey(uint SourceID, uint TargetID, uint TetherID, byte SlotIndex);

    private readonly record struct TetherSnapshotState(byte Progress);

    #endregion

    #region ActorCast 签名 Hook（Splatoon 验证，替代不可靠的 packet opcode 0x039A）

    private unsafe void OnActorCastDetour(uint sourceId, ActorCastPacket* packetPtr)
    {
        _actorCastHook!.Original(sourceId, packetPtr);

        try
        {
            if (packetPtr == null) return;

            var packet = (PacketActorCast*)packetPtr;
            var actionId = packet->ActionId;
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var key = (sourceId, actionId);
            lock (_actorCastDedup)
            {
                if (_actorCastDedup.TryGetValue(key, out var lastMs) && (nowMs - lastMs) < 500)
                    return;
                _actorCastDedup[key] = nowMs;

                var cutoff = nowMs - 5000;
                var stale = _actorCastDedup.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
                foreach (var k in stale) _actorCastDedup.Remove(k);
            }

            var castObj = DService.Instance().ObjectTable.SearchByID(sourceId);
            if (ShouldPublishSelfCastStart(castObj is IPlayerCharacter, actionId))
            {
                Fire(new SelfCastStartCondParams
                {
                    SourceID = sourceId,
                    SpellId = actionId,
                    TotalCastTimeInSec = packet->CastTime,
                    StartCastTime = nowMs
                });
            }
            else
            {
                Fire(new EnemyCastSpellCondParams
                {
                    SourceID = sourceId,
                    DataId = (castObj as IBattleNPC)?.DataID ?? 0,
                    CastPos = new Vector3(
                        ConvertCastCoord(packet->PosX),
                        ConvertCastCoord(packet->PosY),
                        ConvertCastCoord(packet->PosZ)),
                    TotalCastTimeInSec = packet->CastTime,
                    CastRot = packet->RawRotation * 0.0095875263f * 0.0099999998f - MathF.PI,
                    SpellId = actionId,
                    StartCastTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            }
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[GameEventHook] ActorCast 签名处理异常: {ex.Message}");
        }
    }

    private static float ConvertCastCoord(ushort raw) => raw * 3.0518043f * 0.0099999998f - 1000.0f;

    internal static bool ShouldPublishSelfCastStartForTests(bool isPlayerCharacter, uint actionId)
        => ShouldPublishSelfCastStart(isPlayerCharacter, actionId);

    private static bool ShouldPublishSelfCastStart(bool isPlayerCharacter, uint actionId)
        => isPlayerCharacter && actionId != 0;

    #endregion

    #region ActionEffect Hook

    private void OnActionEffect(
        uint sourceId, nint sourceCharacter, nint pos,
        nint effectHeader, nint effectArray, nint effectTail)
    {
        _actionEffectHook!.Original(sourceId, sourceCharacter, pos, effectHeader, effectArray, effectTail);

        try
        {
            if (effectHeader == nint.Zero) return;

            unsafe
            {
                var header = (EffectHeader*)effectHeader;
                var actionId = header->ActionID;
                var animationId = header->AnimationId;
                var targetCount = header->TargetCount;
                var animTargetId = header->AnimationTargetId;

                var sourceObj = DService.Instance().ObjectTable.SearchByID(sourceId);
                var isPlayerSource = sourceObj is IPlayerCharacter;
                var isPetSource = sourceObj is IGameObject { OwnerID: not 0 };
                var isSelf = sourceId == (Data.Me.Object?.EntityID ?? 0);

                if (isSelf)
                {
                    Runtime.SpellActionTracker.Instance.Notify(Runtime.SpellActionType.Effect, actionId);
                    Runtime.AbilityThrottle.ConfirmActionEffect(actionId);
                }

                var entries = effectArray != nint.Zero ? (EffectEntry*)effectArray : null;
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var targets = effectTail != nint.Zero ? (ulong*)effectTail : null;

                for (byte i = 0; i < targetCount; i++)
                {
                    var entry = entries != null ? entries[i * 8] : default;
                    var targetOid = targets != null ? targets[i] : animTargetId;
                    var targetOid32 = (uint)(targetOid & 0xFFFFFFFF);

                    if (isSelf)
                        BattleEvents.OnActionEffect(actionId, sourceId, targetOid32, nowMs);


                    Fire(new ReceviceAbilityEffectCondParams
                    {
                        ActionId = actionId,
                        SourceID = sourceId,
                        TargetID = targetOid32,
                        AnimationID = animationId,
                        EffectType = entry.Type
                    });
                }

                if (targetCount == 0)
                {
                    var animTargetId32 = (uint)(animTargetId & 0xFFFFFFFF);

                    if (isSelf)
                        BattleEvents.OnActionEffect(actionId, sourceId, animTargetId32, nowMs);


                    {
                        Fire(new ReceviceAbilityEffectCondParams
                        {
                            ActionId = actionId,
                            SourceID = sourceId,
                            TargetID = animTargetId32,
                            AnimationID = animationId,
                            EffectType = 0
                        });

                        var p = pos != nint.Zero ? (Vector3Raw*)pos : null;
                        var posVec = p != null ? new Vector3(p->X, p->Y, p->Z) : Vector3.Zero;
                        Fire(new ReceviceNoTargetAbilityEffectCondParams
                        {
                            SourceID = sourceId,
                            ActionId = actionId,
                            Name = OmenTools.Interop.Game.Lumina.LuminaWrapper.GetActionName(actionId),
                            pos = posVec
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[GameEventHook] ActionEffect 处理异常: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct EffectHeader
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public ulong AnimationTargetId;
        [System.Runtime.InteropServices.FieldOffset(8)] public uint ActionID;
        [System.Runtime.InteropServices.FieldOffset(12)] public uint GlobalEffectCounter;
        [System.Runtime.InteropServices.FieldOffset(16)] public float AnimationLockTime;
        [System.Runtime.InteropServices.FieldOffset(20)] public uint SomeTargetID;
        [System.Runtime.InteropServices.FieldOffset(24)] public ushort SourceSequence;
        [System.Runtime.InteropServices.FieldOffset(26)] public ushort Rotation;
        [System.Runtime.InteropServices.FieldOffset(28)] public ushort AnimationId;
        [System.Runtime.InteropServices.FieldOffset(30)] public byte Variation;
        [System.Runtime.InteropServices.FieldOffset(31)] public byte ActionType;
        [System.Runtime.InteropServices.FieldOffset(32)] public byte Flags;
        [System.Runtime.InteropServices.FieldOffset(33)] public byte TargetCount;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 8)]
    private struct EffectEntry
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public byte Type;
        [System.Runtime.InteropServices.FieldOffset(1)] public byte Param0;
        [System.Runtime.InteropServices.FieldOffset(2)] public byte Param1;
        [System.Runtime.InteropServices.FieldOffset(3)] public byte Param2;
        [System.Runtime.InteropServices.FieldOffset(4)] public byte Param3;
        [System.Runtime.InteropServices.FieldOffset(5)] public byte Param4;
        [System.Runtime.InteropServices.FieldOffset(6)] public ushort Value;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Vector3Raw
    {
        public float X;
        public float Y;
        public float Z;
    }

    #endregion

    #region ObjectEffect Hook

    /// <summary>
    /// 从 XSZYYS 签名: 4C 8B DC 53 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 49 89 6B F0 48 8B D9 49 89 7B E0
    /// 函数签名: void EventObject.PlayAnimation(EventObject* a1, uint a2, uint a3, ulong a4)
    /// </summary>
    private unsafe void OnObjectEffect(EventObject* eventObjectPtr, uint data1, uint data2, ulong a4)
    {
        _objectEffectHook!.Original(eventObjectPtr, data1, data2, a4);

        try
        {
            uint objectId = 0;
            if (eventObjectPtr != null)
                objectId = eventObjectPtr->EntityId;

            Fire(new ObjectEffectParams
            {
                ObjectID = objectId,
                Data1 = data1,
                Data2 = data2
            });
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[GameEventHook] ObjectEffect 处理异常: {ex.Message}");
        }
    }

    #endregion

    #region NpcYell 签名 Hook

    private nint OnNpcYellDetour(nint packetPtr)
    {
        var result = _npcYellHook!.Original(packetPtr);

        try
        {
            if (packetPtr == nint.Zero) return result;
            unsafe
            {
                var packet = (NpcYellPacket*)packetPtr;
                var sourceId = packet->SourceID;
                var yellId = packet->Message;
                var obj = DService.Instance().ObjectTable.SearchByID((uint)(sourceId & 0xFFFFFFFF));
                var sourceName = obj?.Name?.ToString() ?? "";

                Fire(new NpcYellCondParams
                {
                    sourceId = sourceId,
                    sourceName = sourceName,
                    yellId = (short)yellId,
                    yellMsg = ""
                });
            }
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[GameEventHook] NpcYell 处理异常: {ex.Message}");
        }

        return result;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    private struct NpcYellPacket
    {
        public ulong SourceID;
        public int u8;
        public ushort Message;
        public ushort uE;
        public ulong u10;
        public ulong u18;
    }

    #endregion

    #region raw packet structs



    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Pack = 1)]
    private struct PacketActorCast
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public ushort ActionId;
        [System.Runtime.InteropServices.FieldOffset(2)] public byte ActionType;
        [System.Runtime.InteropServices.FieldOffset(3)] public byte Unknown;
        [System.Runtime.InteropServices.FieldOffset(4)] public uint Unknown1;
        [System.Runtime.InteropServices.FieldOffset(8)] public float CastTime;
        [System.Runtime.InteropServices.FieldOffset(12)] public uint TargetId;
        [System.Runtime.InteropServices.FieldOffset(16)] public ushort RawRotation;
        [System.Runtime.InteropServices.FieldOffset(20)] public uint Unknown2;
        [System.Runtime.InteropServices.FieldOffset(24)] public ushort PosX;
        [System.Runtime.InteropServices.FieldOffset(26)] public ushort PosY;
        [System.Runtime.InteropServices.FieldOffset(28)] public ushort PosZ;
    }

    #endregion
}
