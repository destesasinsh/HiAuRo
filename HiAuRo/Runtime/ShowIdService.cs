using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HiAuRo.Infrastructure;
using InteropGenerator.Runtime;
using Lumina.Text;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;
using OmenTools;
using OmenTools.OmenService;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaClassJob = Lumina.Excel.Sheets.ClassJob;
using LuminaStatus = Lumina.Excel.Sheets.Status;

namespace HiAuRo.Runtime;

/// <summary>
///     在物品 / 技能悬浮提示上追加显示其 ID（参考 SimpleTweaksPlugin 的 Show ID）。
///     复用 OmenTools 的 TooltipManager 完成原生 tooltip 文本注入，无需自行 hook addon。
/// </summary>
public static class ShowIdService
{
    private static bool _initialized;

    public static unsafe void Init()
    {
        if (_initialized)
            return;

        var tm = DService.Instance().GetOmenService<TooltipManager>();
        if (tm is null)
            return;

        tm.RegItem((_, itemId, ref mods) =>
        {
            if (!PluginConfig.Instance.ShowItemIdEnabled)
                return;

            var cfg = PluginConfig.Instance;
            mods.Add(new TooltipItemModification
            {
                Target = TooltipItemType.UICategory,
                Type   = TooltipModificationType.Append,
                Text   = new ReadOnlySeString($" [{FormatId(itemId, cfg.ShowItemIdHex, cfg.ShowItemIdBoth)}]"),
            });
        });

        tm.RegAction((_, actionId, ref mods) =>
        {
            if (!PluginConfig.Instance.ShowItemIdEnabled)
                return;

            var cfg      = PluginConfig.Instance;
            var resolved = ActionManager.Instance()->GetAdjustedActionId(actionId);

            var text = " [";
            if (cfg.ShowItemIdResolvedActionId)
                text += FormatId(resolved, cfg.ShowItemIdHex, cfg.ShowItemIdBoth);

            if (cfg.ShowItemIdOriginalActionId && cfg.ShowItemIdResolvedActionId && resolved != actionId)
            {
                text += " (";
                text += FormatId(actionId, cfg.ShowItemIdHex, cfg.ShowItemIdBoth);
                text += ")";
            }

            text += "]";

            mods.Add(new TooltipActionModification
            {
                Target = TooltipActionType.Category,
                Type   = TooltipModificationType.Append,
                Text   = new ReadOnlySeString(text),
            });

            AppendActionStatusInfo(resolved, ref mods);
        });

        _initialized = true;
    }

