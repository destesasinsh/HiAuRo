using System.Linq;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using HiAuRo.Command;
using HiAuRo.Execution;
using HiAuRo.Script;
using HiAuRo.Execution.Events;
using HiAuRo.Infrastructure;
using HiAuRo.Runtime;
using HiAuRo.Setting;
using HiAuRo.UI;
using HiAuRo.Decision;
using HiAuRo.Recording;
using HiAuRo.Rendering;
using HiAuRo.Vfx;
using OmenTools;
using HiAuRo.ImGuiLib;
using HiAuRo.Runtime.Movement;
using HiAuRo.Runtime.Intelligence;

namespace HiAuRo;

/// <summary>HiAuRo 插件入口</summary>
public partial class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly PluginConfig _config;
    internal WebUiBridge? _uiBridge;
    internal IDalamudPluginInterface PluginInterface => _pluginInterface;
    internal UIManager? _uiManager;

    /// <summary>当前是否处于 WebUI 模式（用于 ACRLifecycle 等判断状态推送通道）</summary>
    public static bool IsWebUI => Instance?._uiManager?.IsWebUI ?? false;
    private readonly MainWindow _mainWindow;
    private readonly WindowSystem _windowSystem;
    private readonly VfxRenderer? _vfxRenderer;
    private readonly Action _windowDrawAction;
    private readonly RemoteMoveIpcProvider _remoteMoveIpc = new();
    private readonly CancellationTokenSource _helperUpdateCts = new();
    private Task _helperUpdateTask = Task.CompletedTask;
    private Task _helperCompletionTask = Task.CompletedTask;

    /// <summary>插件实例单例</summary>
    public static Plugin Instance { get; private set; } = null!;

    /// <summary>保存配置到磁盘</summary>
    public static void SaveConfig() => Instance?._config.Save();

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class</summary>
    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        try
        {
            Instance = this;
            _pluginInterface = pluginInterface;

            // OmenTools 15.0.3.x 起初始化改为异步（按帧预算分批初始化服务）。
            // Run/RunAsync 在 Framework 线程上是立即执行的，这里同步等待即可保持原构造流程不变。
            DService.InitAsync(pluginInterface).GetAwaiter().GetResult();
            SettingMgr.Init(_pluginInterface.ConfigDirectory.FullName);
            _config = LoadConfig();
            LogManager.Instance.Init(_pluginInterface.ConfigDirectory.FullName);
            Theme.Mode = _config.ImGuiThemeMode == ImGuiThemeMode.Dark ? Theme.ThemeMode.Dark : Theme.ThemeMode.Light;
            IconHelper.Init();
            HelperUpdater.Initialize();
            var helperLoadedAtStartup = HelperUpdater.TryLoadLocalSync();
            _helperUpdateTask = HelperUpdater.CheckAndUpdateAsync(_helperUpdateCts.Token);
            _helperCompletionTask = _helperUpdateTask.ContinueWith(t =>
            {
                if (!t.IsCanceled && !helperLoadedAtStartup && HelperUpdater.Loaded)
                    PluginLifecycle.RequestReload();
                if (DService.IsInitialized)
                    DService.Instance().Log.Information(
                        $"[Lifecycle] HelperUpdater 完成, Loaded={HelperUpdater.Loaded}");
            }, TaskContinuationOptions.ExecuteSynchronously);
            SafeFire(_helperCompletionTask, "HelperUpdater");
            DService.Instance().Log.Information("[Lifecycle] HelperUpdater 异步更新已启动");

            CommandMgr.Init();
            EventSystem.Init();
            GameEventHook.Instance.Init();
            CombatEnhancementManager.Instance.Init(_config);
            ShowIdService.Init();
            BuffInfoService.Init();
            ScriptEngine.Instance.Init();
            EncounterRecorder.Instance.Init();
            CombatRecorder.Instance.Init();
            ExecutionAxis.Instance.Init();

            // 注册 MovementDemand IPC（接收外部分发插件的推送）
            DService.Instance().PI.GetIpcProvider<string, object>("HiAuRo.AddMovementDemand")
                .RegisterAction(json =>
                {
                    try
                    {
                        var demand = System.Text.Json.JsonSerializer.Deserialize<MovementDemand>(json);
                        if (demand != null)
                            DemandBuffer.Add(demand);
                    }
                    catch (Exception ex)
                    {
                        DService.Instance().Log.Debug($"[IPC] AddMovementDemand 反序列化失败: {ex.Message}");
                    }
                });
            _remoteMoveIpc.Register();

            RuntimeCore.Start();
            DecisionEngine.Instance.Init();
            AssistAxis.Instance.Init();
            ModeSwitch.TryAutoSwitch();
            CombatContext.StateChanged += OnCombatStateChanged;

            ACR.HotkeyHelper.OnExecuted += OnHotkeyExecuted;
            ACR.QTHelper.OnChanged += OnQtChanged;

            _windowSystem = new WindowSystem("HiAuRo");
            _vfxRenderer = new VfxRenderer();
            DService.Instance().Log.Information($"[VFX] VfxRenderer 初始化完成, IsAvailable={VfxNative.IsAvailable}");
            _uiManager = new UIManager(_config, _pluginInterface, _windowSystem,
                () => _config.Save());
            _uiManager.Init();
            _uiBridge = _uiManager.Bridge;
            if (_uiBridge != null)
            {
                RegisterUiHandlers(_uiBridge);
            }

            _mainWindow = new MainWindow(_config, () => _config.Save());
            _windowSystem.AddWindow(_mainWindow);
#if DEBUG
            _windowDrawAction = () =>
            {
                var _uiTotal = System.Diagnostics.Stopwatch.GetTimestamp();
                var dt = ImGui.GetIO().DeltaTime;
                _vfxRenderer?.Update(dt);
                PositionalVfx.Update(dt);
                _uiManager?.SyncPanelVisibility();
                _windowSystem.Draw();
                Rendering.DebugPoint.Draw();
                Infrastructure.PerfMonitor.Record("UI.Total", _uiTotal);
            };
#else
            _windowDrawAction = () =>
            {
                var dt = ImGui.GetIO().DeltaTime;
                _vfxRenderer?.Update(dt);
                PositionalVfx.Update(dt);
                _uiManager?.SyncPanelVisibility();
                _windowSystem.Draw();
                Rendering.DebugPoint.Draw();
            };
#endif
            _pluginInterface.UiBuilder.Draw += _windowDrawAction;
            _pluginInterface.UiBuilder.OpenMainUi += () => _mainWindow.IsOpen = !_mainWindow.IsOpen;
            _pluginInterface.UiBuilder.OpenConfigUi += () => _mainWindow.IsOpen = !_mainWindow.IsOpen;

            ACRLifecycle.Init(_pluginInterface.ConfigDirectory.FullName);
            DService.Instance().Log.Information("[ACR] 开始扫描外部 ACR...");
            ACRLoader.LoadAll(_pluginInterface.ConfigDirectory.FullName);
            BasicAcrDevelopment.Init(_config,
                _pluginInterface.ConfigDirectory.FullName,
                _helperUpdateTask);
            DService.Instance().Log.Information("[ACR] 扫描完成, 等待职业切换触发加载");
            ACRLifecycle.ForceRecheck(); // RuntimeCore 可能先于 LoadAll 跑了第一帧，强制重检

            // 加载通用插件
            PluginLifecycle.Init(_pluginInterface.AssemblyLocation.Directory?.FullName ?? ".",
                _pluginInterface.ConfigDirectory.FullName);

            PluginWindowManager.Init(_windowSystem);

            try
            {
                TriggerCatalogExporter.RebuildRuntimeTypeRegistry();
                TriggerCatalogExporter.ExportToConfigDirectory(_pluginInterface.ConfigDirectory.FullName);
            }
            catch (Exception ex)
            {
                DService.Instance().Log.Warning($"[TriggerCatalog] 生成失败: {ex.Message}");
            }

            var modeText = _config.UIMode == Infrastructure.UIMode.WebUI ? "WebUI" : "ImGui";
            DService.Instance().Chat.Print($"[HiAuRo] /hi on|off|toggle|status|panel|reload  UI模式: {modeText}");
            DService.Instance().Log.Information($"[Lifecycle] HiAuRo 初始化完成。版本: {_config.LastSeenPluginVersion}  模式: {modeText}");
            DService.Instance().Log.Information($"[Lifecycle] 状态: Mode={modeText} ACR={ACRLifecycle.CurrentAcrName}");
        }
        catch (Exception ex)
        {
            try
            {
                DService.Instance().Log.Error($"[Lifecycle] 插件构造函数异常，尝试释放已分配资源: {ex}");
                Dispose();
            }
            catch (Exception disposeEx)
            {
                DService.Instance().Log.Error($"[Lifecycle] 释放资源时再次异常: {disposeEx}");
            }
            throw;
        }
    }

    private static void OnCombatStateChanged(CombatContext.State oldState, CombatContext.State newState)
    {
        if (newState is CombatContext.State.OutOfCombat or CombatContext.State.InCombat)
            ModeSwitch.TryAutoSwitch();
    }

    internal async Task UploadCatalogAsync()
    {
        var config = _config;
        if (string.IsNullOrWhiteSpace(config.GitHubToken))
        {
            DService.Instance().Chat.Print("[HiAuRo] 请先在设置中配置 GitHubToken");
            return;
        }

        var catalogPath = Path.Combine(_pluginInterface.ConfigDirectory.FullName, "trigger-catalog.json");
        if (!File.Exists(catalogPath))
        {
            DService.Instance().Chat.Print("[HiAuRo] 目录未生成");
            return;
        }

        var json = await File.ReadAllTextAsync(catalogPath);
        var result = await CatalogSync.UploadToGitHubAsync(
            json, config.CatalogRepo, config.CatalogBranch, config.GitHubToken, onlyCloudSync: true);

        DService.Instance().Chat.Print(result.Success
            ? $"[HiAuRo] 目录已上传 → {result.CommitUrl}"
            : $"[HiAuRo] 上传失败: {result.Message}");
    }

    /// <summary>释放插件资源</summary>
    public void Dispose()
    {
        _helperUpdateCts.Cancel();
        try { _helperCompletionTask.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch { }
        CombatContext.StateChanged -= OnCombatStateChanged;
        ACR.HotkeyHelper.OnExecuted -= OnHotkeyExecuted;
        ACR.QTHelper.OnChanged -= OnQtChanged;

        RuntimeCore.Shutdown();
        CombatContext.Reset();
        ExecutionAxis.Instance.Shutdown();
        AssistAxis.Instance.Shutdown();
        CombatRecorder.Instance.Shutdown();
        EncounterRecorder.Instance.Shutdown();
        ScriptEngine.Instance.Shutdown();
        ScriptManager.Shutdown();
        CombatEnhancementManager.Instance.Shutdown();
        GameEventHook.Instance.Shutdown();
        EventSystem.Shutdown();
        CommandMgr.Shutdown();

        PluginWindowManager.Shutdown();
        PluginLifecycle.Shutdown();

        // 先关 ACR（UnloadRotation 依赖 Plugin.Instance）
        ACRLifecycle.Shutdown();
        BasicAcrDevelopment.Shutdown();

        // 注销 IPC
        try { DService.Instance().PI.GetIpcProvider<string, object>("HiAuRo.AddMovementDemand").UnregisterAction(); } catch { }
        _remoteMoveIpc.Dispose();

        if (_windowSystem != null)
        {
            _pluginInterface.UiBuilder.Draw -= _windowDrawAction;
            _uiManager?.Dispose();
            _windowSystem.RemoveAllWindows();
        }
        _vfxRenderer?.Dispose();
        BuffInfoService.Shutdown();
        Instance = null!;
        PluginConfig.Instance = null!;

        // 清除静态缓存（避免下次加载残留）
        ACRLoader.UnloadAll();
        HelperUpdater.Shutdown();
        _helperUpdateCts.Dispose();
        Execution.ExecutionJsonLoader.Clear();
        Execution.ScriptCompiler.ClearCache();
        Decision.DecisionSkillRegistry.Clear();
        ACR.HotkeyPoller.Clear();
        ACR.SpellHistoryHelper.Reset();

        LogManager.Instance.Dispose();
        DService.UninitAsync().GetAwaiter().GetResult();
    }

    /// <summary>显示组件展示窗口</summary>
    public void ShowDemoWindow()
    {
        _uiManager?.ShowDemoWindow();
    }

    /// <summary>切换主窗口显隐</summary>
    public void ToggleMainWindow()
    {
        _mainWindow.IsOpen = !_mainWindow.IsOpen;
    }

    public void OpenMainWindowRoute(string routeKey)
    {
        _mainWindow.OpenRoute(routeKey);
    }

    #region 日志

    /// <summary>输出调试日志（受 DebugEnabled 开关控制）</summary>
    public void LogDebug(string message)
    {
        if (_config.DebugEnabled)
            DService.Instance().Log.Debug($"[Debug] {message}");
    }

    /// <summary>输出格式化调试日志（受 DebugEnabled 开关控制）</summary>
    public void LogDebug(string messageTemplate, params object[] args)
    {
        if (_config.DebugEnabled)
            DService.Instance().Log.Debug($"[Debug] {string.Format(messageTemplate, args)}");
    }

    #endregion

    #region 工具
    #endregion
}
