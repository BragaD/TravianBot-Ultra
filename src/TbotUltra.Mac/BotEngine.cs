using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Mac;

public sealed record FieldView(int? SlotId, string Type, string Name, int? Level);

public sealed record BuildingView(int? SlotId, string Name, int? Level, int? Gid);

public sealed record ConstructionView(string Name, int? Level, DateTimeOffset? FinishUtc);

public sealed record VillageView(
    string Key,
    string Name,
    string? Url,
    int? X,
    int? Y,
    bool? IsCapital,
    string Tribe,
    Dictionary<string, string> Resources,
    long? Warehouse,
    long? Granary,
    List<FieldView> Fields,
    List<BuildingView> Buildings,
    List<ConstructionView> Constructions,
    DateTimeOffset? UpdatedUtc);

public sealed record FarmListView(string Name, string? ListId, string? Village, int Active, int Total);

public sealed record HeroView(string State, int? Hp, int Adventures, HeroInventoryResources? Inventory, DateTimeOffset UpdatedUtc);

public sealed record JobView(string Key, string Title, DateTimeOffset DueUtc, string LastResult);

public sealed record ScanRow(int X, int Y, string Kind, string Detail, double? Distance, bool IsOasis);

public sealed record ScanView(string Title, string Status, bool Running, List<ScanRow> Rows);

public sealed record CatapultSetupView(Dictionary<string, long> AvailableTroops, int? RallyPointLevel, DateTimeOffset UpdatedUtc);

public sealed record CatapultPlan(
    string VillageKey,
    int X,
    int Y,
    int WaveCount,
    bool Raid,
    Dictionary<string, int> FirstAttackTroops,
    Dictionary<string, int> WaveTroops,
    string? Target1,
    string? Target2);

public sealed record PromptOption(string Value, string Label);

public sealed record PromptView(string Id, string Kind, string Message, List<PromptOption> Options);

/// <summary>
/// The Mac front-end's automation desk: owns the engine's <see cref="BotTaskRunner"/>, the login
/// session and one sequential loop that runs due jobs (construction, hero, farming, troops, ...).
/// </summary>
public sealed class BotEngine
{
    private static readonly TimeSpan MaxDeferral = TimeSpan.FromHours(6);

    // Not an engine task: the loop runs the two inbox calls itself.
    private const string MarkReadTask = "mark_inbox_read";

    private readonly object _sync = new();
    private readonly string _root;
    private readonly AccountStore _accounts;
    private readonly SettingsStore _settings;
    private readonly LogBuffer _log;
    private readonly BotTaskRunner _runner;
    private readonly SemaphoreSlim _manualGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _due = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastResult = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, VillageView> _villages = new(StringComparer.OrdinalIgnoreCase);
    private List<FarmListView> _farmLists = [];
    private HeroView? _hero;
    private string _tribe = "Unknown";
    private string _villagesAccount = string.Empty;
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private CancellationTokenSource? _manualCancellation;
    private DateTimeOffset? _sleepUntilUtc;
    private volatile bool _wakeRequested;
    private ScanView? _scan;
    private CatapultSetupView? _catapultSetup;
    private PromptView? _prompt;
    private TaskCompletionSource<string?>? _promptAnswer;

    public BotEngine(string root, AccountStore accounts, SettingsStore settings, LogBuffer log)
    {
        _root = root;
        _accounts = accounts;
        _settings = settings;
        _log = log;
        _runner = new BotTaskRunner(new EnvAccountProvider(Path.Combine(root, ".env")), new ProjectContext(root))
        {
            LobbyWorldSelectionRequested = RequestLobbyWorldAsync,
            ManualLoginConfirmationRequested = RequestManualLoginConfirmationAsync,
            LobbyWorldServerResolved = (resolution, _) =>
            {
                _accounts.UpdateServer(resolution.AccountName, resolution.WorldName, resolution.ServerUrl);
                Log($"[login] world '{resolution.WorldName}' resolved to {resolution.ServerUrl}; account server updated.");
                return Task.CompletedTask;
            },
        };
    }

    public bool LoggedIn { get; private set; }

    public bool Running => _loopTask is { IsCompleted: false };

    public string Busy { get; private set; } = string.Empty;

    public string Activity { get; private set; } = "Not logged in";

    public string Alert { get; private set; } = string.Empty;

    /// <summary>Set while the loop has closed the browser for a pacing break.</summary>
    public DateTimeOffset? SleepingUntilUtc => _sleepUntilUtc;

    public void WakeNow() => _wakeRequested = true;

    public void ReleaseFarmTarget(string key)
    {
        var account = _accounts.ActiveAccountName();
        if (account.Length > 0)
        {
            _settings.Update(account, settings => settings.FarmRaids.OnHold.Remove(key));
        }
    }

    public string Tribe => _tribe;

    public PromptView? Prompt => _prompt;

    public HeroView? Hero => _hero;

    public ScanView? Scan => _scan;

    public CatapultSetupView? CatapultSetup => _catapultSetup;

    public IReadOnlyList<FarmListView> FarmLists => _farmLists;