    /// <summary>
    ///     在技能 tooltip 类别区追加该技能关联的状态信息：
    ///     主路径 —— 解析技能描述文本（索引 13）中的框架句式（「对自身附加X状态」「追加效果：X」「发动条件：X状态中」「赋予目标X」等），
    ///     提取状态名候选并反查 Status 表得 ID，将 [id] 直接注入到描述原文对应状态名之后，整段作为一行追加；
    ///     副路径 —— 游戏原生 Excel 三字段（StatusGainSelf / ActionProcStatus / ActionCombo）兜底补全（独立行）。
    ///     说明：描述未显式提及状态名的技能（如「战斗之声」只说效果提升）无法被框架解析命中，此时由原生字段兜底；
    ///     游戏原生 Excel 没有「技能给目标施加的 buff/debuff」字段，目标类关联只能靠描述解析（见 PluginConfig 的 TODO）。
    /// </summary>
    private static void AppendActionStatusInfo(uint actionId, ref List<TooltipActionModification> mods)
    {
        var cfg = PluginConfig.Instance;

        // 刷新当前职业的 ClassJobCategory，供状态筛选使用（只保留通用/当前职业专属状态）
        _currentCjc = GetCurrentClassJobCategory();

        // 诊断：dump 该技能 tooltip 的全部原始字段（每个技能只打一次），用于分析描述文本中的关联信息
        DumpActionTooltip(actionId);

        if (!cfg.ShowActionStatusInfoEnabled)
            return;

        var seen = new HashSet<uint>();
        var currentName = GetActionTooltipRos(0).ExtractText().Trim();

        // 主路径：逐索引读取原始 SeString（保留着色宏），把 [id] 注入到匹配状态名/技能名所在的「原始索引行」，
        // 命中哪个索引就在哪个索引位置写回（忠实于原布局，且不破坏原有 ColorType/EdgeColorType 着色）。
        for (var idx = 0; idx < ActionDetailCount; idx++)
        {
            var ros = GetActionTooltipRos(idx);
            if (ros.IsEmpty)
                continue;

            var plain = ros.ExtractText();
            if (string.IsNullOrEmpty(plain))
                continue;

            // 状态名（框架解析）→ 状态ID；描述直接提及的技能名（绝对匹配）→ 技能ID
            var nameToIds  = ParseLinkedNames(plain);
            var skillToIds = ParseMentionedSkills(plain, currentName);
            var allToIds   = MergeNameToIds(nameToIds, skillToIds);
            if (allToIds.Count == 0)
                continue;

            var (injected, changed) = InjectSeString(ros, allToIds, seen);
            if (!changed)
                continue;

            mods.Add(new TooltipActionModification
            {
                Target = (TooltipActionType)idx,
                Type   = TooltipModificationType.Contribute,
                Text   = injected,
            });
        }

        // 主路径全程无命中（描述只说效果提升、未显式提及状态名）时，退回用技能名（索引 0）精确匹配 Status 表
        if (seen.Count == 0)
        {
            var nameRos  = GetActionTooltipRos(0);
            var nameText = nameRos.ExtractText().Trim();
            if (!string.IsNullOrEmpty(nameText))
            {
                var ids = ResolveStatusIds(nameText, -1);
                if (ids.Count > 0)
                {
                    foreach (var id in ids)
                        seen.Add(id);

                    var dict     = new Dictionary<string, List<uint>> { [nameText] = ids };
                    var (injected, changed) = InjectSeString(nameRos, dict, seen);
                    if (!changed)
                        injected = new ReadOnlySeString($"{nameText}[{string.Join("/", ids)}]");

                    mods.Add(new TooltipActionModification
                    {
                        Target = TooltipActionType.Name,
                        Type   = TooltipModificationType.Contribute,
                        Text   = injected,
                    });
                }
                else
                {
                    LogUnknownStatus(nameText);
                }
            }
        }

        // 副路径：原生三字段兜底补全（独立行，去重）
        var sheet = DService.Instance().Data.GetExcelSheet<LuminaAction>();
        if (sheet is not null && sheet.TryGetRow(actionId, out var action))
        {
            var gain  = action.StatusGainSelf;
            var proc  = action.ActionProcStatus;
            var combo = action.ActionCombo;

            if (cfg.ShowActionStatusGain && gain.RowId != 0 && seen.Add(gain.RowId))
                AppendLine(ref mods, "获得", gain.RowId, gain.ValueNullable?.Name.ExtractText());

            if (cfg.ShowActionStatusRequire &&
                proc.RowId != 0 && proc.ValueNullable is { } procDef && procDef.Status.RowId != 0 &&
                seen.Add(procDef.Status.RowId))
                AppendLine(ref mods, "需求", procDef.Status.RowId, procDef.Status.ValueNullable?.Name.ExtractText());

            if (cfg.ShowActionCombo && combo.RowId != 0 && seen.Add(combo.RowId))
                AppendLine(ref mods, "连击自", combo.RowId, combo.ValueNullable?.Name.ExtractText());
        }

        // 目标 buff 手动映射表（原生无字段，按 TODO 用人工表；描述解析的「赋予目标」类不自动显示）
        if (ManualTargetBuffs.TryGetValue(actionId, out var targetBuffs))
        {
            var statusSheet = DService.Instance().Data.GetExcelSheet<LuminaStatus>();
            foreach (var tb in targetBuffs)
            {
                if (!seen.Add(tb))
                    continue;

                var tname = statusSheet is not null && statusSheet.TryGetRow(tb, out var ts)
                    ? ts.Name.ExtractText()
                    : null;
                AppendLine(ref mods, "目标", tb, tname);
            }
        }
    }

    private const int ActionDetailCount = 16;

    private static ReadOnlySeString GetActionTooltipRos(int index)
    {
        unsafe
        {
            var sad = AtkStage.Instance()->GetStringArrayData(StringArrayType.ActionDetail);
            if (sad == null || index < 0 || index >= sad->AtkArrayData.Size)
                return new ReadOnlySeString();

            var slot = sad->StringArray[index];
            return slot.HasValue
                ? new ReadOnlySeString(new CStringPointer(slot.Value).AsSpan())
                : new ReadOnlySeString();
        }
    }

    private sealed record StatusEntry(uint Id, byte Category, uint IconId, ushort ClassJobCategory);

