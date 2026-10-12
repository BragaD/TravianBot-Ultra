using System.Text.Json;

namespace TbotUltra.Mac;

/// <summary>
/// The hand-off point for AI agents: a folder under the data root with where to connect, the guide,
/// a small command-line helper, and a state snapshot refreshed while the app runs.
/// </summary>
public sealed class AgentFiles
{
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(15);

    private readonly string _directory;
    private readonly string _address;
    private readonly string _token;
    private readonly Func<object> _summary;
    private readonly CancellationTokenSource _stop = new();

    public AgentFiles(string root, string address, string token, Func<object> summary)
    {
        _directory = Path.Combine(root, "agent");
        _address = address.TrimEnd('/');
        _token = token;
        _summary = summary;
    }

    private string ConnectionPath => Path.Combine(_directory, "connection.json");

    public void Start()
    {
        Directory.CreateDirectory(_directory);
        WriteOwnerOnly(ConnectionPath, JsonSerializer.Serialize(
            new
            {
                url = _address,
                token = _token,
                tokenHeader = "X-Tbot-Token",
                pid = Environment.ProcessId,
                startedAtUtc = DateTimeOffset.UtcNow,
                guide = Path.Combine(_directory, "AGENT_GUIDE.md"),
            },
            SettingsStore.Json));
        File.WriteAllText(Path.Combine(_directory, "AGENT_GUIDE.md"), MacPaths.ReadEmbeddedText("TbotUltra.Mac.Agent.guide.md"));

        var helperPath = Path.Combine(_directory, "tbot");
        File.WriteAllText(helperPath, MacPaths.ReadEmbeddedText("TbotUltra.Mac.Agent.tbot"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(helperPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var tempPath = Path.Combine(_directory, "state.json.tmp");
                    await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(_summary(), SettingsStore.Json), _stop.Token);
                    File.Move(tempPath, Path.Combine(_directory, "state.json"), overwrite: true);
                    await Task.Delay(SnapshotInterval, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    // The snapshot is a convenience; a failed write is retried on the next tick.
                    await Task.Delay(SnapshotInterval, CancellationToken.None);
                }
            }
        });
    }

    /// <summary>Removes the connection file so agents can tell the app is no longer running.</summary>
    public void Stop()
    {
        _stop.Cancel();
        try
        {
            File.Delete(ConnectionPath);
        }
        catch (IOException)
        {
            // A stale file is caught by its pid no longer being alive.
        }
    }

    // The token grants control of a logged-in game session: readable by the owner only.
    private static void WriteOwnerOnly(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
