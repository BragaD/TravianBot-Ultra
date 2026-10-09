namespace TbotUltra.Mac;

public sealed record LogLine(long Id, DateTimeOffset At, string Text);

/// <summary>In-memory ring of recent log lines for the UI, mirrored to a daily file for diagnostics.</summary>
public sealed class LogBuffer
{
    private const int Capacity = 4000;
    private readonly object _sync = new();
    private readonly LinkedList<LogLine> _lines = new();
    private readonly string _logDirectory;
    private long _nextId = 1;

    public LogBuffer(string logDirectory)
    {
        _logDirectory = logDirectory;
    }

    public bool ShowVerbose { get; set; }

    public void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var verbose = text.Contains(":verbose]", StringComparison.OrdinalIgnoreCase);
        lock (_sync)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(_logDirectory, $"tbot-{now:yyyy-MM-dd}.log"),
                    $"{now:HH:mm:ss} {text}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // The on-disk log is best effort; the UI buffer below is the primary sink.
            }

            if (verbose && !ShowVerbose)
            {
                return;
            }

            _lines.AddLast(new LogLine(_nextId++, now, text));
            while (_lines.Count > Capacity)
            {
                _lines.RemoveFirst();
            }
        }
    }

    public IReadOnlyList<LogLine> ReadAfter(long afterId)
    {
        lock (_sync)
        {
            return _lines.Where(line => line.Id > afterId).ToList();
        }
    }
}
