using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmpTray.Logging;

public enum LogLevel { Debug = 0, Info = 1, Warning = 2, Error = 3 }

/// <summary>One line of the event log: when, how serious, which part of the app, what happened.</summary>
public sealed record LogEvent(DateTimeOffset Time, LogLevel Level, string Source, string Event, string Message, IReadOnlyDictionary<string, string>? Data)
{
    private sealed class Line
    {
        [JsonPropertyName("ts")] public string Ts { get; set; } = "";
        [JsonPropertyName("level")] public string Level { get; set; } = "";
        [JsonPropertyName("src")] public string Src { get; set; } = "";
        [JsonPropertyName("event")] public string Event { get; set; } = "";
        [JsonPropertyName("msg")] public string Msg { get; set; } = "";
        [JsonPropertyName("data")] public Dictionary<string, string>? Data { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One JSON object per line, so the file stays greppable and each line parses on its own.</summary>
    public string ToJson() => JsonSerializer.Serialize(new Line
    {
        Ts = Time.ToString("o", CultureInfo.InvariantCulture),
        Level = Level.ToString(),
        Src = Source,
        Event = Event,
        Msg = Message,
        Data = Data == null || Data.Count == 0 ? null : new Dictionary<string, string>(Data),
    }, Json);

    /// <summary>Parses one line; null for anything that is not an event (older plain-text lines, a torn write).</summary>
    public static LogEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return null;
        try
        {
            var l = JsonSerializer.Deserialize<Line>(line, Json);
            if (l == null || !DateTimeOffset.TryParse(l.Ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)) return null;
            var level = Enum.TryParse<LogLevel>(l.Level, ignoreCase: true, out var lv) ? lv : LogLevel.Info;
            return new LogEvent(ts, level, l.Src, l.Event, l.Msg, l.Data);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Append-only event log with a hard size cap. Lines are JSON objects. When the file would grow past
/// <see cref="MaxBytes"/> the oldest lines are dropped until about half the cap remains, so the file
/// never exceeds 2 MB and the newest events are always kept.
/// </summary>
public sealed class EventLog
{
    public const long MaxBytes = 2 * 1024 * 1024;
    public const long TrimToBytes = MaxBytes / 2;
    private const int MaxDetailChars = 4000;

    private readonly object _gate = new();
    private long _length = -1;

    public string Path { get; }

    /// <summary>Raised after a line is written; the Logs page uses it to refresh promptly.</summary>
    public event Action? Written;

    public EventLog(string path) => Path = path;

    public void Write(LogLevel level, string source, string @event, string message, params (string Key, object? Value)[] data) =>
        Write(new LogEvent(DateTimeOffset.Now, level, source, @event, message, ToData(data, null)));

    public void Write(LogLevel level, string source, string @event, string message, Exception? exception, params (string Key, object? Value)[] data) =>
        Write(new LogEvent(DateTimeOffset.Now, level, source, @event, message, ToData(data, exception)));

    public void Write(LogEvent e)
    {
        string line = e.ToJson() + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (_length < 0) _length = File.Exists(Path) ? new FileInfo(Path).Length : 0;
                if (_length + bytes.Length > MaxBytes) Trim(TrimToBytes - bytes.Length);
                using var fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.Write(bytes, 0, bytes.Length);
                _length += bytes.Length;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // A log that cannot be written must never take the app down with it.
            return;
        }
        try { Written?.Invoke(); } catch { }
    }

    /// <summary>Keeps the newest whole lines that fit in <paramref name="keepBytes"/>; called under the lock.</summary>
    private void Trim(long keepBytes)
    {
        byte[] all;
        try { all = File.ReadAllBytes(Path); }
        catch (FileNotFoundException) { _length = 0; return; }

        int start = (int)Math.Max(0, all.Length - Math.Max(0, keepBytes));
        // Move forward to the first line boundary so no half line survives.
        while (start > 0 && start < all.Length && all[start - 1] != (byte)'\n') start++;
        if (start >= all.Length) start = all.Length;

        string tmp = Path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            fs.Write(all, start, all.Length - start);
        File.Move(tmp, Path, overwrite: true);
        _length = all.Length - start;
    }

    /// <summary>Everything in the file in order, skipping lines that are not events.</summary>
    public static IReadOnlyList<LogEvent> Read(string path)
    {
        var reader = new EventLogReader(path);
        reader.Poll(out var events);
        return events;
    }

    /// <summary>Removes every event; the next write starts the file again.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            try { File.Delete(Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _length = 0;
        }
        try { Written?.Invoke(); } catch { }
    }

    private static IReadOnlyDictionary<string, string>? ToData((string Key, object? Value)[] data, Exception? exception)
    {
        if (data.Length == 0 && exception == null) return null;
        var d = new Dictionary<string, string>();
        foreach (var (key, value) in data)
            if (value != null) d[key] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (exception != null)
        {
            d["exception"] = exception.GetType().FullName ?? exception.GetType().Name;
            string detail = exception.ToString();
            d["detail"] = detail.Length > MaxDetailChars ? detail[..MaxDetailChars] + "…" : detail;
        }
        return d;
    }
}

/// <summary>
/// Reads an event log incrementally: each <see cref="Poll"/> returns only the lines appended since the
/// last call, or everything again after the file was trimmed, cleared or replaced.
/// </summary>
public sealed class EventLogReader
{
    private readonly string _path;
    private long _position;
    private readonly StringBuilder _partial = new();

    public EventLogReader(string path) => _path = path;

    /// <summary>True when the caller must discard what it had and take <paramref name="events"/> as the whole log.</summary>
    public bool Poll(out List<LogEvent> events)
    {
        events = new List<LogEvent>();
        bool restart = false;
        try
        {
            if (!File.Exists(_path))
            {
                restart = _position > 0;
                _position = 0;
                _partial.Clear();
                return restart;
            }
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _position)
            {
                restart = true;
                _position = 0;
                _partial.Clear();
            }
            if (fs.Length == _position) return false;
            fs.Position = _position;
            using var reader = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
            string text = _partial + reader.ReadToEnd();
            _partial.Clear();
            _position = fs.Length;

            int lineStart = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                var e = LogEvent.Parse(text.AsSpan(lineStart, i - lineStart).Trim().ToString());
                if (e != null) events.Add(e);
                lineStart = i + 1;
            }
            // A line without its newline yet is a write in progress: keep it for the next poll.
            if (lineStart < text.Length) _partial.Append(text, lineStart, text.Length - lineStart);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return restart;
    }
}

/// <summary>The Logs page filter, kept as a plain function so it can be tested.</summary>
public static class LogFilter
{
    public static bool Matches(LogEvent e, LogLevel minLevel, string? source, string? text)
    {
        if (e.Level < minLevel) return false;
        if (!string.IsNullOrEmpty(source) && !string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(text)) return true;
        string needle = text.Trim();
        if (e.Message.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        if (e.Event.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        if (e.Source.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return e.Data != null && e.Data.Any(kv => kv.Value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}
