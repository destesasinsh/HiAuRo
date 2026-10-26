using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;
using OmenTools.Dalamud.Services.Game.Object.Enums;
using Dalamud.Game.ClientState.Objects.Enums;
using IGameObj = OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds.IGameObject;

namespace HiAuRo;

public static partial class Data
{
    /// <summary>
    /// 对象扫描 —— 一次扫描 OmenTools ObjectTable，语义分类
    /// </summary>
    public static class Objects
    {
        public static readonly List<IGameObj> All = [];
        public static readonly List<IGameObj> Allies = [];
        public static readonly List<IGameObj> Enemies = [];
        public static readonly List<IGameObj> Party = [];
        public static readonly List<IGameObj> Pets = [];
        public static readonly List<IGameObj> Summons = [];
        public static readonly List<IGameObj> Environment = [];
        public static readonly List<IGameObj> Others = [];

        private static readonly Dictionary<uint, IGameObj> _byEntityId = [];

        // 缓存 BuddyList EntityID，避免每对象嵌套遍历 BuddyList
        private static readonly HashSet<uint> _buddyEntityIds = [];

        public static void Refresh()
        {
            ClearAll();
            _byEntityId.Clear();
            _buddyEntityIds.Clear();

            if (!IsReady) return;

            var self = Me.Object;
            if (self == null) return;

            // 预缓存 BuddyList，将 O(N*BuddyCount) 降为 O(BuddyCount) + O(1)
            foreach (var buddy in DService.Instance().BuddyList)
                _buddyEntityIds.Add(buddy.EntityId);

            foreach (var obj in DService.Instance().ObjectTable)
            {
                All.Add(obj);
                _byEntityId[obj.EntityID] = obj;

                switch (obj.ObjectKind)
                {
                    case ObjectKind.Pc:
                        Party.Add(obj);
                        Allies.Add(obj);
                        break;

                    case ObjectKind.BattleNpc:
                        if (obj is IBattleNPC battleNpc)
                        {
                            if (IsOwnedByParty(obj) || IsOwnedByBuddy(obj))
                            {
                                Pets.Add(obj);
                                Allies.Add(obj);
                            }
                            else if (battleNpc.BattleNPCKind == BattleNpcSubKind.Pet)
                            {
                                Summons.Add(obj);
                                Others.Add(obj);
                            }
                            else if (IsHostile(obj))
                            {
                                if (IsActuallyFriendly(obj))
                                    Allies.Add(obj);
                                else
                                    Enemies.Add(obj);
                            }
                            else
                            {
                                Others.Add(obj);
                            }
                        }
                        else
                        {
                            Others.Add(obj);
                        }
                        break;

                    case ObjectKind.EventNpc:
                    case ObjectKind.Retainer:
                    case ObjectKind.Companion:
                    case ObjectKind.Mount:
                    case ObjectKind.Ornament:
                    case ObjectKind.EventObj:
                        Environment.Add(obj);
                        break;

                    default:
                        Others.Add(obj);
                        break;
                }
            }
        }

        private static void ClearAll()
        {
            All.Clear(); Allies.Clear(); Enemies.Clear(); Party.Clear();
            Pets.Clear(); Summons.Clear(); Environment.Clear(); Others.Clear();
        }

        /// <summary>按 EntityID 查找对象（O(1)）</summary>
        public static IGameObj? GetById(uint entityId)
        {
            return _byEntityId.GetValueOrDefault(entityId);
        }

        /// <summary>是否为敌对目标 (可被攻击 + 可选中 + 未死亡)</summary>
        private static bool IsHostile(IGameObj obj)
        {
            return obj.IsTargetable && obj.IsDead != true;
        }

        /// <summary>是否被 BuddyList 中的宠物持有（用缓存 HashSet 替代嵌套遍历）</summary>
        private static bool IsOwnedByBuddy(IGameObj obj)
        {
            return obj.OwnerID != 0 && _buddyEntityIds.Contains(obj.OwnerID);
        }

        /// <summary>是否由队友持有（用 _byEntityId 替代 SearchByID 原生调用）</summary>
        private static bool IsOwnedByParty(IGameObj obj)
        {
            if (obj.OwnerID == 0) return false;
            return _byEntityId.TryGetValue(obj.OwnerID, out var owner) && owner.ObjectKind == ObjectKind.Pc;
        }

        /// <summary>排除假阳性（用 StatusFlags + _byEntityId 替代 SearchByID 原生调用）</summary>
        private static bool IsActuallyFriendly(IGameObj obj)
        {
            // 自身宠物/召唤物
            if (obj.OwnerID == Me.Object?.EntityID) return true;
            // StatusFlags 位标志快速判断：友方/队友/联盟成员
            if (obj is ICharacter c && (c.StatusFlags & (StatusFlags.Friend | StatusFlags.PartyMember | StatusFlags.AllianceMember)) != 0)
                return true;
            return false;
        }
    }
}
