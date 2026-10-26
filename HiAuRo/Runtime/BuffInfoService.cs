using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Textures;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HiAuRo.Infrastructure;
using HiAuRo.ImGuiLib;
using OmenTools;
using OmenTools.Dalamud.Services.Game.Abstractions;
using OmenTools.Interop.Game.Helpers;
using OmenTools.OmenService;

namespace HiAuRo.Runtime;

/// <summary>
///     鼠标悬停自身 / 目标状态栏的 buff 图标时，在鼠标位置绘制一个瞬态信息框显示该状态的 StatusID。
///     原生 status tooltip 不走字符串数组（无 StringArrayType.Status），也没有公开的“当前悬停状态”API，
///     故采用：每帧对状态栏 addon 做节点命中测试（hit-test），找到鼠标下的图标槽位，
///     再按“槽位序号 → 角色 StatusList 过滤后序号”映射回 StatusID。
///
///     该映射（哪些节点是图标、槽位与 StatusList 的顺序关系）与 HUD 布局 / 版本相关，
///     故 <see cref="PluginConfig.DebugEnabled"/> 开启时会输出详细日志，便于进游戏一次性校准。
/// </summary>
public static unsafe class BuffInfoService
{
    private static bool _initialized;

    // 自身状态栏候选 addon：_Status 为默认合并显示；_StatusCustom0/1/2 为“分离显示”HUD 选项下的增益/减益/其它
    private static readonly string[] SelfAddons =
    [
        "_Status", "_StatusCustom0", "_StatusCustom1", "_StatusCustom2"
    ];

    // 目标状态栏候选 addon：_TargetInfoBuffDebuff 为分离目标信息；_TargetInfo 为合并目标信息
    private static readonly string[] TargetAddons =
    [
        "_TargetInfoBuffDebuff", "_TargetInfo"
    ];

    // 命中结果（供绘制使用）
    private static bool   _hasResult;
    private static uint   _resStatusId;
    private static string _resName = "";
    private static uint   _resIcon;
    private static float  _resRemaining;
    private static ushort _resParam;
    private static string _debugSuffix  = "";

    // 调试节流：避免每帧刷屏
    private static int    _lastDebugTick;
    private static string _lastDebugKey = "";

    public static void Init()
    {
        if (_initialized)
            return;

        DService.Instance().UIBuilder.Draw += Draw;
        _initialized = true;
    }

    public static void Shutdown()
    {
        if (!_initialized)
            return;

        DService.Instance().UIBuilder.Draw -= Draw;
        _initialized = false;
    }

    private static void Draw()
    {
        if (!PluginConfig.Instance.ShowBuffIdEnabled)
            return;

        _hasResult    = false;
        _resStatusId  = 0;
        _resName      = "";
        _resIcon      = 0;
        _resRemaining = 0;
        _resParam     = 0;
        _debugSuffix  = "";

        var mouse = ImGui.GetMousePos();

        // 依次扫描自身 / 目标状态栏，命中即停
        if (!TryResolve(SelfAddons, mouse, isTarget: false))
            TryResolve(TargetAddons, mouse, isTarget: true);

        if (!_hasResult)
            return;

        DrawInfoBox(mouse);
    }

