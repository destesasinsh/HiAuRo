using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace HiAuRo.ACR.Extension;

/// <summary>
/// GameObject/ICharacter/IBattleChara 扩展方法 —— ACR 作者常用工具
/// 命名和签名尽量保持一致
/// </summary>
public static class GameObjectExtension
{
    #region Aura / Buff

    public static bool HasAura(this IBattleChara? bc, uint auraId, float timeLeft = 0)
    {
        if (bc == null) return false;
        if (!bc.StatusList.TryGetStatus(auraId, out var s, out _) || s == null) return false;
        return timeLeft <= 0 || s.RemainingTime >= timeLeft;
    }

    public static bool HasMyAura(this IBattleChara? bc, uint auraId)
    {
        if (bc == null || Data.Me.Object == null) return false;
        return bc.StatusList.Any(s => s.StatusID == auraId && s.SourceID == Data.Me.Object.EntityID);
    }

    public static int GetAuraStack(this IBattleChara bc, uint id)
    {
        if (bc == null) return 0;
        bc.StatusList.TryGetStatus(id, out var st, out _);
        // FFXIVClientStructs Status.StackCount 已在 DT 版本移除，改用 Param 近似
        return st != null ? st.Param > 0 ? st.Param : 1 : 0;
    }

    public static bool HasAnyAura(this IBattleChara bc, List<uint> auras, float timeLeft = 0)
    {
        if (bc == null) return false;
        return auras.Any(id => bc.HasAura(id, timeLeft));
    }

    public static float GetAuraTimeLeft(this IBattleChara bc, uint auraId, bool fromSelf = true)
    {
        if (bc == null) return 0;
        var srcId = fromSelf ? (Data.Me.Object?.EntityID ?? 0xE0000000) : 0xE0000000;
        return AuraHelper.GetAuraTimeLeft(bc, auraId, srcId);
    }

    #endregion

    #region HP / Combat State

    public static float CurrentHpPercent(this ICharacter c)
        => c.MaxHp > 0 ? (float)c.CurrentHp / c.MaxHp : 0f;

    public static float CurrentMpPercent(this ICharacter c)
        => c.MaxMp > 0 ? (float)c.CurrentMp / c.MaxMp : 0f;

    public static bool IsDead(this IBattleChara? bc)
        => bc == null || bc.IsDead == true || bc.CurrentHp == 0;

    public static bool IsAlive(this IBattleChara? bc) => !bc.IsDead();

    public static bool ValidAttackUnit(this IBattleChara? unit)
        => unit != null && unit.IsValid() && unit.IsTargetable && unit.IsAlive();

    public static bool CanAttack(this IBattleChara? bc)
        => bc.ValidAttackUnit();

    public static bool IsInCombat(this IBattleChara bc)
        => Data.Combat.InCombat;

    #endregion

    #region Role / Job Checks

    public static bool IsTank(this ICharacter c)
        => c != null && IsRole(c.ClassJob.RowId, 19, 21, 32, 37);

    public static bool IsHealer(this ICharacter c)
        => c != null && IsRole(c.ClassJob.RowId, 24, 28, 33, 40);

    public static bool IsMelee(this ICharacter c)
        => c != null && IsRole(c.ClassJob.RowId, 20, 22, 30, 34, 39, 41);

    public static bool IsRanged(this ICharacter c)
        => c != null && IsRole(c.ClassJob.RowId, 23, 31, 38);

    public static bool IsCaster(this ICharacter c)
        => c != null && IsRole(c.ClassJob.RowId, 25, 27, 35, 42);

    public static bool IsDps(this ICharacter c)
        => c != null && (IsMelee(c) || IsRanged(c) || IsCaster(c));

    public static bool IsInParty(this ICharacter c)
        => c != null && (c.StatusFlags & StatusFlags.PartyMember) != 0;

    private static bool IsRole(uint jobId, params uint[] ids)
        => ids.Contains(jobId);

    #endregion

    #region Boss / Dummy

    public static bool IsDummy(this IBattleChara bc)
        => bc != null && bc.NameID == 541;

    public static bool IsBoss(this IBattleChara bc)
        => bc != null && bc.IsTargetable && bc.IsDead != true && !bc.IsCastInterruptible && bc.DataID != 0;

