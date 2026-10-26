using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;
using System.Numerics;

namespace HiAuRo.ACR;

/// <summary>
/// 目标辅助 —— 目标选择 / 身位 / AOE 目标查找
/// </summary>
public static class TargetHelper
{
    /// <summary>获取目标周围敌人数（AOE 判断核心）</summary>
    public static int GetNearbyEnemyCount(IGameObject? target, float range = 5f)
    {
        if (target == null) return 0;

        var count = 0;
        foreach (var enemy in Data.Objects.Enemies)
        {
            if (enemy == null || enemy.GameObjectID == target.GameObjectID) continue;
            var dist = Vector3.Distance(target.Position, enemy.Position);
            if (dist <= range)
                count++;
        }
        return count;
    }

    /// <summary>判断是否在目标背后（前后判定）</summary>
    public static bool IsBehind(IGameObject? target)
    {
        if (target == null) return false;
        var self = Data.Me.Object;
        if (self == null) return false;

        var toTarget = self.Position - target.Position;
        var facing = GetForward(target);
        var dot = Vector3.Dot(Vector3.Normalize(toTarget), facing);
        return dot < -0.5f;
    }

    /// <summary>判断是否在目标侧面</summary>
    public static bool IsFlanking(IGameObject? target)
    {
        if (target == null) return false;
        var self = Data.Me.Object;
        if (self == null) return false;

        var toTarget = self.Position - target.Position;
        var right = GetRight(target);
        var dot = Vector3.Dot(Vector3.Normalize(toTarget), right);
        return Math.Abs(dot) > 0.5f;
    }

    private static Vector3 GetForward(IGameObject obj)
    {
        var rot = obj.Rotation; // Rotation 是绕 Y 轴的弧度
        return new Vector3((float)Math.Sin(rot), 0, (float)Math.Cos(rot));
    }

    private static Vector3 GetRight(IGameObject obj)
    {
        var rot = obj.Rotation;
        return new Vector3((float)Math.Cos(rot), 0, -(float)Math.Sin(rot));
    }

    /// <summary>判断 Boss 是否在释放 AOE（读条时间 >= 阈值）</summary>
    public static bool TargetCastingIsBossAOE(IBattleChara? target, int castTimeThresholdMs = 3000)
    {
        if (target == null || !target.IsCasting) return false;
        if (!target.IsCastInterruptible) return true; // Boss AOE 通常不可打断
        return target.TotalCastTime * 1000 >= castTimeThresholdMs;
    }

    /// <summary>获取当前目标读条剩余时间（毫秒），未读条返回 0</summary>
    public static float GetCastingSpellTiming(IBattleChara? target)
    {
        if (target == null || !target.IsCasting) return 0;
        return (target.TotalCastTime - target.CurrentCastTime) * 1000f;
    }

    /// <summary>获取最佳 AOE 目标 — 周围敌人最多的可攻击目标</summary>
    public static unsafe IBattleChara? GetMostCanTargetObjects(uint spellId, int minCount, float range = 5f)
    {
        IBattleChara? best = null;
        int bestCount = 0;

        foreach (var obj in Data.Objects.Enemies)
        {
            if (obj is not IBattleChara bc) continue;
            if (!bc.IsTargetable || bc.IsDead == true) continue;

            int count = 0;
            foreach (var other in Data.Objects.Enemies)
            {
                if (other is not IBattleChara bc2) continue;
                if (!bc2.IsTargetable || bc2.IsDead == true) continue;
                if (bc2.EntityID == bc.EntityID) continue;

                var dx = bc2.Position.X - bc.Position.X;
                var dz = bc2.Position.Z - bc.Position.Z;
                if (MathF.Sqrt(dx * dx + dz * dz) <= range)
                    count++;
            }

            if (count > bestCount)
            {
                bestCount = count;
                best = bc;
            }
        }

        return bestCount >= minCount ? best : null;
    }
}
