using System;
using System.IO;
using AmpTray.Service;

namespace AmpTray.Logging;

/// <summary>
/// The process-wide event log. The widget writes to amptray.log in its settings folder, the service
/// to service.log in ProgramData; both use the same line format so the Logs page can show them together.
/// </summary>
public static class AppLog
{
    private static EventLog? _log;

    public static string WidgetLogPath => Path.Combine(Settings.AppDataFolder, "amptray.log");
    public static string ServiceLogPath => ServiceSettings.LogFile;

    /// <summary>The log this process writes to; the widget log until <see cref="Configure"/> says otherwise.</summary>
    public static EventLog Current => _log ??= new EventLog(WidgetLogPath);

    public static void Configure(string path) => _log = new EventLog(path);

    public static void Info(string source, string @event, string message, params (string Key, object? Value)[] data) =>
        Current.Write(LogLevel.Info, source, @event, message, data);

    public static void Warn(string source, string @event, string message, params (string Key, object? Value)[] data) =>
        Current.Write(LogLevel.Warning, source, @event, message, data);

    public static void Warn(string source, string @event, string message, Exception? exception, params (string Key, object? Value)[] data) =>
        Current.Write(LogLevel.Warning, source, @event, message, exception, data);

    public static void Error(string source, string @event, string message, Exception? exception = null, params (string Key, object? Value)[] data) =>
        Current.Write(LogLevel.Error, source, @event, message, exception, data);
}