    public IReadOnlyList<VillageView> Villages(string accountName)
    {
        lock (_sync)
        {
            EnsureVillagesLoaded(accountName);
            return _villages.Values.OrderBy(village => village.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public IReadOnlyList<JobView> Jobs()
    {
        var account = _accounts.ActiveAccountName();
        if (account.Length == 0)
        {
            return [];
        }

        var jobs = BuildJobs(_settings.Load(account), account);
        lock (_sync)
        {
            return jobs
                .Select(job => new JobView(job.Key, job.Title, DueAt(job.Key), _lastResult.GetValueOrDefault(job.Key, string.Empty)))
                .OrderBy(job => job.DueUtc)
                .ToList();
        }
    }

    public void ClearAlert() => Alert = string.Empty;

    public void AnswerPrompt(string id, string? value)
    {
        lock (_sync)
        {
            if (_prompt is null || !string.Equals(_prompt.Id, id, StringComparison.Ordinal))
            {
                return;
            }

            _prompt = null;
            _promptAnswer?.TrySetResult(value);
        }
    }

    public void RunJobNow(string key)
    {
        lock (_sync)
        {
            _due[key] = DateTimeOffset.UtcNow;
            _failures.Remove(key);
        }
    }

    // ----- Session ------------------------------------------------------------------------------

    public Task LoginAsync() => RunManualAsync("Logging in", LoginCoreAsync);

    private async Task LoginCoreAsync(CancellationToken cancellationToken)
    {
        var account = RequireActiveAccount();
        var options = BuildOptions(account, _settings.Load(account.Name));
        PostLoginSnapshot snapshot;
        try
        {
            try
            {
                snapshot = await _runner.ExecuteLoginAndLoadPostLoginSnapshotAsync(
                    options, Log, cancellationToken: cancellationToken, keepBrowserOpenAfterLogin: true);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("did not reach the configured game world", StringComparison.OrdinalIgnoreCase)
                && !LobbyWorldSelectionDefaults.IsChooseInLobby(options.ServerName))
            {
                // The saved URL matches none of the account's lobby worlds: let the user pick one.
                // The engine then stores the world's real URL on the account.
                Log("[mac] the saved server URL matched no world in the lobby; asking which world to play.");
                await _runner.ShutdownAsync(Log);
                snapshot = await _runner.ExecuteLoginAndLoadPostLoginSnapshotAsync(
                    options with { ServerName = LobbyWorldSelectionDefaults.ServerName },
                    Log,
                    cancellationToken: cancellationToken,
                    keepBrowserOpenAfterLogin: true);
                account = RequireActiveAccount();
            }
        }
        catch
        {
            // A canceled or failed login must not leave a half-authenticated browser window open.
            await _runner.ShutdownAsync(Log);
            throw;
        }

        LoggedIn = true;
        Activity = "Logged in";
        ApplyVillageStatus(account.Name, snapshot.VillageStatus, replaceMembership: true);
        ApplyHero(snapshot.VillageStatus.HeroStatus, snapshot.AdventureCount, snapshot.HeroInventory);
        if (snapshot.VillageStatus.FarmLists is { Count: > 0 } farmLists)
        {
            ApplyFarmLists(farmLists);
        }

        Log($"[mac] logged in as '{account.Name}' — tribe {_tribe}, {_villages.Count} village(s).");
    }

    /// <summary>Runs the startup choices: log in when the app opens, then start the bot.</summary>
    public async Task RunStartupAsync()
    {
        var account = _accounts.ActiveAccountName();
        if (account.Length == 0)
        {
            return;
        }

        var startup = _settings.Load(account).Startup;
        if (!startup.AutoLogin)
        {
            return;
        }

        Log("[mac] logging in automatically (startup setting).");
        await LoginAsync();
        if (LoggedIn && startup.AutoStart)
        {
            Start();
        }
    }

    public Task ScanVillagesAsync() => RunManualAsync("Scanning villages", async cancellationToken =>
    {
        var account = RequireActiveAccount();
        var options = BuildOptions(account, _settings.Load(account.Name));
        foreach (var village in Villages(account.Name))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Busy = $"Scanning {village.Name}";
            var status = await _runner.ReadVillageStatusAsync(
                options, Log, village.Name, village.Url, cancellationToken: cancellationToken);
            ApplyVillageStatus(account.Name, status, replaceMembership: false);
        }

        Log($"[mac] scanned {_villages.Count} village(s).");
    });

    public Task RefreshFarmListsAsync() => RunManualAsync("Reading farm lists", async cancellationToken =>
    {
        var account = RequireActiveAccount();
        var options = BuildOptions(account, _settings.Load(account.Name));
        ApplyFarmLists(await _runner.ReadFarmListsOverviewAsync(options, Log, cancellationToken: cancellationToken));
        Log($"[mac] found {_farmLists.Count} farm list(s).");
    });

    public Task CreateFarmListAsync(string name, string villageKey, string troopType, int troopCount) => RunManualAsync("Creating farm list", async cancellationToken =>
    {
        var account = RequireActiveAccount();
        var options = BuildOptions(account, _settings.Load(account.Name));
        var village = Villages(account.Name).FirstOrDefault(item => item.Key == villageKey)
            ?? throw new InvalidOperationException("Unknown village.");
        var villageId = Regex.Match(village.Url ?? string.Empty, @"newdid=(\d+)") is { Success: true } match ? match.Groups[1].Value : null;

        // Reading the lists first leaves the Farm List page fully rendered; creating straight after a
        // navigation can run before the page's Create button exists.
        ApplyFarmLists(await _runner.ReadFarmListsOverviewAsync(options, Log, cancellationToken: cancellationToken));
        var result = await _runner.CreateFarmListsAsync(
            options,
            new FarmListCreateRequest([name.Trim()], village.Name, villageId, troopType, Math.Max(1, troopCount)),
            Log,
            cancellationToken: cancellationToken);
        Log($"[mac] created {result.CreatedCount} farm list(s): {string.Join(", ", result.CreatedNames)}");
        ApplyFarmLists(await _runner.ReadFarmListsOverviewAsync(options, Log, cancellationToken: cancellationToken));
    });

    public Task AddFarmTargetsAsync(string listName, string troopType, int troopCount, IReadOnlyList<FarmCoordinate> coordinates) => RunManualAsync("Adding farm targets", async cancellationToken =>
    {
        var account = RequireActiveAccount();
        var options = BuildOptions(account, _settings.Load(account.Name));
        ApplyFarmLists(await _runner.ReadFarmListsOverviewAsync(options, Log, cancellationToken: cancellationToken));
        var result = await _runner.AddFarmsFromCoordinatesAsync(
            options,
            listName,
            troopType,
            Math.Max(1, troopCount),
            coordinates.Count,
            coordinates,
            useDefaultTroops: false,
            Log,
            cancellationToken: cancellationToken);
        Log($"[mac] farm targets for '{listName}': added {result.AddedCount}, already listed {result.AlreadyInListCount}, not found {result.NotFoundCount}, failed {result.FailedCount}.");
        ApplyFarmLists(await _runner.ReadFarmListsOverviewAsync(options, Log, cancellationToken: cancellationToken));
        if (result.FailedCount > 0)
        {
            throw new InvalidOperationException(
                $"The game did not accept {result.FailedCount} target(s) for '{listName}'. It can refuse small raids — try a larger troop count.");
        }
    });

    public Task MarkInboxReadAsync() => RunManualAsync("Marking messages and reports as read", async cancellationToken =>
    {
        var account = RequireActiveAccount();
        await MarkInboxReadCoreAsync(BuildOptions(account, _settings.Load(account.Name)), cancellationToken);
    });

    private async Task MarkInboxReadCoreAsync(BotOptions options, CancellationToken cancellationToken)
    {
        await _runner.MarkMessagesAsReadAsync(options, Log, cancellationToken: cancellationToken);
        await _runner.MarkReportsAsReadAsync(options, Log, cancellationToken: cancellationToken);
    }

    public Task RunAdventureBonusAsync(string kind) => RunManualAsync("Watching adventure bonus video", async cancellationToken =>
    {
        var account = RequireActiveAccount();
        // A manual "watch now" must not be skipped by the random chance gate used for automatic runs.
        var options = BuildOptions(account, _settings.Load(account.Name)) with { HeroAdventureVideoChancePercent = 100 };
        var result = kind == "hard"
            ? await _runner.RunIncreaseAdventuresToHardAsync(options, Log, cancellationToken: cancellationToken)
            : await _runner.RunReduceAdventuresTimeAsync(options, Log, cancellationToken: cancellationToken);
        Log($"[mac] adventure bonus ({kind}): {result}");
    });

    public Task RunProductionBonusAsync() => RunManualAsync("Activating +15% production", async cancellationToken =>
    {
        var account = RequireActiveAccount();
        var options = BuildOptions(account, _settings.Load(account.Name)) with { ProductionBonusVideoEnabled = true };
        var result = await _runner.RunActivateProductionBonusVideosAsync(options, Log, cancellationToken: cancellationToken);
        Log($"[mac] +15% production: {result.Message}");
    });

    public Task ScanOasesAsync(int centerX, int centerY, int radius, bool includeOccupied, IReadOnlyCollection<string> types) => RunManualAsync("Scanning oases", async cancellationToken =>
    {
        var account = RequireActiveAccount();
        var options = BuildOptions(account, _settings.Load(account.Name));
        var title = $"Oases within {radius} fields of ({centerX}|{centerY})";
        _scan = new ScanView(title, "Starting…", true, []);
        try
        {
            var progress = new Progress<MapOasisScanProgress>(update =>
                _scan = new ScanView(title, $"Map areas {update.CompletedAreas}/{update.TotalAreas}, {update.OasisCount} oases so far", true, []));
            var result = await _runner.ScanMapOasesAsync(
                options,
                includeOccupied,
                types,
                new MapOasisScanRequest(centerX, centerY, MapOasisScanScope.Radius, Math.Clamp(radius, 1, 200), MapOasisScanSpeed.Normal),
                Log,
                progress,
                cancellationToken: cancellationToken);
            var rows = result.Oases
                .Select(oasis => new ScanRow(
                    oasis.X,
                    oasis.Y,
                    oasis.OasisType,
                    oasis.IsOccupied ? $"occupied by {oasis.OwnerPlayer}" : string.IsNullOrWhiteSpace(oasis.Animals) ? "no animals" : oasis.Animals,
                    Math.Round(Math.Sqrt(Math.Pow(oasis.X - centerX, 2) + Math.Pow(oasis.Y - centerY, 2)), 1),
                    IsOasis: true))
                .OrderBy(row => row.Distance)
                .ToList();
            _scan = new ScanView(title, $"{rows.Count} oases found{(result.IsPartialResult ? " (partial result)" : string.Empty)}", false, rows);
        }
        catch
        {
            _scan = new ScanView(title, "Scan stopped.", false, _scan?.Rows ?? []);
            throw;
        }
    });

    public Task SearchTravcoAsync(int x, int y, int daysInactive, string orderBy) => RunManualAsync("Searching Travco for inactive villages", async cancellationToken =>
    {
        var account = RequireActiveAccount();
        var options = BuildOptions(account, _settings.Load(account.Name));
        var title = $"Inactive villages near ({x}|{y}), {daysInactive}+ days";
        _scan = new ScanView(title, "Opening Travco…", true, []);
        try
        {
            var searchProgress = new Progress<TravcoSearchProgress>(update =>
                _scan = new ScanView(title, update.Status, true, []));
            await _runner.OpenTravcoAndSearchAsync(options, new TravcoSearchRequest(x, y, daysInactive, orderBy), Log, searchProgress, cancellationToken);
            var pageProgress = new Progress<(int CurrentPage, int TotalPages)>(update =>
                _scan = new ScanView(title, $"Reading page {update.CurrentPage}/{update.TotalPages}", true, []));
            var result = await _runner.ScrapeAllTravcoPagesAsync(Log, pageProgress, cancellationToken);
            var rows = new List<ScanRow>();
            foreach (var row in result.Rows)
            {
                var coordinates = Regex.Match(row.Coordinates ?? string.Empty, @"(-?\d+)\D+(-?\d+)");
                if (coordinates.Success)
                {
                    rows.Add(new ScanRow(
                        int.Parse(coordinates.Groups[1].Value, CultureInfo.InvariantCulture),
                        int.Parse(coordinates.Groups[2].Value, CultureInfo.InvariantCulture),
                        row.Village,
                        $"{row.Account}, pop {row.Pop?.ToString(CultureInfo.InvariantCulture) ?? "?"}",
                        row.Distance,
                        IsOasis: false));
                }
            }

            _scan = new ScanView(title, $"{rows.Count} inactive villages found", false, rows);
        }
        catch
        {
            _scan = new ScanView(title, "Search stopped.", false, _scan?.Rows ?? []);
            throw;
        }
        finally
        {
            await _runner.CloseTravcoTabAsync(Log);
        }
    });

    public Task ReadCatapultSetupAsync(string villageKey) => RunManualAsync("Reading troops for catapult waves", async cancellationToken =>
    {
        var account = RequireActiveAccount();
        var setup = await _runner.ReadCatapultWaveSetupInfoAsync(
            BuildVillageOptions(account, villageKey), Log, forceRefresh: true, cancellationToken: cancellationToken);
        _catapultSetup = new CatapultSetupView(
            setup.AvailableTroops.ToDictionary(pair => pair.Key, pair => pair.Value), setup.RallyPointLevel, DateTimeOffset.UtcNow);
    });

    public Task StartCatapultWavesAsync(CatapultPlan plan) => RunManualAsync("Sending catapult waves", async cancellationToken =>
    {
        var account = RequireActiveAccount();
        var request = new CatapultWaveRequest(
            plan.X,
            plan.Y,
            Math.Clamp(plan.WaveCount, 0, CatapultWaveLimits.MaxWaveCount),
            plan.Raid,
            plan.FirstAttackTroops,
            plan.WaveTroops,
            string.IsNullOrWhiteSpace(plan.Target1) ? null : plan.Target1,
            string.IsNullOrWhiteSpace(plan.Target2) ? null : plan.Target2);
        var result = await _runner.StartCatapultWavesAsync(
            BuildVillageOptions(account, plan.VillageKey),
            request,
            Log,
            async (preparedCount, token) => await AskAsync(
                "confirm",
                $"{preparedCount} attack(s) are prepared against ({plan.X}|{plan.Y}). Send them now?",
                [new PromptOption("ok", "Send all"), new PromptOption(string.Empty, "Cancel")],
                token) == "ok",
            cancellationToken: cancellationToken);
        Log($"[mac] catapult waves: sent {result.SentCount}/{result.TotalAttacks}, failed {result.FailedCount}.");
    });

    private BotOptions BuildVillageOptions(AccountEntry account, string villageKey)
    {
        var village = Villages(account.Name).FirstOrDefault(item => item.Key == villageKey)
            ?? throw new InvalidOperationException("Unknown village.");
        return BuildOptions(account, _settings.Load(account.Name)) with
        {
            TargetVillageName = village.Name,
            TargetVillageUrl = village.Url ?? string.Empty,
        };
    }

    public Task RefreshHeroAsync() => RunManualAsync("Reading hero inventory", async cancellationToken =>
    {
        var account = RequireActiveAccount();
        var options = BuildOptions(account, _settings.Load(account.Name));
        var inventory = await _runner.ReadHeroInventoryResourcesAsync(options, Log, cancellationToken: cancellationToken);
        _hero = (_hero ?? new HeroView("Unknown", null, 0, null, DateTimeOffset.UtcNow)) with
        {
            Inventory = inventory,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
    });

    /// <summary>Saves the HTML of one game page under the data folder's diagnostics directory for bug reports.</summary>
    public Task SavePageHtmlAsync(string pagePath) => RunManualAsync("Saving page HTML", async cancellationToken =>
    {
        var account = RequireActiveAccount();
        var options = BuildOptions(account, _settings.Load(account.Name));
        var capture = await _runner.NavigateToPageAndReadHtmlAsync(options, pagePath, Log, cancellationToken: cancellationToken);
        var directory = Path.Combine(_root, "diagnostics");
        Directory.CreateDirectory(directory);
        var name = Regex.Replace(pagePath.Trim('/'), "[^A-Za-z0-9]+", "_");
        var filePath = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}.html");
        await File.WriteAllTextAsync(filePath, capture.Html, cancellationToken);
        Log($"[mac] saved {capture.Url} to {filePath}");
    });

    public Task SetLanguageToEnglishAsync() => RunManualAsync("Switching game language to English", async cancellationToken =>
    {
        var account = RequireActiveAccount();
        var options = BuildOptions(account, _settings.Load(account.Name));
        var language = await _runner.SetLanguageToEnglishAsync(options, Log, cancellationToken: cancellationToken);
        Log($"[mac] game language is now '{language ?? "unknown"}'.");
        Alert = string.Empty;
    });

    public async Task LogoutAsync()
    {
        await StopAsync();
        _manualCancellation?.Cancel();
        await _manualGate.WaitAsync();
        try
        {
            Busy = "Closing browser";
            await _runner.ShutdownAsync(Log);
            LoggedIn = false;
            Activity = "Not logged in";
        }
        finally
        {
            Busy = string.Empty;
            _manualGate.Release();
        }
    }

    public void CancelManual() => _manualCancellation?.Cancel();

    // ----- Loop ---------------------------------------------------------------------------------

    public void Start()
    {
        lock (_sync)
        {
            if (Running)
            {
                return;
            }

            if (!LoggedIn)
            {
                throw new InvalidOperationException("Log in first.");
            }

            Alert = string.Empty;
            _failures.Clear();
            _loopCancellation = new CancellationTokenSource();
            var cancellationToken = _loopCancellation.Token;
            _loopTask = Task.Run(() => RunLoopAsync(cancellationToken), CancellationToken.None);
        }

        Log("[mac] automation started.");
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_sync)
        {
            loop = _loopTask;
            _loopCancellation?.Cancel();
        }

        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // Expected when the loop is stopped.
            }

            Log("[mac] automation stopped.");
        }

