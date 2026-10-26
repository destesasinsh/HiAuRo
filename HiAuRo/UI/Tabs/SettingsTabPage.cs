using HiAuRo.Infrastructure;
using HiAuRo.ImGuiLib;
using CL = HiAuRo.ImGuiLib.ComponentLibrary;

namespace HiAuRo.UI.Tabs;

public sealed class SettingsTabPage : TabPageBase
{
    private readonly PluginConfig _config;
    private readonly Action _saveConfig;

    public SettingsTabPage(PluginConfig config, Action saveConfig)
        : base("设置", "settings", IconHelper.Icons.Settings)
    {
        _config = config;
        _saveConfig = saveConfig;
    }

    public override void DrawContent()
    {
        ImGui.Spacing();

        // ── 界面设置（控制面板 + 面板布局）──
        CL.Card("界面设置", () =>
        {
            var collapsedW = (int)_config.OverlayStatusBarCollapsedW;
            if (CL.InputNumber("sb_collapsed_w", "折叠宽度", ref collapsedW, 10))
            {
                _config.OverlayStatusBarCollapsedW = collapsedW;
                _saveConfig();
            }
            var collapsedH = (int)_config.OverlayStatusBarCollapsedH;
            if (CL.InputNumber("sb_collapsed_h", "折叠高度", ref collapsedH, 5))
            {
                _config.OverlayStatusBarCollapsedH = collapsedH;
                _saveConfig();
            }

            CL.Divider();

            var ui = HiAuRo.Runtime.ACRLifecycle.GetCurrentSettings();
            if (ui == null)
            {
                ImGui.TextColored(Theme.Colors.TextTertiary, "请先加载 ACR 以配置 QT/热键面板");
            }
            else
            {
                var qtCols = ui.QtCols > 0 ? ui.QtCols : 4;
                if (CL.SliderInt("qt_cols", "QT 每行列数", ref qtCols, 1, 10))
                {
                    ui.QtCols = qtCols;
                    HiAuRo.Runtime.ACRLifecycle.MarkSettingsDirty();
                }

                var hkCols = ui.HkCols > 0 ? ui.HkCols : 5;
                if (CL.SliderInt("hk_cols", "热键每行列数", ref hkCols, 1, 10))
                {
                    ui.HkCols = hkCols;
                    HiAuRo.Runtime.ACRLifecycle.MarkSettingsDirty();
                }

                var hkBtn = ui.HkBtnSize > 0 ? (int)ui.HkBtnSize : 50;
                if (CL.SliderInt("hk_btnsz", "热键按钮大小(px)", ref hkBtn, 28, 80))
                {
                    ui.HkBtnSize = hkBtn;
                    HiAuRo.Runtime.ACRLifecycle.MarkSettingsDirty();
                }
            }
        });

        // ── 显示 ID（调试）──
        CL.Card("显示 ID（调试）", () =>
        {
            var enabled = _config.ShowItemIdEnabled;
            if (CL.Switch("showid_enabled", "显示物品/技能 ID", ref enabled))
            {
                _config.ShowItemIdEnabled = enabled;
                _saveConfig();
            }

            ImGui.BeginDisabled(!enabled);
            {
                var hex = _config.ShowItemIdHex;
                if (CL.Switch("showid_hex", "十六进制显示", ref hex))
                {
                    _config.ShowItemIdHex = hex;
                    _saveConfig();
                }

                ImGui.BeginDisabled(!hex);
                {
                    var both = _config.ShowItemIdBoth;
                    if (CL.Switch("showid_both", "同时显示十进制与十六进制", ref both))
                    {
                        _config.ShowItemIdBoth = both;
                        _saveConfig();
                    }
                }
                ImGui.EndDisabled();

                var resolved = _config.ShowItemIdResolvedActionId;
                if (CL.Switch("showid_resolved", "显示解析后动作 ID", ref resolved))
                {
                    _config.ShowItemIdResolvedActionId = resolved;
                    _saveConfig();
                }

                ImGui.BeginDisabled(!resolved);
                {
                    var original = _config.ShowItemIdOriginalActionId;
                    if (CL.Switch("showid_original", "显示原始动作 ID", ref original))
                    {
                        _config.ShowItemIdOriginalActionId = original;
                        _saveConfig();
                    }
                }
                ImGui.EndDisabled();
            }
            ImGui.EndDisabled();

            ImGui.TextColored(Theme.Colors.TextTertiary, "在物品/技能悬浮提示的类别文字后追加其 ID，便于查表编写轴数据。");
        });

        // ── 技能状态关联（调试）──
        CL.Card("技能状态关联（调试）", () =>
        {
            var enabled = _config.ShowActionStatusInfoEnabled;
            if (CL.Switch("actionstatus_enabled", "在技能提示中显示关联状态", ref enabled))
            {
                _config.ShowActionStatusInfoEnabled = enabled;
                _saveConfig();
            }

            ImGui.BeginDisabled(!enabled);
            {
                var gain = _config.ShowActionStatusGain;
                if (CL.Switch("actionstatus_gain", "显示施放后获得的 buff", ref gain))
                {
                    _config.ShowActionStatusGain = gain;
                    _saveConfig();
                }

                var require = _config.ShowActionStatusRequire;
                if (CL.Switch("actionstatus_require", "显示需求/触发 buff", ref require))
                {
                    _config.ShowActionStatusRequire = require;
                    _saveConfig();
                }

                var combo = _config.ShowActionCombo;
                if (CL.Switch("actionstatus_combo", "显示连击前置技能", ref combo))
                {
                    _config.ShowActionCombo = combo;
                    _saveConfig();
                }
            }
            ImGui.EndDisabled();

            ImGui.TextColored(Theme.Colors.TextTertiary, "信息取自游戏原生数据。注意：原生数据无「技能给目标施加的 buff/debuff」，故暂不显示。");
        });

        CL.Card("显示 Buff ID（调试）", () =>
        {
            var enabled = _config.ShowBuffIdEnabled;
            if (CL.Switch("buffid_enabled", "悬停 buff 时显示 StatusID", ref enabled))
            {
                _config.ShowBuffIdEnabled = enabled;
                _saveConfig();
            }

            ImGui.BeginDisabled(!enabled);
            {
                var showIcon = _config.ShowBuffIdShowIcon;
                if (CL.Switch("buffid_icon", "显示状态图标", ref showIcon))
                {
                    _config.ShowBuffIdShowIcon = showIcon;
                    _saveConfig();
                }

                var showName = _config.ShowBuffIdShowName;
                if (CL.Switch("buffid_name", "显示状态名称", ref showName))
                {
                    _config.ShowBuffIdShowName = showName;
                    _saveConfig();
                }

                var showRemain = _config.ShowBuffIdShowRemainingTime;
                if (CL.Switch("buffid_remain", "显示剩余时间", ref showRemain))
                {
                    _config.ShowBuffIdShowRemainingTime = showRemain;
                    _saveConfig();
                }

                var showStack = _config.ShowBuffIdShowStackCount;
                if (CL.Switch("buffid_stack", "显示层数", ref showStack))
                {
                    _config.ShowBuffIdShowStackCount = showStack;
                    _saveConfig();
                }

                var hex = _config.ShowBuffIdHex;
                if (CL.Switch("buffid_hex", "十六进制显示 StatusID", ref hex))
                {
                    _config.ShowBuffIdHex = hex;
                    _saveConfig();
                }

                var alpha = _config.ShowBuffIdBgAlpha;
                if (CL.Slider("buffid_alpha", "背景不透明度", ref alpha, 0.2f, 1.0f, "%.2f"))
                {
                    _config.ShowBuffIdBgAlpha = alpha;
                    _saveConfig();
                }
            }
            ImGui.EndDisabled();

            ImGui.TextColored(Theme.Colors.TextTertiary, "鼠标悬停自身/目标状态栏的 buff 图标时，在鼠标位置显示其 StatusID。");
        });

        CL.Card("战斗录制", () =>
        {
            var logRoot = _config.CombatRecorderLogRoot ?? "";
            CL.FormRow("日志目录", () =>
            {
                ImGui.SetNextItemWidth(420);
                if (ImGui.InputText("##combat_rec_log_root", ref logRoot, 512))
                {
                    _config.CombatRecorderLogRoot = logRoot;
                    _saveConfig();
                }
            });

            ImGui.SameLine();
            if (CL.DefaultButton("恢复默认目录"))
            {
                _config.CombatRecorderLogRoot = @"E:\DalamudPlugins\HiAuRo\Recordings\CombatRecorder";
                _saveConfig();
            }

            ImGui.TextColored(Theme.Colors.TextTertiary, "Combat Recorder 新日志会保存到这个目录，而不是 C 盘 AppData。");
        });

        // ── 触发器目录同步 ──
        CL.Card("触发器目录同步", () =>
        {
            var ghToken = _config.GitHubToken ?? "";
            var ghRepo = _config.CatalogRepo ?? "";
            var ghBranch = _config.CatalogBranch ?? "";
            var changed = false;

            CL.FormRow("GitHub Token", () =>
            {
                ImGui.SetNextItemWidth(220);
                changed |= ImGui.InputTextWithHint("##gh_token", "ghp_... (repo 权限)", ref ghToken, 128, ImGuiInputTextFlags.Password);
            });

            CL.FormRow("仓库", () =>
            {
                ImGui.SetNextItemWidth(220);
                changed |= ImGui.InputText("##gh_repo", ref ghRepo, 128);
            });

            CL.FormRow("分支", () =>
            {
                ImGui.SetNextItemWidth(150);
                changed |= ImGui.InputText("##gh_branch", ref ghBranch, 64);
            });

            if (changed)
            {
                _config.GitHubToken = ghToken.Length > 0 ? ghToken : null;
                _config.CatalogRepo = ghRepo;
                _config.CatalogBranch = ghBranch;
                _saveConfig();
            }
        });

        // ── 外部悬浮窗 ──
        CL.Card("外部悬浮窗", () =>
        {
            var overlays = _config.Overlays;
            if (overlays is null or { Length: 0 })
            {
                ImGui.TextColored(Theme.Colors.TextTertiary, "暂无外部悬浮窗配置。");
                return;
            }

            for (int i = 0; i < overlays.Length; i++)
            {
                var ol = overlays[i];
                var overlayChanged = false;

                ImGui.PushID(i);

                CL.Collapsible(ol.Name, () =>
                {
                    var vis = ol.Visible;
                    if (CL.Switch($"ol_vis_{i}", "启用", ref vis))
                    {
                        ol.Visible = vis;
                        overlayChanged = true;
                    }

                    var url = ol.Url;
                    CL.FormRow("URL", () =>
                    {
                        ImGui.SetNextItemWidth(240);
                        if (ImGui.InputText("##ol_url", ref url, 256))
                        {
                            ol.Url = url;
                            overlayChanged = true;
                        }
                    });

                    var w = ol.Width;
                    var h = ol.Height;
                    if (CL.InputNumber($"ol_w_{i}", "宽", ref w, 10))
                    {
                        ol.Width = w;
                        overlayChanged = true;
                    }
                    ImGui.SameLine();
                    if (CL.InputNumber($"ol_h_{i}", "高", ref h, 10))
                    {
                        ol.Height = h;
                        overlayChanged = true;
                    }

                    var zoom = ol.Zoom;
                    if (CL.Slider($"ol_zoom_{i}", "缩放 %", ref zoom, 50f, 200f, "%.0f%%"))
                    {
                        ol.Zoom = zoom;
                        overlayChanged = true;
                    }

                    var locked = ol.Locked;
                    if (CL.Switch($"ol_lock_{i}", "锁定窗口", ref locked))
                    {
                        ol.Locked = locked;
                        overlayChanged = true;
                    }
                });

                ImGui.PopID();

                if (overlayChanged)
                {
                    Plugin.Instance._uiManager?.BrowsingwayIpc?.CreateOrUpdateOverlay(ol);
                    _saveConfig();
                }
            }
        });
    }
}
