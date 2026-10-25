namespace ScreenTime.Tracker;

/// <summary>Append-only error log, rolled over to .old at about 1 MB (N6). Never throws.</summary>
internal sealed class TrackerLog(string path)
{
    private const long MaxBytes = 1024 * 1024;

    public void Write(string message)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length > MaxBytes)
                File.Move(path, path + ".old", overwrite: true);

            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Write(Exception exception) => Write(exception.ToString());
}