        lock (_sync)
        {
            _loopTask = null;
        }

        if (LoggedIn)
        {
            Activity = "Logged in — automation stopped";
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var sessionEndsAt = NextSessionEnd(_settings.Load(RequireActiveAccount().Name));
            while (!cancellationToken.IsCancellationRequested)
            {
                var account = RequireActiveAccount();
                var settings = _settings.Load(account.Name);
                if (ResolveWakeTime(settings, sessionEndsAt) is { } wakeAt)
                {
                    await SleepUntilAsync(wakeAt, cancellationToken);
                    sessionEndsAt = NextSessionEnd(_settings.Load(account.Name));
                    continue;
                }

                var jobs = BuildJobs(settings, account.Name);
                var now = DateTimeOffset.UtcNow;
                Job? next;
                lock (_sync)
                {
                    next = jobs.OrderBy(job => DueAt(job.Key)).FirstOrDefault();
                }

                if (next is null)
                {
                    Activity = "Running — nothing enabled yet. Turn on features or add items to a build queue.";
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                    continue;
                }

                var dueAt = DueAt(next.Key);
                if (dueAt > now)
                {
                    Activity = $"Waiting — next: {next.Title} at {dueAt.ToLocalTime():HH:mm:ss}";
                    var wait = dueAt - now;
                    await Task.Delay(wait < TimeSpan.FromSeconds(2) ? wait : TimeSpan.FromSeconds(2), cancellationToken);
                    continue;
                }

                Activity = $"Running — {next.Title}";
                await ExecuteJobAsync(account, settings, next, cancellationToken);
                if (_runner.ConsumeBrowserClosedByUserSignal())
                {
                    LoggedIn = false;
                    Activity = "Not logged in";
                    Alert = "The browser window was closed. Log in again to continue.";
                    Log("[mac] browser was closed; automation stopped.");
                    return;
                }

                // A short human-like pause between jobs.
                await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(3, 9)), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop requested.
        }
        catch (Exception ex)
        {
            Alert = ex.Message;
            Log($"[mac] automation stopped: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Activity = LoggedIn ? "Logged in — automation stopped" : "Not logged in";
        }
    }

