using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using CodexBar.Core;
using Microsoft.Win32;

namespace CodexBar.App;

/// <summary>Encrypts secrets with DPAPI for the current Windows user; other accounts and PCs cannot read them.</summary>
internal sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexBar.Windows.v1");

    public string Protect(string plainText) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string protectedText)
    {
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>Start-at-login through the per-user Run key. No admin rights needed.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexBar";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } path)
        {
            key.SetValue(ValueName, $"\"{path}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

internal static class Shell
{
    public static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
    }
}

internal static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Light or dark for app windows.</summary>
    public static bool AppsUseDark => ReadFlag("AppsUseLightTheme") == 0;

    /// <summary>Light or dark for the taskbar, which decides the tray icon's track color.</summary>
    public static bool TaskbarIsDark => ReadFlag("SystemUsesLightTheme") != 1;

    public static void Apply(ResourceDictionary resources, bool dark)
    {
        Set(resources, "Bg", dark ? "#FF202124" : "#FFF7F7F8");
        Set(resources, "Card", dark ? "#FF2B2C30" : "#FFFFFFFF");
        Set(resources, "Fg", dark ? "#FFF2F2F3" : "#FF1B1C1F");
        Set(resources, "FgDim", dark ? "#FFA3A6AD" : "#FF5F636B");
        Set(resources, "Line", dark ? "#FF3A3C42" : "#FFDCDDE1");
        Set(resources, "Track", dark ? "#FF3A3C42" : "#FFE4E5E9");
        Set(resources, "Accent", dark ? "#FF16D3B4" : "#FF0B8F7A");
        Set(resources, "Warn", dark ? "#FFF0A53A" : "#FFB86A00");
        Set(resources, "Danger", dark ? "#FFEF5B5B" : "#FFC62828");
    }

    private static void Set(ResourceDictionary resources, string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        resources[key] = brush;
    }

    private static int? ReadFlag(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(name) as int?;
    }
}
