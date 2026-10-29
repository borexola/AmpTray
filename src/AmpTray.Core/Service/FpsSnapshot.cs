using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AmpTray.Service;

public sealed record FpsEntry(int Pid, string Name, double Fps);

/// <summary>
/// Present rates per process, written by the background service twice a second and read by the widget.
/// A plain file in ProgramData keeps it simple across the session boundary (service runs in session 0).
/// </summary>
public sealed class FpsSnapshot
{
    public DateTime Timestamp { get; set; }
    public List<FpsEntry> Processes { get; set; } = new();

    public static string FilePath => Path.Combine(ServiceSettings.Folder, "fps.json");

    public bool IsStale => (DateTime.Now - Timestamp).TotalSeconds > 3;

    public void TrySave()
    {
        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Next tick writes again.
        }
    }

    /// <summary>Reads the latest snapshot; a read that collides with the service replacing the file is retried.</summary>
    public static FpsSnapshot? TryRead()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var s = JsonSerializer.Deserialize<FpsSnapshot>(fs);
                return s == null || s.IsStale ? null : s;
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(15);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }
}
