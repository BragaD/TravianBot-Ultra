using System.Text.Json;
using TbotUltra.Core.Accounts;

namespace TbotUltra.Mac;

public sealed class FeatureToggles
{
    public bool Construction { get; set; } = true;
    public bool Hero { get; set; } = true;
    public bool HeroResources { get; set; } = true;
    public bool Farming { get; set; }
    public bool Troops { get; set; }
    public bool CollectTasks { get; set; } = true;
    public bool CollectDailyQuests { get; set; } = true;
    public bool TownHall { get; set; }
    public bool Smithy { get; set; }
    public bool Brewery { get; set; }
    public bool ResourceTransfer { get; set; }
    public bool Reinforcements { get; set; }
    public bool ProductionBonus { get; set; }
    public bool MarkRead { get; set; }
    public bool AttackWatch { get; set; } = true;
    public bool Academy { get; set; }
    public bool Traps { get; set; }
}

public sealed class BuildItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];

    /// <summary>resources_all | resource_field | building_upgrade | building_max | building_construct | building_demolish</summary>
    public string Type { get; set; } = string.Empty;
    public int? SlotId { get; set; }
    public int? Gid { get; set; }
    public string Name { get; set; } = string.Empty;
    public int TargetLevel { get; set; } = 1;
    public string ResourceTypes { get; set; } = "wood,clay,iron,crop";

    /// <summary>pending | blocked</summary>
    public string Status { get; set; } = "pending";
    public string Note { get; set; } = string.Empty;
    public int StallCount { get; set; }
}

public sealed class VillageSettings
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<BuildItem> BuildQueue { get; set; } = [];

    /// <summary>Engine troop-training payload keys (troop_training_*) for this village.</summary>
    public Dictionary<string, string> Troops { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string SmithyTargets { get; set; } = string.Empty;

    /// <summary>Troop names to research at the Academy, separated by ';', in the order to research them.</summary>
    public string AcademyTargets { get; set; } = string.Empty;

    /// <summary>Build traps in this village (Gauls).</summary>
    public bool Traps { get; set; }

    /// <summary>Most traps to queue per round; 0 queues as many as the Trapper allows.</summary>
    public int TrapsPerRun { get; set; }
    public bool TownHall { get; set; }
    public bool Brewery { get; set; }
}

public sealed class StartupSettings
{
    public bool AutoLogin { get; set; }
    public bool AutoStart { get; set; }
}

public sealed class HeldTarget
{
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset SinceUtc { get; set; }
}

/// <summary>
/// What the no-Gold-Club raid sender remembers between rounds: raids sent from the Rally Point do
/// not show on the farm list, so in-flight targets and loss reports are tracked here. Keys are "x|y".
/// </summary>
public sealed class FarmRaidState
{
    public long LastReportId { get; set; }
    public Dictionary<string, DateTimeOffset> BusyUntilUtc { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, HeldTarget> OnHold { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Raid size factor per target, adjusted from how full raids came home.</summary>
    public Dictionary<string, double> TroopScale { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>What the bot got done on one calendar day (local time).</summary>
public sealed class DailyStats
{
    public int BuildsFinished { get; set; }
    public int RaidsSent { get; set; }
    public long ResourcesRaided { get; set; }
    public int TrainingBatches { get; set; }
    public int AdventuresSent { get; set; }
    public int AttacksSeen { get; set; }
    public int TargetsPutOnHold { get; set; }
    public bool SummarySent { get; set; }
}

public sealed class MacSettings
{
    /// <summary>Per-day counters keyed by local date (yyyy-MM-dd); the last two weeks are kept.</summary>
    public Dictionary<string, DailyStats> Daily { get; set; } = new(StringComparer.Ordinal);
    public StartupSettings Startup { get; set; } = new();
    public FarmRaidState FarmRaids { get; set; } = new();
    public FeatureToggles Features { get; set; } = new();

    /// <summary>Account-level engine option overrides, keyed by <c>BotOptionPayloadKeys</c> names.</summary>
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> FarmListNames { get; set; } = [];
    public List<string> FarmListIds { get; set; } = [];
    public Dictionary<string, VillageSettings> Villages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Per-account persistence for the Mac front-end's own settings and village cache.</summary>
public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _sync = new();
    private readonly string _root;

    public SettingsStore(string root)
    {
        _root = root;
    }

    public MacSettings Load(string accountName)
    {
        lock (_sync)
        {
            return ReadFile<MacSettings>(SettingsPath(accountName)) ?? new MacSettings();
        }
    }

    /// <summary>Applies one change under the store lock so UI edits and loop updates never overwrite each other.</summary>
    public MacSettings Update(string accountName, Action<MacSettings> change)
    {
        lock (_sync)
        {
            var settings = ReadFile<MacSettings>(SettingsPath(accountName)) ?? new MacSettings();
            change(settings);
            WriteFile(SettingsPath(accountName), settings);
            return settings;
        }
    }

    public Dictionary<string, VillageView> LoadVillages(string accountName)
    {
        lock (_sync)
        {
            var loaded = ReadFile<Dictionary<string, VillageView>>(VillagesPath(accountName));
            return loaded is null
                ? new Dictionary<string, VillageView>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, VillageView>(loaded, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SaveVillages(string accountName, IReadOnlyDictionary<string, VillageView> villages)
    {
        lock (_sync)
        {
            WriteFile(VillagesPath(accountName), villages);
        }
    }

    /// <summary>The hero as last read, so its inventory is known before the first read after a launch.</summary>
    public HeroView? LoadHero(string accountName)
    {
        lock (_sync)
        {
            return ReadFile<HeroView>(HeroPath(accountName));
        }
    }

    public void SaveHero(string accountName, HeroView hero)
    {
        lock (_sync)
        {
            WriteFile(HeroPath(accountName), hero);
        }
    }

    private string HeroPath(string accountName)
        => Path.Combine(AccountStoragePaths.AccountDirectory(_root, accountName), "mac_hero.json");

    private string SettingsPath(string accountName)
        => Path.Combine(AccountStoragePaths.AccountDirectory(_root, accountName), "mac_settings.json");

    private string VillagesPath(string accountName)
        => Path.Combine(AccountStoragePaths.AccountDirectory(_root, accountName), "mac_villages.json");

    private static T? ReadFile<T>(string path)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection instead of silently overwriting it.
            File.Move(path, $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
            return null;
        }
    }

    private static void WriteFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(value, Json));
        File.Move(tempPath, path, overwrite: true);
    }
}
