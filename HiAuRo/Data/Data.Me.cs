using OmenTools.OmenService;
using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;

namespace HiAuRo;

public static partial class Data
{
    /// <summary>
    /// 玩家自身数据 —— 转发 LocalPlayerState.*
    /// </summary>
    public static class Me
    {
        public static IPlayerCharacter? Object => LocalPlayerState.Object;

        public static string Name => LocalPlayerState.Name;

        public static uint ClassJob => Object?.ClassJob.RowId ?? LocalPlayerState.ClassJob;

        public static ushort CurrentLevel => LocalPlayerState.CurrentLevel;

        public static bool IsMoving => LocalPlayerState.Instance().IsMoving;

        public static bool IsInParty => LocalPlayerState.IsInParty;

        public static bool IsPartyLeader => LocalPlayerState.IsPartyLeader;

        public static float DistanceToObject2D(IGameObject? target, bool ignoreRadius = true) =>
            LocalPlayerState.DistanceToObject2D(target, ignoreRadius);

        public static float DistanceToObject3D(IGameObject? target, bool ignoreRadius = true) =>
            LocalPlayerState.DistanceToObject3D(target, ignoreRadius);

        public static bool HasStatus(uint statusID, out int index, uint sourceID = 0xE0000000) =>
            LocalPlayerState.HasStatus(statusID, out index, sourceID);
    }
}
