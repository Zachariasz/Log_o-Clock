using Microsoft.Win32;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Services;

public sealed class AutostartService : IAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LogOClock";
    private const string LegacyValueName = "ProjectTimeTracker";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return (key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value)) ||
                   (key?.GetValue(LegacyValueName) is string legacyValue && !string.IsNullOrWhiteSpace(legacyValue));
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unavailable.");
        key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }
}
