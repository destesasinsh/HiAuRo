namespace HiAuRo.UI;

public static class EditorLaunchHelper
{
    public const string HostedToolsUrl = "https://destesasinsh.github.io/HiAuRo.WebTools/";

    public static string BuildWebToolsUrl()
    {
        // ponytail: 主项目已不再内置编辑器/阅读器静态页，统一跳托管 WebTools。
        return HostedToolsUrl;
    }

    public static System.Diagnostics.ProcessStartInfo Build(string url)
    {
        return new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        };
    }
}
