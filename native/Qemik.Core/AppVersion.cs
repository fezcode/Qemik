using System.Reflection;

namespace Qemik.Core;

public static class AppVersion
{
    public static string Current { get; } = Read();
    public static string Display { get; } = $"Qemik {Current}";
    public static string UserAgent { get; } = $"Qemik/{Current}";
    private static string Read()
    {
        var informational = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var metadata = informational.IndexOf('+');
            return metadata < 0 ? informational : informational[..metadata];
        }
        var assembly = typeof(AppVersion).Assembly.GetName().Version;
        return assembly == null ? "0.0.0" : $"{assembly.Major}.{assembly.Minor}.{assembly.Build}";
    }
}
