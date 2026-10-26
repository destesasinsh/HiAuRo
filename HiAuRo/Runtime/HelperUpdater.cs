using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;

namespace HiAuRo.Runtime;

/// <summary>
/// Helper DLL 自动更新 — 从 GitHub Release 拉取最新 HiAuRo.Helper.dll
/// </summary>
public static class HelperUpdater
{
    private const string RepoOwner = "destesasinsh";
    private const string RepoName = "HiAuRo.Helper";
    private const string DllName = "HiAuRo.Helper.dll";
    private const string LocalDevMarkerName = "HiAuRo.Helper.localdev";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "User-Agent", "HiAuRo" } }
    };

    private static string StoreDir => Path.Combine(
        DService.Instance().PI.ConfigDirectory.FullName, "Helper");

    private static string LocalDevMarkerPath => Path.Combine(StoreDir, LocalDevMarkerName);

    private static HelperAssemblySnapshot? _helperSnapshot;
    private static readonly object _snapshotLock = new();
    private static volatile bool _acceptLoads;
    private static volatile bool _loaded;

    internal sealed record HelperAssemblySnapshot(
        AssemblyLoadContext LoadContext,
        Assembly Assembly,
        byte[] AssemblyBytes);

    /// <summary>Helper DLL 是否已加载</summary>
    public static bool Loaded => _loaded;

    /// <summary>HelperUpdater 已加载的 HiAuRo.Helper 程序集（供 ACRLoader 共享，避免 ALC 隔离）</summary>
    public static Assembly? HelperAssembly => HelperSnapshot?.Assembly;

    internal static byte[]? HelperAssemblyBytes => HelperSnapshot?.AssemblyBytes;

    internal static HelperAssemblySnapshot? HelperSnapshot => Volatile.Read(ref _helperSnapshot);

    internal static void Initialize()
    {
        lock (_snapshotLock)
            _acceptLoads = true;
    }

    /// <summary>尝试从本地缓存建立当前宿主生命周期唯一的 Helper 实例。</summary>
    public static bool TryLoadLocalSync()
    {
        if (HelperAssembly != null) return true;

        var localDll = Path.Combine(StoreDir, DllName);
        if (!File.Exists(localDll)) return false;

        try
        {
            LoadDll(localDll);
            DService.Instance().Log.Information($"[HelperUpdater] 从本地缓存同步加载: {localDll}");
            return true;
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[HelperUpdater] 本地缓存加载失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>检查更新；当前会话已有 Helper 时只更新下次重载使用的缓存。</summary>
    public static async Task CheckAndUpdateAsync(CancellationToken cancellationToken = default)
    {
        var localDll = Path.Combine(StoreDir, DllName);
        if (ShouldUseLocalDevHelper(localDll))
        {
            DService.Instance().Log.Information("[HelperUpdater] 检测到本地开发版 Helper，跳过在线更新");
            LoadDll(localDll);
            return;
        }

        try
        {
            // 直接下载 latest release DLL，无需调用 GitHub API（避免 403 rate limit）
            var downloaded = await DownloadLatestDll(localDll, cancellationToken).ConfigureAwait(false);
            if (downloaded)
            {
                if (HelperSnapshot is null)
                {
                    LoadDll(localDll);
                    DService.Instance().Log.Information($"[HelperUpdater] 已更新并加载 {RepoName}");
                }
                else
                {
                    DService.Instance().Log.Information(
                        $"[HelperUpdater] 已更新 {RepoName} 缓存，下次 HiAuRo 重载时生效");
                }
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning($"[HelperUpdater] 下载失败: {ex.Message}");
        }

        // 下载失败 → 尝试本地缓存
        if (File.Exists(localDll))
        {
            DService.Instance().Log.Information("[HelperUpdater] 从本地缓存加载");
            LoadDll(localDll);
        }
        else
        {
            DService.Instance().Log.Warning("[HelperUpdater] 无本地缓存，跳过 Helper 加载");
        }
    }

    /// <summary>判断当前缓存是否为本地构建的 Helper，避免开发调试时被在线版本覆盖。</summary>
    private static bool ShouldUseLocalDevHelper(string localDll)
    {
        if (!File.Exists(localDll) || !File.Exists(LocalDevMarkerPath))
            return false;

        var hostPath = typeof(HelperUpdater).Assembly.Location;
        if (string.IsNullOrWhiteSpace(hostPath) || !File.Exists(hostPath))
            return true;

        var helperTime = File.GetLastWriteTimeUtc(localDll);
        var hostTime = File.GetLastWriteTimeUtc(hostPath);
        return helperTime >= hostTime;
    }

    /// <summary>下载 latest release DLL（直接链接，不走 API，不受 rate limit 限制）。</summary>
    private static async Task<bool> DownloadLatestDll(
        string destPath,
        CancellationToken cancellationToken)
    {
        // GitHub 支持 /releases/latest/download/ 直接重定向到最新 release 的 asset
        // 格式: https://github.com/{owner}/{repo}/releases/latest/download/{filename}
        var url = $"https://github.com/{RepoOwner}/{RepoName}/releases/latest/download/{DllName}";
        using var req = new HttpRequestMessage(HttpMethod.Head, url); // HEAD 先探测
        using var headResp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!headResp.IsSuccessStatusCode) return false;

        using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return false;

        Directory.CreateDirectory(StoreDir);
        var tempPath = destPath + ".download";
        try
        {
            await using (var fs = File.Create(tempPath))
                await resp.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, destPath, overwrite: true);
            return true;
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch { }
        }
    }

    private static void LoadDll(string dllPath)
    {
        if (!_acceptLoads || HelperSnapshot is not null || !File.Exists(dllPath)) return;

        var assemblyBytes = File.ReadAllBytes(dllPath);
        var bindings = SharedAssemblyResolver.CaptureHost();
        var loadContext = new AssemblyLoadContext("HiAuRo.Helper", isCollectible: true);
        loadContext.Resolving += (_, name) =>
            bindings.IsHost(name.Name) ? bindings.Resolve(name) : null;

        Assembly assembly;
        try
        {
            using var ms = new MemoryStream(assemblyBytes, writable: false);
            assembly = loadContext.LoadFromStream(ms);
            if (!string.Equals(
                    assembly.GetName().Name,
                    RepoName,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Helper 程序集名称必须为 HiAuRo.Helper");
            SharedAssemblyResolver.PreloadDependencies(
                loadContext,
                assembly,
                bindings,
                _ => null,
                "Helper",
                allowedHostRootName: RepoName);

            if (!_acceptLoads)
                throw new OperationCanceledException("HelperUpdater 正在关闭");
        }
        catch
        {
            loadContext.Unload();
            throw;
        }

        lock (_snapshotLock)
        {
            if (_acceptLoads && _helperSnapshot is null)
            {
                Volatile.Write(
                    ref _helperSnapshot,
                    new HelperAssemblySnapshot(loadContext, assembly, assemblyBytes));
                _loaded = true;
                return;
            }
        }

        loadContext.Unload();
    }

    internal static void Shutdown()
    {
        HelperAssemblySnapshot? snapshot;
        lock (_snapshotLock)
        {
            _acceptLoads = false;
            snapshot = _helperSnapshot;
            Volatile.Write(ref _helperSnapshot, null);
            _loaded = false;
        }

        snapshot?.LoadContext.Unload();
    }
}