    // ----- Session pacing --------------------------------------------------------------------------

    private static string PacingValue(MacSettings settings, string key)
        => settings.Options.GetValueOrDefault(key) ?? MacDefaults.Options[key];

    private static int PacingMinutes(MacSettings settings, string key)
        => int.TryParse(PacingValue(settings, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Max(1, value) : 1;

    private static TimeSpan RandomMinutes(MacSettings settings, string minKey, string maxKey)
    {
        var min = PacingMinutes(settings, minKey);
        var max = Math.Max(min, PacingMinutes(settings, maxKey));
        return TimeSpan.FromSeconds(Random.Shared.Next(min * 60, (max * 60) + 1));
    }

    private static int ParseHour(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour) ? Math.Clamp(hour, 0, 24) : 0;

    private static DateTimeOffset NextSessionEnd(MacSettings settings)
        => DateTimeOffset.UtcNow + RandomMinutes(settings, "mac_pacing_run_min_minutes", "mac_pacing_run_max_minutes");

    /// <summary>Returns when to come back if the loop should take a break now, or null to keep playing.</summary>
    internal static DateTimeOffset? ResolveWakeTime(MacSettings settings, DateTimeOffset sessionEndsAt, DateTimeOffset? nowOverride = null)
    {
        if (!string.Equals(PacingValue(settings, "mac_pacing_enabled"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var now = nowOverride ?? DateTimeOffset.Now;
        var from = ParseHour(PacingValue(settings, "mac_active_from_hour"));
        var to = ParseHour(PacingValue(settings, "mac_active_to_hour"));
        var limited = from != to && !(from == 0 && to == 24);
        if (limited)
        {
            var hour = now.Hour;
            var active = from < to ? hour >= from && hour < to : hour >= from || hour < to;
            if (!active)
            {
                // Outside the allowed hours: come back at the next start hour, plus a few random minutes.
                var start = new DateTimeOffset(now.Year, now.Month, now.Day, from % 24, 0, 0, now.Offset);
                if (start <= now)
                {
                    start = start.AddDays(1);
                }

                return start.AddSeconds(Random.Shared.Next(0, 600));
            }
        }

        return now.ToUniversalTime() >= sessionEndsAt
            ? now + RandomMinutes(settings, "mac_pacing_sleep_min_minutes", "mac_pacing_sleep_max_minutes")
            : null;
    }

    // A pacing break: close the browser (the saved session makes the next login quick), wait, log back in.
    private async Task SleepUntilAsync(DateTimeOffset wakeAt, CancellationToken cancellationToken)
    {
        Log($"[mac] taking a break until {wakeAt.ToLocalTime():HH:mm}; closing the browser.");
        await _manualGate.WaitAsync(cancellationToken);
        try
        {
            await _runner.ShutdownAsync(Log);
            LoggedIn = false;
            _sleepUntilUtc = wakeAt.ToUniversalTime();
            _wakeRequested = false;
            Activity = $"Sleeping until {wakeAt.ToLocalTime():HH:mm} — browser closed";
            while (DateTimeOffset.UtcNow < _sleepUntilUtc && !_wakeRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            _sleepUntilUtc = null;
            Log("[mac] break over; logging back in.");
            for (var attempt = 1; !LoggedIn; attempt++)
            {
                Activity = "Waking up — logging in";
                try
                {
                    await LoginCoreAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && attempt < 4)
                {
                    Log($"[mac] login after the break failed ({attempt}/3): {ex.Message} — retrying in 5 minutes.");
                    Activity = "Waking up — login failed, retrying in 5 minutes";
                    await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                }
            }
        }
        finally
        {
            _sleepUntilUtc = null;
            _manualGate.Release();
        }
    }

    private async Task ExecuteJobAsync(AccountEntry account, MacSettings settings, Job job, CancellationToken cancellationToken)
    {
        var options = BotOptionsPayloadApplier.Apply(BuildOptions(account, settings), job.Payload);
        try
        {
            if (job.Task == MarkReadTask)
            {
                await MarkInboxReadCoreAsync(options, cancellationToken);
                Schedule(job.Key, job.SuccessInterval(), "Messages and reports marked as read.");
                return;
            }

            if (job.Task == "send_farmlists")
            {
                // The game only lets Gold Club accounts start a farm list. Without it the engine sends the
                // lists' targets as individual raids instead, and this round is done.
                var raidState = settings.FarmRaids;
                var guard = new FarmRaidGuard(raidState.BusyUntilUtc, raidState.OnHold.Keys.ToList(), raidState.LastReportId);
                var (goldClubActive, raids) = await _runner.SendFarmListTargetsAsRaidsUnlessGoldClubAsync(
                    options, guard, Log, cancellationToken: cancellationToken);
                if (!goldClubActive && raids is not null)
                {
                    _settings.Update(account.Name, current =>
                    {
                        var state = current.FarmRaids;
                        state.LastReportId = Math.Max(state.LastReportId, raids.LastReportId);
                        foreach (var expired in state.BusyUntilUtc.Where(pair => pair.Value <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToList())
                        {
                            state.BusyUntilUtc.Remove(expired);
                        }

                        foreach (var raid in raids.SentRaids ?? [])
                        {
                            state.BusyUntilUtc[$"{raid.X}|{raid.Y}"] = raid.ReturnAtUtc;
                        }

                        foreach (var loss in raids.NewLosses ?? [])
                        {
                            state.OnHold[$"{loss.X}|{loss.Y}"] = new HeldTarget
                            {
                                Reason = loss.AllLost ? "Last raid lost all troops" : "Last raid had losses",
                                SinceUtc = DateTimeOffset.UtcNow,
                            };
                        }
                    });

                    var delay = TimeSpan.FromSeconds(FarmingDefaults.CalculateDispatchDelaySeconds(
                        options.ContinuousFarmDispatchDelayMinMinutes, options.ContinuousFarmDispatchDelayMaxMinutes));
                    Schedule(
                        job.Key,
                        delay,
                        $"Raids sent: {raids.Sent}. Still out: {raids.AlreadyRunning}. On hold: {raids.SkippedOnHold}. Waiting for troops: {raids.WaitingForTroops}. Failed: {raids.Failed}.");
                    return;
                }
            }

            if (job.Task == "send_farmlists"
                && options.ContinuousFarmListNames.Count == 0
                && options.ContinuousFarmListIds.Count == 0
                && FarmingDefaults.NormalizeSendMode(options.ContinuousFarmSendMode) != FarmingDefaults.SendModeAllAtOnce)
            {
                // Nothing ticked means "send every list": the engine needs the lists named explicitly.
                ApplyFarmLists(await _runner.ReadFarmListsOverviewAsync(options, Log, cancellationToken: cancellationToken));
                if (_farmLists.Count == 0)
                {
                    Log("[mac] no farm lists exist on this account yet; farming will retry later.");
                    Schedule(job.Key, TimeSpan.FromMinutes(30), "No farm lists found.");
                    return;
                }

                options = options with
                {
                    ContinuousFarmListNames = _farmLists.Select(list => list.Name).ToList(),
                    ContinuousFarmListIds = _farmLists.Where(list => list.ListId is not null).Select(list => list.ListId!).ToList(),
                };
            }

            var result = await _runner.ExecuteOnceAsync(options, Log, [job.Task], cancellationToken: cancellationToken);
            var message = result.LastTask?.Message ?? "completed";
            _failures.Remove(job.Key);
            if (job.BuildItemId is not null)
            {
                CompleteBuildRun(account.Name, job, result);
            }
            else
            {
                Schedule(job.Key, job.SuccessInterval(), message);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TaskWaitException wait)
        {
            var delay = TimeSpan.FromSeconds(wait.DelaySeconds + Random.Shared.Next(5, 30));
            Schedule(job.Key, delay > MaxDeferral ? MaxDeferral : delay, wait.Message);
            if (job.BuildItemId is not null)
            {
                UpdateBuildItem(account.Name, job, item =>
                {
                    item.Note = Shorten(wait.Message);
                    item.StallCount = 0;
                });
            }
        }
        catch (TaskBlockedPermanentlyException blocked)
        {
            Log($"[mac] {job.Title} is blocked: {blocked.Message}");
            if (job.BuildItemId is not null)
            {
                UpdateBuildItem(account.Name, job, item =>
                {
                    item.Status = "blocked";
                    item.Note = Shorten(blocked.Message);
                });
                Schedule(job.Key, TimeSpan.FromSeconds(20), blocked.Message);
            }
            else
            {
                Schedule(job.Key, TimeSpan.FromHours(2), blocked.Message);
            }
        }
        catch (Exception ex) when (ex is AccountAccessException or ManualVerificationRequiredException or UnexpectedTravianLanguageException)
        {
            // These need the user: a restricted account, a captcha/verification page, or a non-English UI.
            throw;
        }
        catch (Exception ex)
        {
            var failures = _failures.GetValueOrDefault(job.Key) + 1;
            _failures[job.Key] = failures;
            var backoff = TimeSpan.FromMinutes(Math.Min(60, 2 * Math.Pow(2, failures - 1)));
            Log($"[mac] {job.Title} failed ({failures}x): {ex.Message} — retrying in {backoff.TotalMinutes:F0} min.");
            Schedule(job.Key, backoff, ex.Message);
        }

        await RefreshVillageAfterJobAsync(account, settings, job, cancellationToken);
    }

    // Keeps the UI's village view current without extra navigation: the engine just worked in this
    // village, so a quick current-page resource read is cheap.
    private async Task RefreshVillageAfterJobAsync(AccountEntry account, MacSettings settings, Job job, CancellationToken cancellationToken)
    {
        if (job.VillageKey is null || job.BuildItemId is null)
        {
            return;
        }

        try
        {
            VillageView? village;
            lock (_sync)
            {
                _villages.TryGetValue(job.VillageKey, out village);
            }

            if (village is null)
            {
                return;
            }

            var status = await _runner.ReadVillageStatusAsync(
                BuildOptions(account, settings), Log, village.Name, village.Url, cancellationToken: cancellationToken);
            ApplyVillageStatus(account.Name, status, replaceMembership: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"[mac:verbose] village refresh after '{job.Title}' failed: {ex.Message}");
        }
    }

    private void CompleteBuildRun(string accountName, Job job, BotTaskExecutionResult result)
    {
        var last = result.Tasks.LastOrDefault();
        var message = last?.Message ?? string.Empty;
        var outcome = last?.ConstructionOutcome ?? ConstructionTaskOutcome.None;
        if (IsBuildItemFinished(job.Task, outcome, message))
        {
            _settings.Update(accountName, settings =>
            {
                if (settings.Villages.TryGetValue(job.VillageKey!, out var village))
                {
                    village.BuildQueue.RemoveAll(item => item.Id == job.BuildItemId);
                }
            });
            Log($"[mac] build queue item finished: {job.Title} — {message}");
            Schedule(job.Key, TimeSpan.FromSeconds(Random.Shared.Next(5, 20)), message);
            return;
        }

        if (message.Contains("could not start demolition", StringComparison.OrdinalIgnoreCase))
        {
            UpdateBuildItem(accountName, job, item =>
            {
                item.Status = "blocked";
                item.Note = "The game did not offer demolition. It needs Main Building level 10.";
            });
            Schedule(job.Key, TimeSpan.FromSeconds(20), message);
            return;
        }

        if (outcome == ConstructionTaskOutcome.MissingBuilding)
        {
            UpdateBuildItem(accountName, job, item =>
            {
                item.Status = "blocked";
                item.Note = "The slot is empty — add a Construct item for this building first.";
            });
            Schedule(job.Key, TimeSpan.FromSeconds(20), message);
            return;
        }

        var stalled = false;
        UpdateBuildItem(accountName, job, item =>
        {
            item.StallCount++;
            item.Note = Shorten(message);
            if (item.StallCount >= 8)
            {
                item.Status = "blocked";
                item.Note = $"No progress after {item.StallCount} attempts: {Shorten(message)}";
                stalled = true;
            }
        });
        Schedule(job.Key, stalled ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(Random.Shared.Next(60, 150)), message);
    }

    internal static bool IsBuildItemFinished(string taskName, ConstructionTaskOutcome outcome, string message)
    {
        if (outcome is ConstructionTaskOutcome.AlreadySatisfied or ConstructionTaskOutcome.AlreadyExists)
        {
            return true;
        }

        if (message.Contains("max level", StringComparison.OrdinalIgnoreCase)
            || message.Contains("appears maxed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("at or above target level", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var reached = Regex.Match(message, @"reached level (\d+) \(target (\d+)\)", RegexOptions.IgnoreCase);
        if (reached.Success)
        {
            return int.Parse(reached.Groups[1].Value, CultureInfo.InvariantCulture)
                >= int.Parse(reached.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        if (outcome == ConstructionTaskOutcome.ConfirmedComplete)
        {
            return true;
        }

        // construct_building with target level 1 is done as soon as the building is queued.
        return string.Equals(taskName, "construct_building", StringComparison.OrdinalIgnoreCase)
            && outcome == ConstructionTaskOutcome.QueuedOrInProgress
            && message.StartsWith("Queued ", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateBuildItem(string accountName, Job job, Action<BuildItem> change)
    {
        _settings.Update(accountName, settings =>
        {
            if (settings.Villages.TryGetValue(job.VillageKey!, out var village)
                && village.BuildQueue.FirstOrDefault(item => item.Id == job.BuildItemId) is { } item)
            {
                change(item);
            }
        });
    }

    // ----- Jobs ---------------------------------------------------------------------------------

    private sealed record Job(
        string Key,
        string Title,
        string Task,
        Dictionary<string, string> Payload,
        int MinMinutes,
        int MaxMinutes,
        string? VillageKey = null,
        string? BuildItemId = null)
    {
        public TimeSpan SuccessInterval()
            => TimeSpan.FromSeconds(Random.Shared.Next(MinMinutes * 60, (Math.Max(MinMinutes, MaxMinutes) * 60) + 1));
    }

    private List<Job> BuildJobs(MacSettings settings, string accountName)
    {
        var jobs = new List<Job>();
        var features = settings.Features;
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (features.Hero)
        {
            jobs.Add(new Job("hero", "Hero: adventures", "hero_manage", empty, 10, 20));
        }

        if (features.Farming)
        {
            jobs.Add(new Job("farming", "Farming: send farm lists", "send_farmlists", empty, 20, 40));
        }

        if (features.CollectTasks)
        {
            jobs.Add(new Job("collect_tasks", "Collect task rewards", "collect_tasks", empty, 45, 90));
        }

        if (features.CollectDailyQuests)
        {
            jobs.Add(new Job("collect_daily_quests", "Collect daily quests", "collect_daily_quests", empty, 180, 300));
        }

        if (features.MarkRead)
        {
            jobs.Add(new Job("mark_read", "Mark messages and reports as read", MarkReadTask, empty, 60, 120));
        }

        if (features.ProductionBonus)
        {
            var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [BotOptionPayloadKeys.ProductionBonusVideoEnabled] = "true" };
            jobs.Add(new Job("production_bonus", "+15% production bonus videos", "activate_production_bonus", payload, 120, 240));
        }

        if (features.ResourceTransfer)
        {
            var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [BotOptionPayloadKeys.ResourceTransferEnabled] = "true" };
            jobs.Add(new Job("resource_transfer", "Send resources between villages", "send_resources_between_villages", payload, 20, 40));
        }

        if (features.Reinforcements)
        {
            var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [BotOptionPayloadKeys.ReinforcementsEnabled] = "true" };
            jobs.Add(new Job("reinforcements", "Send reinforcements between villages", "send_reinforcements_between_villages", payload, 30, 60));
        }

        foreach (var village in Villages(accountName))
        {
            if (!settings.Villages.TryGetValue(village.Key, out var villageSettings) || !villageSettings.Enabled)
            {
                continue;
            }

            var target = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BotOptionPayloadKeys.TargetVillageName] = village.Name,
            };
            if (!string.IsNullOrWhiteSpace(village.Url))
            {
                target[BotOptionPayloadKeys.TargetVillageUrl] = village.Url;
            }

            if (village.X.HasValue && village.Y.HasValue)
            {
                target[BotOptionPayloadKeys.TargetVillageKey] = $"xy:{village.X}|{village.Y}";
            }

            var buildItem = villageSettings.BuildQueue.FirstOrDefault(item => item.Status == "pending");
            if (features.Construction && buildItem is not null && CreateBuildJob(village, buildItem, target) is { } buildJob)
            {
                jobs.Add(buildJob);
            }

            if (features.Troops && villageSettings.Troops.Any(pair =>
                    pair.Key.EndsWith("_enabled", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(pair.Value, "true", StringComparison.OrdinalIgnoreCase)))
            {
                var payload = new Dictionary<string, string>(target, StringComparer.OrdinalIgnoreCase);
                foreach (var pair in villageSettings.Troops)
                {
                    payload[pair.Key] = pair.Value;
                }

                jobs.Add(new Job($"troops:{village.Key}", $"Train troops in {village.Name}", "build_troops", payload, 10, 25, village.Key));
            }

            if (features.Smithy && !string.IsNullOrWhiteSpace(villageSettings.SmithyTargets))
            {
                var payload = new Dictionary<string, string>(target, StringComparer.OrdinalIgnoreCase)
                {
                    [BotOptionPayloadKeys.SmithyUpgradeTargets] = villageSettings.SmithyTargets,
                };
                jobs.Add(new Job($"smithy:{village.Key}", $"Smithy upgrades in {village.Name}", "upgrade_troops_at_smithy", payload, 30, 60, village.Key));
            }

            if (features.Brewery && villageSettings.Brewery)
            {
                jobs.Add(new Job($"brewery:{village.Key}", $"Brewery celebration in {village.Name}", "run_brewery_celebration", target, 30, 60, village.Key));
            }

            if (features.TownHall && villageSettings.TownHall)
            {
                jobs.Add(new Job($"townhall:{village.Key}", $"Town Hall celebration in {village.Name}", "run_town_hall_celebration", target, 30, 60, village.Key));
            }
        }

        return jobs;
    }

    private static Job? CreateBuildJob(VillageView village, BuildItem item, Dictionary<string, string> target)
    {
        var payload = new Dictionary<string, string>(target, StringComparer.OrdinalIgnoreCase);
        var level = item.TargetLevel.ToString(CultureInfo.InvariantCulture);
        string task;
        switch (item.Type)
        {
            case "resources_all":
                task = "upgrade_all_resources_to_level";
                payload[BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = level;
                payload[BotOptionPayloadKeys.ResourceUpgradeTypes] = item.ResourceTypes;
                break;
            case "resource_field" when item.SlotId.HasValue:
                task = "upgrade_resource_to_level";
                payload[BotOptionPayloadKeys.ResourceUpgradeSlotId] = item.SlotId.Value.ToString(CultureInfo.InvariantCulture);
                payload[BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = level;
                break;
            case "building_upgrade" when item.SlotId.HasValue:
                task = "upgrade_building_to_level";
                payload[BotOptionPayloadKeys.BuildingUpgradeSlotId] = item.SlotId.Value.ToString(CultureInfo.InvariantCulture);
                payload[BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = level;
                payload[BotOptionPayloadKeys.BuildingUpgradeName] = item.Name;
                break;
            case "building_max" when item.SlotId.HasValue:
                task = "upgrade_building_to_max";
                payload[BotOptionPayloadKeys.BuildingUpgradeSlotId] = item.SlotId.Value.ToString(CultureInfo.InvariantCulture);
                payload[BotOptionPayloadKeys.BuildingUpgradeName] = item.Name;
                break;
            case "building_demolish" when item.SlotId.HasValue:
                task = "demolish_building_to_level";
                payload[BotOptionPayloadKeys.TargetBuildingSlotOrName] = item.SlotId.Value.ToString(CultureInfo.InvariantCulture);
                payload[BotOptionPayloadKeys.TargetLevel] = level;
                break;
            case "building_construct" when item.SlotId.HasValue && item.Gid.HasValue:
                task = "construct_building";
                payload[BotOptionPayloadKeys.BuildingConstructSlotId] = item.SlotId.Value.ToString(CultureInfo.InvariantCulture);
                payload[BotOptionPayloadKeys.BuildingConstructGid] = item.Gid.Value.ToString(CultureInfo.InvariantCulture);
                payload[BotOptionPayloadKeys.BuildingConstructName] = item.Name;
                payload[BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = level;
                break;
            default:
                return null;
        }

        return new Job($"build:{village.Key}", $"{DescribeBuildItem(item)} in {village.Name}", task, payload, 1, 2, village.Key, item.Id);
    }

    internal static string DescribeBuildItem(BuildItem item) => item.Type switch
    {
        "resources_all" => $"All resource fields to level {item.TargetLevel}",
        "resource_field" => $"{item.Name} (slot {item.SlotId}) to level {item.TargetLevel}",
        "building_construct" => $"Construct {item.Name} in slot {item.SlotId} to level {item.TargetLevel}",
        "building_max" => $"{item.Name} (slot {item.SlotId}) to max level",
        "building_demolish" => $"Demolish {item.Name} (slot {item.SlotId}) to level {item.TargetLevel}",
        _ => $"{item.Name} (slot {item.SlotId}) to level {item.TargetLevel}",
    };

    private DateTimeOffset DueAt(string key)
    {
        lock (_sync)
        {
            return _due.TryGetValue(key, out var due) ? due : DateTimeOffset.MinValue;
        }
    }

    private void Schedule(string key, TimeSpan delay, string result)
    {
        lock (_sync)
        {
            _due[key] = DateTimeOffset.UtcNow + delay;
            _lastResult[key] = Shorten(result);
        }
    }

    // ----- State --------------------------------------------------------------------------------

    private BotOptions BuildOptions(AccountEntry account, MacSettings settings)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in MacDefaults.Options)
        {
            overrides[pair.Key] = pair.Value;
        }

        foreach (var pair in settings.Options)
        {
            if (!TryExpandJsonList(pair.Key, pair.Value, overrides))
            {
                overrides[pair.Key] = pair.Value;
            }
        }

        overrides["base_url"] = account.ServerUrl;
        overrides["server_name"] = account.ServerName;
        overrides[BotOptionPayloadKeys.HeroResourceTransferEnabled] = settings.Features.HeroResources ? "true" : "false";
        for (var index = 0; index < settings.FarmListNames.Count; index++)
        {
            overrides[$"{BotOptionPayloadKeys.ContinuousFarmListNames}:{index}"] = settings.FarmListNames[index];
        }

        for (var index = 0; index < settings.FarmListIds.Count; index++)
        {
            overrides[$"{BotOptionPayloadKeys.ContinuousFarmListIds}:{index}"] = settings.FarmListIds[index];
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(_root, "config", "bot.json"), optional: true)
            .AddInMemoryCollection(overrides)
            .Build();
        return BotOptionsFactory.FromConfiguration(configuration);
    }

    // List-valued engine options (source villages, reinforcement rules) are stored as a JSON array and
    // expanded to the "key:index[:property]" form the configuration binder reads.
    private static bool TryExpandJsonList(string key, string value, Dictionary<string, string?> target)
    {
        if (!value.TrimStart().StartsWith('['))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in element.EnumerateObject())
                    {
                        target[$"{key}:{index}:{property.Name}"] = property.Value.ToString();
                    }
                }
                else
                {
                    target[$"{key}:{index}"] = element.ToString();
                }

                index++;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private AccountEntry RequireActiveAccount()
    {
        var name = _accounts.ActiveAccountName();
        return _accounts.Find(name) ?? throw new InvalidOperationException("No account configured. Add an account first.");
    }

    private void EnsureVillagesLoaded(string accountName)
    {
        if (string.Equals(_villagesAccount, accountName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _villagesAccount = accountName;
        _villages = accountName.Length == 0
            ? new Dictionary<string, VillageView>(StringComparer.OrdinalIgnoreCase)
            : _settings.LoadVillages(accountName);
        _farmLists = [];
        _hero = null;
        _tribe = _villages.Values.Select(village => village.Tribe).FirstOrDefault(IsKnownTribe) ?? "Unknown";
    }

    private void ApplyVillageStatus(string accountName, VillageStatus status, bool replaceMembership)
    {
        lock (_sync)
        {
            EnsureVillagesLoaded(accountName);
            if (IsKnownTribe(status.Tribe))
            {
                _tribe = status.Tribe;
            }

            if (status.Villages.Count > 0)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var village in status.Villages)
                {
                    var key = VillageKey(village.Name, village.CoordX, village.CoordY);
                    seen.Add(key);
                    var existing = _villages.GetValueOrDefault(key);
                    _villages[key] = existing is null
                        ? new VillageView(key, village.Name, village.Url, village.CoordX, village.CoordY, village.IsCapital, _tribe, [], null, null, [], [], [], null)
                        : existing with { Name = village.Name, Url = village.Url ?? existing.Url, IsCapital = village.IsCapital ?? existing.IsCapital };
                }

                if (replaceMembership)
                {
                    foreach (var stale in _villages.Keys.Where(key => !seen.Contains(key)).ToList())
                    {
                        _villages.Remove(stale);
                    }
                }
            }

            var activeKey = ResolveActiveVillageKey(status);
            if (activeKey is not null && _villages.TryGetValue(activeKey, out var active))
            {
                _villages[activeKey] = active with
                {
                    Tribe = _tribe,
                    IsCapital = status.IsCapital ?? active.IsCapital,
                    Resources = new Dictionary<string, string>(status.Resources, StringComparer.OrdinalIgnoreCase),
                    Warehouse = status.WarehouseCapacity,
                    Granary = status.GranaryCapacity,
                    Fields = status.ResourceFields.Count > 0
                        ? status.ResourceFields.Select(field => new FieldView(field.SlotId, field.FieldType, field.Name, field.Level)).ToList()
                        : active.Fields,
                    Buildings = status.Buildings.Count > 0
                        ? status.Buildings.Select(building => new BuildingView(building.SlotId, building.Name, building.Level, building.Gid)).ToList()
                        : active.Buildings,
                    Constructions = (status.ActiveConstructions ?? [])
                        .Select(item => new ConstructionView(item.Name, item.Level, item.Finish?.FinishUtc))
                        .ToList(),
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };
            }

            _settings.SaveVillages(accountName, _villages);
        }

        ApplyHero(status.HeroStatus, status.AdventureCount, inventory: null);

        // Every known village gets a settings entry so its build queue and troop rules can be edited.
        _settings.Update(accountName, settings =>
        {
            foreach (var village in Villages(accountName))
            {
                if (!settings.Villages.TryGetValue(village.Key, out var entry))
                {
                    settings.Villages[village.Key] = new VillageSettings { Name = village.Name };
                }
                else
                {
                    entry.Name = village.Name;
                }
            }
        });
    }

    private string? ResolveActiveVillageKey(VillageStatus status)
    {
        if (status.ActiveVillageCoordX.HasValue && status.ActiveVillageCoordY.HasValue)
        {
            var key = VillageKey(status.ActiveVillage, status.ActiveVillageCoordX, status.ActiveVillageCoordY);
            if (_villages.ContainsKey(key))
            {
                return key;
            }
        }

        return _villages.Values
            .FirstOrDefault(village => string.Equals(village.Name, status.ActiveVillage, StringComparison.OrdinalIgnoreCase))?.Key;
    }

    private static string VillageKey(string name, int? x, int? y)
        => x.HasValue && y.HasValue ? $"xy:{x}|{y}" : $"name:{name.Trim().ToLowerInvariant()}";

    private static bool IsKnownTribe(string? tribe)
        => !string.IsNullOrWhiteSpace(tribe) && !string.Equals(tribe, "Unknown", StringComparison.OrdinalIgnoreCase);

    private void ApplyHero(HeroStatus? status, int? adventureCount, HeroInventoryResources? inventory)
    {
        if (status is null && adventureCount is null && inventory is null)
        {
            return;
        }

        _hero = new HeroView(
            status?.IsDead == true ? "Dead" : status?.State ?? _hero?.State ?? "Unknown",
            status?.HpPercent ?? _hero?.Hp,
            adventureCount ?? status?.AdventuresAvailable ?? _hero?.Adventures ?? 0,
            inventory ?? _hero?.Inventory,
            DateTimeOffset.UtcNow);
    }

    private void ApplyFarmLists(IReadOnlyList<FarmListOverview> lists)
    {
        _farmLists = lists
            .Select(list => new FarmListView(list.Name, list.ListId, list.VillageName, list.ActiveFarmCount, list.TotalFarmCount))
            .ToList();
    }

    // ----- Manual operations and prompts ---------------------------------------------------------

    private async Task RunManualAsync(string title, Func<CancellationToken, Task> action)
    {
        if (!await _manualGate.WaitAsync(0))
        {
            throw new InvalidOperationException(_sleepUntilUtc is null
                ? $"Busy: {Busy}. Wait for it to finish."
                : "The bot is taking a break and the browser is closed. Press “Wake now” first.");
        }

        using var cancellation = new CancellationTokenSource();
        _manualCancellation = cancellation;
        Busy = title;
        try
        {
            await action(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Log($"[mac] {title.ToLowerInvariant()} canceled.");
        }
        catch (UnexpectedTravianLanguageException ex)
        {
            Alert = $"{ex.Message} Open Settings and use 'Set game language to English'.";
            throw;
        }
        catch (Exception ex)
        {
            Alert = $"{title} failed: {ex.Message}";
            throw;
        }
        finally
        {
            Busy = string.Empty;
            _manualCancellation = null;
            _manualGate.Release();
        }
    }

    private async Task<string?> RequestLobbyWorldAsync(LobbyWorldSelectionRequest request, CancellationToken cancellationToken)
    {
        var message = request.PreviousSelectionFailed
            ? "That world could not be opened. Choose the game world to play:"
            : "Several game worlds are available for this login. Choose the one to play:";
        var options = request.Worlds.Select(world => new PromptOption(world.WorldUid, world.DisplayText)).ToList();
        return await AskAsync("world", message, options, cancellationToken);
    }

    private async Task<bool> RequestManualLoginConfirmationAsync(ManualLoginConfirmationRequest request, CancellationToken cancellationToken)
    {
        var message = "Finish logging in inside the browser window, then confirm here."
            + (string.IsNullOrWhiteSpace(request.ValidationMessage) ? string.Empty : $" ({request.ValidationMessage})");
        var answer = await AskAsync(
            "manual_login",
            message,
            [new PromptOption("ok", "I am logged in"), new PromptOption(string.Empty, "Cancel")],
            cancellationToken);
        return answer == "ok";
    }

    private async Task<string?> AskAsync(string kind, string message, List<PromptOption> options, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _promptAnswer = answer;
            _prompt = new PromptView(Guid.NewGuid().ToString("N"), kind, message, options);
        }

        using var registration = cancellationToken.Register(() =>
        {
            lock (_sync)
            {
                _prompt = null;
            }

            answer.TrySetCanceled(cancellationToken);
        });
        var value = await answer.Task;
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private void Log(string message) => _log.Add(message);

    private static string Shorten(string? text)
    {
        var value = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        // Drop the engine's machine-readable wrapper and tokens so the UI shows the readable reason only.
        value = Regex.Replace(value, @"^Task '[a-z_]+' (waiting|blocked permanently): ", string.Empty);
        value = Regex.Replace(value, @"\s*\b[a-z_]+=\S+", string.Empty).Trim();
        return value.Length <= 220 ? value : $"{value[..220]}…";
    }
}