    private static readonly Regex[] FrameworkPatterns =
    [
        new(@"附加(.+?)状态",                 RegexOptions.Compiled), // 对自身附加 / 对目标附加
        new(@"获得(.+?)状态",                 RegexOptions.Compiled), // 获得 X 状态
        new(@"追加效果[^：:]*[：:]\s*(.+)",   RegexOptions.Compiled), // 追加效果：X（可带机率修饰）
        new(@"发动条件[：:](.+?)状态中",      RegexOptions.Compiled), // 发动条件：X 状态中（可「或」并列）
        new(@"赋予(?:目标|自身)?\s*(.+?)(?:状态)?(?:[，。\s/]|$)", RegexOptions.Compiled), // 赋予目标 X
    ];

    private static readonly char[] NameSeparators = ['或', '、', '和', '｜', '/'];

    /// <summary>
    ///     按句式框架解析描述文本，得到「状态名 → 筛选后 StatusID 列表」的映射（仅含框架内识别出的关联状态）。
    ///     框架内的状态名天然排除了技能名（如「可以发动辉煌箭」不在框架内）。
    /// </summary>
    private static Dictionary<string, List<uint>> ParseLinkedNames(string plain)
    {
        EnsureStatusIndex();

        var dict = new Dictionary<string, List<uint>>();
        var segments = plain.Split([" / ", "／"], StringSplitOptions.RemoveEmptyEntries);
        for (var si = 0; si < segments.Length; si++)
        {
            var seg = segments[si];
            for (var i = 0; i < FrameworkPatterns.Length; i++)
            {
                var m = FrameworkPatterns[i].Match(seg);
                if (!m.Success)
                    continue;

                // wantCategory：1=自身增益 2=敌方减益 -1=不限
                var want = i switch { 0 or 1 => 1, 2 => -1, 3 => 1, _ => 2 };
                var raw  = m.Groups[1].Value;
                var name = LeadingName(raw);
                if (name.Length < 2)
                    continue;

                if (i == 3) // 发动条件：可能「A或B」并列，逐个拆分
                {
                    foreach (var sub in name.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = sub.Trim();
                        if (trimmed.Length >= 2)
                            AddLinked(dict, trimmed, want);
                    }
                }
                else
                {
                    AddLinked(dict, name, want);
                }
            }
        }

        return dict;
    }

    private static void AddLinked(Dictionary<string, List<uint>> dict, string name, int want)
    {
        var ids = ResolveStatusIds(name, want);
        if (ids.Count == 0)
        {
            LogUnknownStatus(name);
            return;
        }

        if (!dict.TryGetValue(name, out var list))
            dict[name] = list = [];
        foreach (var id in ids)
            if (!list.Contains(id))
                list.Add(id);
    }

    /// <summary>
    ///     在原始 SeString 的 payload 流里注入 [id]：保留所有 ColorType/EdgeColorType 着色宏，
    ///     只在匹配状态名对应的文本 payload 内插入 [id]。返回注入后的 SeString，无变化返回 null。
    /// </summary>
    private static (ReadOnlySeString Result, bool Changed) InjectSeString(ReadOnlySeString ros, Dictionary<string, List<uint>> nameToIds, HashSet<uint> usedIds)
    {
        var       builder = new SeStringBuilder();
        var       changed = false;

        foreach (var payload in ros)
        {
            if (payload.Type == ReadOnlySePayloadType.Text)
            {
                var t = payload.ToString();
                var injected = InjectText(t, nameToIds, usedIds);
                if (injected != t)
                {
                    changed = true;
                    builder.Append(new ReadOnlySeString(injected));
                }
                else
                {
                    builder.Append(payload);
                }
            }
            else
            {
                builder.Append(payload);
            }
        }

        return (builder.ToReadOnlySeString(), changed);
    }

    /// <summary>
    ///     在单个文本片段内，把其中出现的状态名替换为「名称[id]」（支持一个片段内多处出现、按长度降序避免子串覆盖）。
    /// </summary>
    private static string InjectText(string t, Dictionary<string, List<uint>> nameToIds, HashSet<uint> usedIds)
    {
        var matches = new List<(int Start, int Len, List<uint> Ids)>();
        foreach (var kv in nameToIds)
        {
            var name = kv.Key;
            if (string.IsNullOrEmpty(name))
                continue;

            var pos = 0;
            while ((pos = t.IndexOf(name, pos, StringComparison.Ordinal)) >= 0)
            {
                matches.Add((pos, name.Length, kv.Value));
                pos += name.Length;
            }
        }

        if (matches.Count == 0)
            return t;

        matches.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Len.CompareTo(a.Len));