    #endregion

    #region Distance / Position

    public static float Distance(this IGameObject source, IGameObject target, bool ignoreHeight = true)
    {
        if (source == null || target == null) return float.MaxValue;
        var p1 = source.Position;
        var p2 = target.Position;
        if (ignoreHeight) { p1.Y = 0; p2.Y = 0; }
        return Vector3.Distance(p1, p2);
    }

    public static float DistanceToPlayer(this IGameObject? obj)
    {
        if (obj == null) return float.MaxValue;
        return Data.Me.DistanceToObject3D(obj, false);
    }

    public static bool InActionRange(this IGameObject obj, float range = 30f)
        => obj.DistanceToPlayer() <= range;

    public static IEnumerable<T> GetObjectInRadius<T>(this IEnumerable<T> objects, float radius) where T : IGameObject
        => objects.Where(o => o.DistanceToPlayer() <= radius);

    #endregion

    #region Positional

    public static (Vector3 Origin, float Dir, float Radius, float Angle) BehindShape(this IGameObject obj)
    {
        var dir = obj.Rotation + MathF.PI;
        return (obj.Position, dir, obj.HitboxRadius + 3f, 45f);
    }

    public static bool InBehind(this IGameObject obj, Vector3 pos)
    {
        var (origin, dir, radius, angle) = obj.BehindShape();
        var delta = pos - origin;
        delta.Y = 0;
        var dist = delta.Length();
        if (dist > radius) return false;

        var facing = new Vector2(MathF.Cos(dir), MathF.Sin(dir));
        var toPos = new Vector2(delta.X, delta.Z);
        toPos = toPos.LengthSquared() > 0 ? Vector2.Normalize(toPos) : Vector2.UnitX;

        var dot = Vector2.Dot(facing, toPos);
        var halfAngle = angle / 2f * MathF.PI / 180f;
        return dot >= MathF.Cos(MathF.PI - halfAngle);
    }

    public static bool IsBehindTarget(this IGameObject source, IGameObject target)
        => target.InBehind(source.Position);

    #endregion

    #region Misc

    public static bool IsMe(this IGameObject? obj)
    {
        if (obj == null) return false;
        var self = Data.Me.Object;
        return self != null && obj.EntityID == self.EntityID;
    }

    public static void BecomeTargetOfLocalPlayer(this IGameObject obj)
        => OmenTools.OmenService.TargetManager.Target = obj;

    public static IBattleChara? GetCurrTarget(this IBattleChara unit)
        => unit.TargetObject as IBattleChara;

    public static unsafe bool IsInEnemiesList(this IGameObject obj)
    {
        if (obj == null) return false;
        var ptr = DService.Instance().GameGUI.GetAddonByName("_EnemyList", 1);
        if (ptr.Address == IntPtr.Zero) return false;
        var addon = (AddonEnemyList*)ptr.Address;

        var arr = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()
            ->GetUIModule()->GetRaptureAtkModule()->AtkModule.AtkArrayDataHolder.NumberArrays[21];

        for (int i = 0; i < addon->EnemyCount; i++)
        {
            if ((ulong)obj.GameObjectID == (ulong)arr->IntArray[8 + i * 6])
                return true;
        }
        return false;
    }

    public static unsafe uint GetNamePlateIcon(this IGameObject obj)
        => ((CSGameObject*)obj.Address)->NamePlateIconId;

    public static string ToLogString(this IGameObject? obj)
    {
        if (obj == null) return "[NULL]";
        return $"Name:{obj.Name} DataId:{obj.DataID} EntityId:{obj.EntityID} Targetable:{obj.IsTargetable}";
    }

    public static unsafe bool IsEnemy(this IGameObject obj)
        => obj != null && obj.IsTargetable
           && ActionManager.CanUseActionOnTarget(7 /* ActionType.Buddy */, (CSGameObject*)obj.Address);

    public static T? ToGameObject<T>(this uint objectId) where T : class, IGameObject
        => DService.Instance().ObjectTable.SearchByID(objectId) as T;

    /// <summary>是否有身位判定（非 Boss / 可移动目标可打断读条表示有身位）</summary>
    public static bool HasPositional(this IGameObject obj)
        => obj is IBattleChara bc && bc.IsCastInterruptible;

    #endregion
}
