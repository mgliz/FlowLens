using System.Reflection;

namespace FlowLens;

internal static class AppBuildInfo
{
    public static string Version => (typeof(AppBuildInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-preview")
        .Split('+')[0];
    public static bool IsTestBuild => Version.Contains('-');
}