        var sb   = new StringBuilder(t.Length + matches.Count * 8);
        var last = 0;
        foreach (var mt in matches)
        {
            if (mt.Start < last)
                continue;

            sb.Append(t, last, mt.Start - last);
            sb.Append(t, mt.Start, mt.Len);
            sb.Append('[').Append(string.Join("/", mt.Ids)).Append(']');
            foreach (var id in mt.Ids)
                usedIds.Add(id);
            last = mt.Start + mt.Len;
        }

        sb.Append(t, last, t.Length - last);
        return sb.ToString();
    }

    /// <summary>
    ///     反查并筛选状态：排除无图标状态（视觉/装饰），按上下文类别过滤（自身增益/敌方减益），
    ///     再按当前职业的 ClassJobCategory 过滤（只保留通用状态或当前职业专属状态）。
    /// </summary>
    private static List<uint> ResolveStatusIds(string name, int wantCategory)
    {
        var result = new List<uint>();
        if (!_statusByName!.TryGetValue(name, out var candidates))
            return result;

        foreach (var c in candidates)
        {
            if (c.IconId == 0)                                                       // 无图标：视觉/装饰特效，排除
                continue;
            if (wantCategory != -1 && c.Category != 0 && c.Category != wantCategory) // 类别不符上下文，排除
                continue;

            // 临时诊断（不过滤）：打印职业不匹配，用于校准按职业过滤逻辑
            if (c.ClassJobCategory != 0 && c.ClassJobCategory != _currentCjc && _currentCjc != 0)
            {
                if (_loggedCjc.Add($"{name}:{c.ClassJobCategory}:{_currentCjc}"))
                    DService.Instance().Log.Information(
                        $"[ShowId] 职业不匹配 状态'{name}' cjc={c.ClassJobCategory} 当前cjc={_currentCjc} (id={c.Id})");
            }

            result.Add(c.Id);
        }

        return result;
    }

    private static string LeadingName(string raw)
    {
        var end = 0;
        foreach (var c in raw)
        {
            if (c is (>= '一' and <= '龥') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '·')
                end++;
            else
                break;
        }

        return end == 0 ? string.Empty : raw[..end];
    }

    private static ushort _currentCjc;

    private static Dictionary<string, List<StatusEntry>>? _statusByName;

    private static Dictionary<string, List<uint>>? _actionByName;

    private static ushort GetCurrentClassJobCategory()
    {
        var jobId = LocalPlayerState.ClassJob;
        if (jobId == 0)
            return 0;

        var sheet = DService.Instance().Data.GetExcelSheet<LuminaClassJob>();
        if (sheet is null || !sheet.TryGetRow(jobId, out var job))
            return 0;

        return (ushort)job.ClassJobCategory.RowId;
    }

    /// <summary>
    ///     手动映射：技能 → 该技能给目标施加的状态。
    ///     原生 Excel 没有「技能→目标 buff/debuff」字段，故描述解析到的「赋予目标 / 对目标造成」类状态
    ///     不自动显示，以本表（人工填写）为准。key=技能 Action RowId，value=目标状态 Status RowId 列表。
    /// </summary>
    private static readonly Dictionary<uint, List<uint>> ManualTargetBuffs = new()
    {
        // 示例（按实际游戏数据填写）：
        // { 7406u, new List<uint> { 1234 } }, // 烈毒咬箭 → 中毒(id)
    };

    private static void EnsureStatusIndex()
    {
        if (_statusByName is not null)
            return;

        var index = new Dictionary<string, List<StatusEntry>>();
        var sheet = DService.Instance().Data.GetExcelSheet<LuminaStatus>();
        if (sheet is not null)
        {
            foreach (var row in sheet)
            {
                var name = row.Name.ExtractText().Trim();
                if (name.Length < 2)
                    continue;

                if (!index.TryGetValue(name, out var list))
                    index[name] = list = [];
                list.Add(new StatusEntry(row.RowId, (byte)row.StatusCategory, row.Icon, (ushort)row.ClassJobCategory.RowId));
            }
        }

        _statusByName = index;
    }

    private static void EnsureActionIndex()
    {
        if (_actionByName is not null)
            return;

        var index = new Dictionary<string, List<uint>>();
        var sheet = DService.Instance().Data.GetExcelSheet<LuminaAction>();
        if (sheet is not null)
        {
            foreach (var row in sheet)
            {
                var name = row.Name.ExtractText().Trim();
                if (name.Length < 2)
                    continue;

                if (!index.TryGetValue(name, out var list))
                    index[name] = list = [];
                list.Add(row.RowId);
            }
        }

        _actionByName = index;
    }

    /// <summary>
    ///     描述里直接提及的技能名 = 绝对匹配的关联技能，标注其技能 ID（查 Action 表反查）。
    ///     排除当前技能自身（避免与 ShowId 的技能 ID 重复）；仅匹配长度 ≥ 3 的技能名以降低短词误匹配。
    /// </summary>
    private static Dictionary<string, List<uint>> ParseMentionedSkills(string plain, string currentName)
    {
        EnsureActionIndex();

        var dict = new Dictionary<string, List<uint>>();
        var segments = plain.Split([" / ", "／"], StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            foreach (var kv in _actionByName!)
            {
                var skill = kv.Key;
                if (skill.Length < 3 || skill == currentName)
                    continue;
                if (!seg.Contains(skill))
                    continue;

                if (!dict.TryGetValue(skill, out var list))
                    dict[skill] = list = [];
                foreach (var id in kv.Value)
                    if (!list.Contains(id))
                        list.Add(id);
            }
        }

        return dict;
    }

    private static Dictionary<string, List<uint>> MergeNameToIds(
        Dictionary<string, List<uint>> a, Dictionary<string, List<uint>> b)
    {
        var merged = new Dictionary<string, List<uint>>(a);
        foreach (var kv in b)
        {
            if (!merged.TryGetValue(kv.Key, out var list))
                merged[kv.Key] = kv.Value;
            else
                foreach (var id in kv.Value)
                    if (!list.Contains(id))
                        list.Add(id);
        }

        return merged;
    }

    private static readonly HashSet<string> _loggedUnknown = [];

    private static readonly HashSet<string> _loggedCjc = [];

    private static void LogUnknownStatus(string name)
    {
        if (!_loggedUnknown.Add(name))
            return;

        DService.Instance().Log.Information($"[ShowId] 描述解析到未收录状态名：{name}（无 Status 表匹配，可补充规则或人工表单）");
    }

    private static uint _lastDumpedAction;

    /// <summary>
    ///     临时诊断：把技能 tooltip 的 ActionDetail 字符串数组全部条目打印到日志，
    ///     用于分析描述文本（Description 等）中是否包含可解析的关联状态信息。
    ///     每个技能只打印一次，避免刷屏；定位方案后移除或收敛到调试开关。
    /// </summary>
    private static unsafe void DumpActionTooltip(uint actionId)
    {
        if (actionId == _lastDumpedAction)
            return;
        _lastDumpedAction = actionId;

        var sad = AtkStage.Instance()->GetStringArrayData(StringArrayType.ActionDetail);
        if (sad == null)
            return;

        var log  = DService.Instance().Log;
        var size = sad->AtkArrayData.Size;
        log.Information($"[ShowId] ===== Action {actionId} tooltip dump (size={size}) =====");

        var textArray = sad->StringArray;
        for (var i = 0; i < size; i++)
        {
            if (!textArray[i].HasValue)
                continue;

            var ros  = new ReadOnlySeString(new CStringPointer(textArray[i].Value).AsSpan());
            var text = ros.ExtractText();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            log.Information($"[ShowId]   [{i}] {text.Replace("\r", "").Replace("\n", " / ")}");

            var macros = DescribePayloads(ros);
            if (!string.IsNullOrEmpty(macros))
                log.Information($"[ShowId]      payloads[{i}]: {macros}");
        }
    }

    /// <summary>
    ///     临时诊断：列出 SeString 里所有非文本 payload（宏）的结构，用于确认状态名处是否内嵌 StatusID。
    /// </summary>
    private static string DescribePayloads(ReadOnlySeString ros)
    {
        var sb = new StringBuilder();
        foreach (var payload in ros)
        {
            if (payload.Type != ReadOnlySePayloadType.Macro)
                continue;

            if (sb.Length > 0)
                sb.Append(' ');

            sb.Append('<').Append(payload.MacroCode);
            foreach (var expr in payload)
                sb.Append(':').Append(expr.ToString());
            sb.Append('>');
        }

        return sb.ToString();
    }

    private static void AppendLine(ref List<TooltipActionModification> mods, string label, uint id, string? name)
    {
        var text = string.IsNullOrEmpty(name)
            ? $"{label}: ({id})"
            : $"{label}: {name} ({id})";

        mods.Add(new TooltipActionModification
        {
            Target = TooltipActionType.Description,
            Type   = TooltipModificationType.Append,
            Text   = new ReadOnlySeString(text),
        });
    }

    private static string FormatId(uint id, bool hex, bool both)
    {
        if (!hex)
            return id.ToString();

        return both ? $"{id} - 0x{id:X}" : $"0x{id:X}";
    }
}