    private static void DrawInfoBox(Vector2 mouse)
    {
        var cfg = PluginConfig.Instance;

        var bg = Theme.Colors.BgElevated;
        bg.W = Math.Clamp(cfg.ShowBuffIdBgAlpha, 0.2f, 1.0f);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Theme.RadiusMD);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, bg);
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.Colors.GlassBorder);

        ImGui.SetNextWindowPos(mouse + new Vector2(18, 18));
        ImGui.SetNextWindowBgAlpha(bg.W);
        ImGui.Begin("##HiAuRoBuffInfo",
            ImGuiWindowFlags.NoTitleBar         | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoInputs           | ImGuiWindowFlags.NoNav    |
            ImGuiWindowFlags.NoSavedSettings    | ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize);

        // 图标
        if (cfg.ShowBuffIdShowIcon && _resIcon != 0 &&
            DService.Instance().Texture.TryGetFromGameIcon(new GameIconLookup(_resIcon), out var tex))
        {
            var iconSize = ImGui.GetTextLineHeightWithSpacing() * 2f;
            // 状态图标非正方（40x56），按比例缩放
            ImGui.Image(tex.GetWrapOrEmpty().Handle, new Vector2(iconSize * 0.72f, iconSize));
            ImGui.SameLine(0, 8);
        }

        ImGui.BeginGroup();
        {
            // 状态名
            if (cfg.ShowBuffIdShowName && !string.IsNullOrEmpty(_resName))
                ImGui.TextColored(Theme.Colors.TextPrimary, _resName);

            // StatusID 行
            var idText = cfg.ShowBuffIdHex ? $"0x{_resStatusId:X}" : _resStatusId.ToString();
            ImGui.TextColored(Theme.Colors.TextTertiary, "StatusID");
            ImGui.SameLine(0, 6);
            ImGui.TextColored(Theme.Colors.AccentBlue, idText);

            // 剩余时间 / 层数
            var extras = new List<(string label, string value, Vector4 color)>();
            if (cfg.ShowBuffIdShowRemainingTime)
                extras.Add(("剩余", FormatRemaining(_resRemaining), Theme.Colors.AccentGreen));
            if (cfg.ShowBuffIdShowStackCount && _resParam > 0)
                extras.Add(("层数", _resParam.ToString(), Theme.Colors.AccentOrange));

            foreach (var (label, value, color) in extras)
            {
                ImGui.TextColored(Theme.Colors.TextTertiary, label);
                ImGui.SameLine(0, 6);
                ImGui.TextColored(color, value);
            }
        }
        ImGui.EndGroup();

        // 调试后缀
        if (cfg.DebugEnabled && !string.IsNullOrEmpty(_debugSuffix))
        {
            ImGui.Separator();
            ImGui.TextColored(Theme.Colors.TextTertiary, _debugSuffix);
        }

        ImGui.End();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
    }

    private static string FormatRemaining(float seconds)
    {
        var s = MathF.Abs(seconds);
        if (s <= 0.05f || s >= 1_000_000f)
            return "∞";
        if (s >= 60f)
            return $"{(int)(s / 60)}:{(int)(s % 60):D2}";
        return $"{s:F1}s";
    }

    private static bool TryResolve(string[] addonNames, Vector2 mouse, bool isTarget)
    {
        foreach (var name in addonNames)
        {
            if (!AddonHelper.TryGetByName(name, out var addon) || addon == null)
                continue;
            if (!addon->IsVisible || !addon->IsAddonAndNodesReady())
                continue;

            // 收集该 addon 内“像图标槽位”的可见组件节点
            var icons = CollectIconNodes(addon);
            if (icons.Count == 0)
                continue;

            // 命中测试
            var hitIndex = -1;
            for (var i = 0; i < icons.Count; i++)
            {
                if (PointInNode(icons[i].Node, mouse, addon->Scale))
                {
                    hitIndex = i;
                    break;
                }
            }

            if (hitIndex < 0)
                continue;

            // 槽位序号 = 命中节点在“按 NodeId 排序”后的位置
            var slot = hitIndex;

            var status = ResolveStatus(name, slot, isTarget);
            if (status == null)
                continue;

            var game = status.GameData.ValueNullable;
            _hasResult    = true;
            _resStatusId  = status.StatusID;
            _resName      = game?.Name.ExtractText() ?? "";
            _resIcon      = game?.Icon ?? 0;
            _resRemaining = status.RemainingTime;
            _resParam     = status.Param;
            _debugSuffix  = $"{name} · slot {slot} · node 0x{icons[hitIndex].NodeId:X}";

            LogProbe(name, addon, icons, hitIndex, slot, isTarget, status.StatusID);
            return true;
        }

        return false;
    }

    private readonly struct IconNode(nint node, uint nodeId)
    {
        public AtkResNode* Node   => (AtkResNode*)node;
        public uint        NodeId => nodeId;
    }

    /// <summary>
    ///     收集 addon 内疑似“状态图标槽位”的可见组件节点：组件节点(Type>=1000)、可见、尺寸接近图标。
    ///     按 NodeId 升序返回（状态栏通常按 NodeId 顺序填充槽位）。
    /// </summary>
    private static List<IconNode> CollectIconNodes(AtkUnitBase* addon)
    {
        var result = new List<IconNode>();

        var mgr = &addon->UldManager;
        for (var i = 0; i < mgr->NodeListCount; i++)
        {
            var node = mgr->NodeList[i];
            if (node == null)
                continue;
            if ((uint)node->Type < 1000) // 仅组件节点
                continue;
            if (!node->IsVisible())
                continue;

            var w = node->GetWidth();
            var h = node->GetHeight();
            // 状态图标槽位大致为 24 宽、图标+时间文字使高度略大；用较宽松阈值过滤背景/容器等大组件
            if (w is < 18 or > 64 || h is < 18 or > 80)
                continue;

            result.Add(new IconNode((nint)node, node->NodeId));
        }

        result.Sort((a, b) => a.NodeId.CompareTo(b.NodeId));
        return result;
    }

    private static bool PointInNode(AtkResNode* node, Vector2 p, float scale)
    {
        var x = node->ScreenX;
        var y = node->ScreenY;
        var w = node->GetWidth()  * scale;
        var h = node->GetHeight() * scale;
        return p.X >= x && p.X <= x + w && p.Y >= y && p.Y <= y + h;
    }

    /// <summary>
    ///     槽位序号 → 状态。按 addon 类别对 StatusList 做过滤后取第 slot 个。
    ///     这是最佳猜测，最终以调试日志校准。
    /// </summary>
    private static IStatus? ResolveStatus(string addonName, int slot, bool isTarget)
    {
        var chara = isTarget
            ? TargetManager.Target as IBattleChara
            : DService.Instance().ObjectTable.LocalPlayer;
        if (chara == null)
            return null;

        var all = chara.StatusList.Where(s => s != null && s.StatusID != 0).ToList();

        IEnumerable<IStatus> filtered = addonName switch
        {
            "_StatusCustom0" => all.Where(s => CategoryOf(s) == 1 && !IsFcBuff(s)),
            "_StatusCustom1" => all.Where(s => CategoryOf(s) == 2),
            "_StatusCustom2" => all.Where(s => IsFcBuff(s) || CategoryOf(s) == 0),
            _                => all, // _Status / 目标：合并显示，直接按数组顺序
        };

        var list = filtered.ToList();
        if (slot < 0 || slot >= list.Count)
            return null;

        return list[slot];
    }

    private static byte CategoryOf(IStatus s) =>
        (byte)(s.GameData.ValueNullable?.StatusCategory ?? 0);

    private static bool IsFcBuff(IStatus s) =>
        s.GameData.ValueNullable?.IsFcBuff ?? false;

    private static void LogProbe(string addonName, AtkUnitBase* addon, List<IconNode> icons, int hitIndex, int slot, bool isTarget, uint statusId)
    {
        if (!PluginConfig.Instance.DebugEnabled)
            return;

        // 节流：同一 addon+slot 每 1s 最多一条
        var key = $"{addonName}:{slot}";
        if (key == _lastDebugKey && Environment.TickCount - _lastDebugTick < 1000)
            return;
        _lastDebugKey  = key;
        _lastDebugTick = Environment.TickCount;

        var log = DService.Instance().Log;
        log.Debug($"[BuffInfo] addon={addonName} scale={addon->Scale:F2} iconNodes={icons.Count} hitIndex={hitIndex} slot={slot} -> StatusID={statusId}");

        // 图标节点几何（便于确认过滤与排序是否正确）
        for (var i = 0; i < icons.Count; i++)
        {
            var n = icons[i].Node;
            log.Debug($"[BuffInfo]   icon[{i}] node=0x{icons[i].NodeId:X} pos=({n->ScreenX:F0},{n->ScreenY:F0}) size=({n->GetWidth()}x{n->GetHeight()}){(i == hitIndex ? " <== HIT" : "")}");
        }

        // 角色 StatusList 全量（便于确定槽位与 StatusList 的映射关系）
        var chara = isTarget
            ? TargetManager.Target as IBattleChara
            : DService.Instance().ObjectTable.LocalPlayer;
        if (chara != null)
        {
            var idx = 0;
            foreach (var s in chara.StatusList)
            {
                if (s == null || s.StatusID == 0) continue;
                var g = s.GameData.ValueNullable;
                log.Debug($"[BuffInfo]   status[{idx}] id={s.StatusID} cat={(g?.StatusCategory ?? 0)} fc={(g?.IsFcBuff ?? false)} name={g?.Name.ExtractText()}");
                idx++;
            }
        }
    }
}
