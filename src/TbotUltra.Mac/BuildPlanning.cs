using System.Globalization;
using System.Text.Json;
using TbotUltra.Worker.Services;

namespace TbotUltra.Mac;

public sealed class TemplateStep
{
    /// <summary>resources | building</summary>
    public string Kind { get; set; } = "building";
    public int? Gid { get; set; }
    public string Name { get; set; } = string.Empty;
    public int TargetLevel { get; set; } = 1;
    public string ResourceTypes { get; set; } = "wood,clay,iron,crop";
}

public sealed class BuildTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = string.Empty;
    public List<TemplateStep> Steps { get; set; } = [];
}

public sealed record PlanResult(List<BuildItem> Items, List<string> Notes);

public sealed record LevelCost(int Level, int Wood, int Clay, int Iron, int Crop, int UpkeepTotal, int CulturePoints, int BuildSeconds);

public sealed record ItemEstimate(
    string Description,
    int Steps,
    long Wood,
    long Clay,
    long Iron,
    long Crop,
    int WaitForResourcesSeconds,
    int BuildSeconds,
    int FinishesInSeconds,
    string? Blocked);

public sealed record PlanEstimate(List<ItemEstimate> Items, int TotalSeconds, DateTimeOffset? FinishesAtUtc, List<string> Assumptions);

/// <summary>Reusable build orders, shared by every account on this Mac.</summary>
public sealed class TemplateStore
{
    private readonly object _sync = new();
    private readonly string _path;

    public TemplateStore(string root)
    {
        _path = Path.Combine(root, "config", "mac_building_templates.json");
    }

    public List<BuildTemplate> Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
            {
                var starter = new List<BuildTemplate> { CreateStarterTemplate() };
                Write(starter);
                return starter;
            }

            try
            {
                return JsonSerializer.Deserialize<List<BuildTemplate>>(File.ReadAllText(_path), SettingsStore.Json) ?? [];
            }
            catch (JsonException)
            {
                File.Move(_path, $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
                return [];
            }
        }
    }

    public BuildTemplate Save(BuildTemplate template)
    {
        if (string.IsNullOrWhiteSpace(template.Name))
        {
            throw new InvalidOperationException("Give the template a name.");
        }

        lock (_sync)
        {
            var templates = Load();
            templates.RemoveAll(item => item.Id == template.Id);
            templates.Add(template);
            Write(templates.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList());
            return template;
        }
    }

    public void Delete(string id)
    {
        lock (_sync)
        {
            var templates = Load();
            templates.RemoveAll(item => item.Id == id);
            Write(templates);
        }
    }

    private void Write(List<BuildTemplate> templates)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = $"{_path}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(templates, SettingsStore.Json));
        File.Move(tempPath, _path, overwrite: true);
    }

    private static BuildTemplate CreateStarterTemplate() => new()
    {
        Name = "Starter village",
        Steps =
        [
            new TemplateStep { Kind = "resources", Name = "Resource fields", TargetLevel = 2 },
            new TemplateStep { Kind = "building", Gid = 15, Name = "Main Building", TargetLevel = 3 },
            new TemplateStep { Kind = "building", Gid = 10, Name = "Warehouse", TargetLevel = 3 },
            new TemplateStep { Kind = "building", Gid = 11, Name = "Granary", TargetLevel = 3 },
            new TemplateStep { Kind = "resources", Name = "Resource fields", TargetLevel = 5 },
            new TemplateStep { Kind = "building", Gid = 15, Name = "Main Building", TargetLevel = 5 },
            new TemplateStep { Kind = "building", Gid = 19, Name = "Barracks", TargetLevel = 3 },
            new TemplateStep { Kind = "building", Gid = 17, Name = "Marketplace", TargetLevel = 1 },
        ],
    };
}

/// <summary>
/// Pure planning over what the app knows about a village: which storage upgrades a build needs
/// first, and how a template turns into concrete queue items for one village.
/// </summary>
public static class BuildPlanner
{
    private const int WarehouseGid = 10;
    private const int GranaryGid = 11;
    private const int RallyPointGid = 16;
    private const int RallyPointSlot = 39;
    private const int WallSlot = 40;
    private const long BaseCapacity = 800;

    // Official storage capacity per Warehouse/Granary level at 1x.
    private static readonly long[] CapacityByLevel =
    [
        BaseCapacity, 1200, 1700, 2300, 3100, 4000, 5000, 6300, 7800, 9600, 11800,
        14400, 17600, 21400, 25900, 31300, 37900, 45700, 55100, 66400, 80000,
    ];

    private static readonly Dictionary<string, int> FieldGids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wood"] = 1, ["clay"] = 2, ["iron"] = 3, ["crop"] = 4,
    };

    /// <summary>
    /// Returns the Warehouse/Granary items that must come before <paramref name="item"/> so every
    /// level it builds fits in storage, given the village and what is already queued.
    /// </summary>
    public static PlanResult PlanStorage(VillageView village, IReadOnlyList<BuildItem> queue, BuildItem item)
    {
        var result = new PlanResult([], []);
        var state = new Projection(village, queue);
        AddStoragePrerequisites(village, state, item, result);
        return result;
    }

    /// <summary>Turns a template into queue items for one village, storage prerequisites included.</summary>
    public static PlanResult PlanTemplate(BuildTemplate template, VillageView village, IReadOnlyList<BuildItem> queue, string tribe)
    {
        var result = new PlanResult([], []);
        var state = new Projection(village, queue);
        var wallGid = BuildingCatalogService.WallForTribe(tribe)?.Gid;
        foreach (var step in template.Steps)
        {
            BuildItem? item = null;
            if (step.Kind == "resources")
            {
                var types = step.ResourceTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var levels = village.Fields
                    .Where(field => types.Contains(field.Type, StringComparer.OrdinalIgnoreCase))
                    .Select(field => field.Level ?? 0)
                    .ToList();
                if (levels.Count > 0 && levels.Min() >= step.TargetLevel)
                {
                    result.Notes.Add($"Resource fields are already level {levels.Min()} or higher; skipped the step to level {step.TargetLevel}.");
                    continue;
                }

                item = new BuildItem { Type = "resources_all", Name = "Resource fields", TargetLevel = step.TargetLevel, ResourceTypes = step.ResourceTypes };
            }
            else if (step.Gid is int gid)
            {
                var name = string.IsNullOrWhiteSpace(step.Name) ? BuildingCatalogService.NameForGid(gid) : step.Name;
                if (state.Buildings.TryGetValue(gid, out var existing))
                {
                    if (existing.Level >= step.TargetLevel)
                    {
                        result.Notes.Add($"{name} is already level {existing.Level}; skipped.");
                        continue;
                    }

                    item = new BuildItem { Type = "building_upgrade", SlotId = existing.Slot, Gid = gid, Name = name, TargetLevel = step.TargetLevel };
                }
                else
                {
                    foreach (var requirement in BuildingCatalogService.RequirementsFor(gid))
                    {
                        var requiredGid = BuildingCatalogService.GidForName(requirement.Name);
                        var have = requiredGid is int id && state.Buildings.TryGetValue(id, out var required) ? required.Level : 0;
                        if (have < requirement.Level)
                        {
                            result.Notes.Add($"{name} needs {requirement.Name} level {requirement.Level} first (it will wait until then).");
                        }
                    }

                    var slot = gid == RallyPointGid ? RallyPointSlot : gid == wallGid ? WallSlot : state.TakeFreeSlot();
                    if (slot is null)
                    {
                        result.Notes.Add($"No free building slot is known for {name}; skipped. Scan the village and try again.");
                        continue;
                    }

                    item = new BuildItem { Type = "building_construct", SlotId = slot, Gid = gid, Name = name, TargetLevel = step.TargetLevel };
                }
            }

            if (item is null)
            {
                continue;
            }

            AddStoragePrerequisites(village, state, item, result);
            state.Apply(item);
            result.Items.Add(item);
        }

        return result;
    }

    private static void AddStoragePrerequisites(VillageView village, Projection state, BuildItem item, PlanResult result)
    {
        var (warehouseNeed, granaryNeed) = MaxCost(village, state, item);
        var itemGid = item.Gid ?? state.GidAtSlot(item.SlotId);
        if (itemGid is WarehouseGid or GranaryGid)
        {
            // Storage upgrades are never blocked on themselves; planning them recursively helps nobody.
            return;
        }

        PlanOneStorage(village.Warehouse, WarehouseGid, "Warehouse", warehouseNeed, state, item, result);
        PlanOneStorage(village.Granary, GranaryGid, "Granary", granaryNeed, state, item, result);
    }

    private static void PlanOneStorage(long? observedCapacity, int gid, string name, long need, Projection state, BuildItem forItem, PlanResult result)
    {
        if (need <= 0)
        {
            return;
        }

        var hasBuilding = state.Buildings.TryGetValue(gid, out var storage);
        var currentLevel = hasBuilding ? storage.Level : 0;
        var villageLevel = state.VillageLevel(gid);

        // Speed worlds can scale capacities; derive the factor from what the village reports today.
        var scale = observedCapacity is > 0 ? Math.Max(1d, observedCapacity.Value / (double)CapacityByLevel[Math.Clamp(villageLevel, 0, 20)]) : 1d;
        if (CapacityByLevel[Math.Clamp(currentLevel, 0, 20)] * scale >= need)
        {
            return;
        }

        var neededLevel = Enumerable.Range(currentLevel + 1, Math.Max(0, 20 - currentLevel))
            .Cast<int?>()
            .FirstOrDefault(level => CapacityByLevel[level!.Value] * scale >= need);
        if (neededLevel is null)
        {
            result.Notes.Add($"{BotEngine.DescribeBuildItem(forItem)} needs {Number(need)} storage, more than a level 20 {name} holds.");
            return;
        }

        BuildItem prerequisite;
        if (hasBuilding)
        {
            prerequisite = new BuildItem { Type = "building_upgrade", SlotId = storage.Slot, Gid = gid, Name = name, TargetLevel = neededLevel.Value };
        }
        else
        {
            var slot = state.TakeFreeSlot();
            if (slot is null)
            {
                result.Notes.Add($"A {name} is needed for {BotEngine.DescribeBuildItem(forItem)}, but no free building slot is known.");
                return;
            }

            prerequisite = new BuildItem { Type = "building_construct", SlotId = slot, Gid = gid, Name = name, TargetLevel = neededLevel.Value };
        }

        prerequisite.Note = $"Added automatically: {BotEngine.DescribeBuildItem(forItem)} needs {Number(need)} storage.";
        state.Apply(prerequisite);
        result.Items.Add(prerequisite);
        result.Notes.Add($"Added {name} to level {neededLevel} first: {BotEngine.DescribeBuildItem(forItem)} costs up to {Number(need)} of one resource.");
    }

    // UI text is English, so numbers are too, whatever the Mac's region is.
    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Median of the observed/table build-time ratios; 1 when nothing has been observed.</summary>
    public static double TimeFactor(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0)
        {
            return 1;
        }

        var sorted = samples.OrderBy(value => value).ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }

    /// <summary>Cost and build time of each level of one building type, for this village's Main Building.</summary>
    public static List<LevelCost> Costs(int gid, int fromLevel, int toLevel, double serverSpeed, int mainBuildingLevel, double timeFactor = 1)
    {
        var result = new List<LevelCost>();
        for (var level = Math.Max(1, fromLevel + 1); level <= Math.Min(toLevel, BuildingCatalogService.MaxLevelFor(gid)); level++)
        {
            if (BuildingCatalogService.CostFor(gid, level) is { } cost)
            {
                result.Add(new LevelCost(
                    level, cost.Wood, cost.Clay, cost.Iron, cost.Crop, cost.Upkeep, cost.CulturePoints,
                    (int)Math.Ceiling(BuildingCatalogService.BuildSecondsFor(gid, level, serverSpeed, Math.Max(1, mainBuildingLevel)) * timeFactor)));
            }
        }

        return result;
    }

    /// <summary>
    /// Walks a list of build items one level at a time against the village's stock and production:
    /// wait until the level is affordable, pay, build, repeat. One thing is built at a time.
    /// </summary>
    public static PlanEstimate Estimate(VillageView village, IReadOnlyList<BuildItem> items, double serverSpeed, double timeFactor = 1, int timeSamples = 0)
    {
        var assumptions = new List<string>
        {
            timeSamples > 0
                ? $"Build times use the standard tables corrected by ×{timeFactor.ToString("0.##", CultureInfo.InvariantCulture)}, learned from {timeSamples} build(s) seen on this world."
                : "Build times use the standard tables; they are corrected once the bot has watched a few builds on this world.",
            "One construction at a time (no second build slot).",
            "Production stays at today's rate; field upgrades in the plan would make it faster.",
            "No resources spent on troops, and none added by raids, the hero or trades.",
        };
        var stockEntries = village.Stock ?? [];
        if (stockEntries.Count == 0 || village.UpdatedUtc is null)
        {
            return new PlanEstimate([], 0, null, ["The village's stock and production have not been read yet. Scan the village first."]);
        }

        string[] names = ["wood", "clay", "iron", "crop"];
        var hoursSinceRead = Math.Max(0, (DateTimeOffset.UtcNow - village.UpdatedUtc.Value).TotalHours);
        var perHour = names.Select(name => stockEntries.FirstOrDefault(entry => entry.Resource == name)?.PerHour ?? 0).ToArray();
        var capacity = names.Select(name => (double)(stockEntries.FirstOrDefault(entry => entry.Resource == name)?.Capacity
            ?? (name == "crop" ? village.Granary : village.Warehouse) ?? 0)).ToArray();
        var stock = names.Select((name, index) => Math.Min(
            capacity[index],
            (stockEntries.FirstOrDefault(entry => entry.Resource == name)?.Current ?? 0) + (perHour[index] * hoursSinceRead))).ToArray();

        var state = new Projection(village, []);
        var estimates = new List<ItemEstimate>();
        double clock = 0;
        foreach (var item in items)
        {
            long wood = 0, clay = 0, iron = 0, crop = 0;
            double waited = 0, built = 0;
            var steps = 0;
            string? blocked = item.Type == "building_demolish" ? "Demolition is not estimated." : null;
            foreach (var (gid, from, to) in blocked is null ? LevelRanges(village, state, item) : [])
            {
                for (var level = from + 1; level <= to && blocked is null; level++)
                {
                    if (BuildingCatalogService.CostFor(gid, level) is not { } cost)
                    {
                        break;
                    }

                    double[] price = [cost.Wood, cost.Clay, cost.Iron, cost.Crop];
                    double wait = 0;
                    for (var index = 0; index < 4 && blocked is null; index++)
                    {
                        if (price[index] > capacity[index])
                        {
                            blocked = $"Level {level} costs {Number((long)price[index])} {names[index]}, more than storage holds ({Number((long)capacity[index])}).";
                        }
                        else if (price[index] > stock[index])
                        {
                            if (perHour[index] <= 0)
                            {
                                blocked = $"Not enough {names[index]} for level {level} and none is being produced.";
                            }
                            else
                            {
                                wait = Math.Max(wait, (price[index] - stock[index]) / perHour[index] * 3600);
                            }
                        }
                    }

                    if (blocked is not null)
                    {
                        break;
                    }

                    var duration = BuildingCatalogService.BuildSecondsFor(gid, level, serverSpeed, Math.Max(1, state.Buildings.GetValueOrDefault(15).Level)) * timeFactor;
                    for (var index = 0; index < 4; index++)
                    {
                        stock[index] = Math.Min(capacity[index], stock[index] + (perHour[index] * wait / 3600)) - price[index];
                        stock[index] = Math.Min(capacity[index], stock[index] + (perHour[index] * duration / 3600));
                    }

                    waited += wait;
                    built += duration;
                    steps++;
                    wood += cost.Wood;
                    clay += cost.Clay;
                    iron += cost.Iron;
                    crop += cost.Crop;
                }
            }

            clock += waited + built;
            state.Apply(item);
            estimates.Add(new ItemEstimate(
                BotEngine.DescribeBuildItem(item), steps, wood, clay, iron, crop, (int)Math.Ceiling(waited), (int)Math.Ceiling(built), (int)Math.Ceiling(clock), blocked));
            if (blocked is not null)
            {
                assumptions.Add("Items after a blocked one are estimated as if it were skipped.");
            }
        }

        return new PlanEstimate(estimates, (int)Math.Ceiling(clock), DateTimeOffset.UtcNow.AddSeconds(clock), assumptions.Distinct().ToList());
    }

    /// <summary>
    /// Crop check for one item: how much crop per hour the village will have left once everything
    /// already queued and this item are built. Buildings and fields eat crop; cropland adds it, which
    /// this estimate leaves out, so a warning errs on the safe side.
    /// </summary>
    public static string? CropWarning(VillageView village, IReadOnlyList<BuildItem> queue, BuildItem item)
    {
        var cropPerHour = village.Stock?.FirstOrDefault(entry => entry.Resource == "crop")?.PerHour;
        if (cropPerHour is null || item.Type == "building_demolish")
        {
            return null;
        }

        var state = new Projection(village, []);
        long queued = 0;
        foreach (var earlier in queue)
        {
            queued += Upkeep(village, state, earlier);
            state.Apply(earlier);
        }

        var own = Upkeep(village, state, item);
        var left = cropPerHour.Value - queued - own;
        if (own <= 0 || left >= 0)
        {
            return null;
        }

        return $"Crop warning: {BotEngine.DescribeBuildItem(item)} adds {Number(own)} crop per hour of upkeep"
            + (queued > 0 ? $" on top of {Number(queued)} already queued" : string.Empty)
            + $". The village makes {Number((long)cropPerHour.Value)} per hour, so it would end about {Number((long)-left)} per hour short. Queue cropland first.";
    }

    private static long Upkeep(VillageView village, Projection state, BuildItem item)
    {
        long upkeep = 0;
        foreach (var (gid, from, to) in LevelRanges(village, state, item))
        {
            // Cropland produces crop instead of consuming it.
            if (gid != 4 && to > from)
            {
                upkeep += BuildingCatalogService.AdditionalUpkeepForUpgrade(gid, from, to) ?? 0;
            }
        }

        return upkeep;
    }

    // The largest single-level cost the item will hit: (wood/clay/iron maximum, crop maximum).
    private static (long Warehouse, long Granary) MaxCost(VillageView village, Projection state, BuildItem item)
    {
        long warehouse = 0, granary = 0;
        foreach (var (gid, from, to) in LevelRanges(village, state, item))
        {
            for (var level = from + 1; level <= to; level++)
            {
                if (BuildingCatalogService.CostFor(gid, level) is { } cost)
                {
                    warehouse = Math.Max(warehouse, Math.Max(cost.Wood, Math.Max(cost.Clay, cost.Iron)));
                    granary = Math.Max(granary, cost.Crop);
                }
            }
        }

        return (warehouse, granary);
    }

    // Which building types the item raises, and between which levels.
    private static List<(int Gid, int From, int To)> LevelRanges(VillageView village, Projection state, BuildItem item)
    {
        var ranges = new List<(int Gid, int From, int To)>();
        switch (item.Type)
        {
            case "resources_all":
                foreach (var type in item.ResourceTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (FieldGids.TryGetValue(type, out var fieldGid))
                    {
                        // One range per field: costs peak at the lowest field, upkeep adds up over all.
                        foreach (var field in village.Fields.Where(field => string.Equals(field.Type, type, StringComparison.OrdinalIgnoreCase)))
                        {
                            ranges.Add((fieldGid, field.Level ?? 0, item.TargetLevel));
                        }
                    }
                }

                break;
            case "resource_field":
                var target = village.Fields.FirstOrDefault(field => field.SlotId == item.SlotId);
                if (target is not null && FieldGids.TryGetValue(target.Type, out var gidOfField))
                {
                    ranges.Add((gidOfField, target.Level ?? 0, item.TargetLevel));
                }

                break;
            case "building_construct" when item.Gid is int constructGid:
                ranges.Add((constructGid, 0, item.TargetLevel));
                break;
            case "building_upgrade" or "building_max":
                if ((item.Gid ?? state.GidAtSlot(item.SlotId)) is int upgradeGid)
                {
                    var to = item.Type == "building_max" ? BuildingCatalogService.MaxLevelFor(upgradeGid) : item.TargetLevel;
                    ranges.Add((upgradeGid, state.LevelAtSlot(item.SlotId), to));
                }

                break;
        }

        return ranges;
    }

    /// <summary>The village's buildings as they will be once everything already queued has run.</summary>
    private sealed class Projection
    {
        private readonly Dictionary<int, (int Gid, int Level)> _slots = [];
        private readonly Dictionary<int, int> _villageLevels = [];
        private readonly SortedSet<int> _freeSlots = [];

        public Projection(VillageView village, IReadOnlyList<BuildItem> queue)
        {
            foreach (var building in village.Buildings)
            {
                if (building.SlotId is not int slot)
                {
                    continue;
                }

                var gid = building.Gid ?? BuildingCatalogService.GidForName(building.Name);
                if (gid is > 0 && !string.Equals(building.Name, "Empty", StringComparison.OrdinalIgnoreCase))
                {
                    _slots[slot] = (gid.Value, building.Level ?? 0);
                    _villageLevels[gid.Value] = Math.Max(_villageLevels.GetValueOrDefault(gid.Value), building.Level ?? 0);
                }
                else if (slot is >= 19 and <= 38)
                {
                    _freeSlots.Add(slot);
                }
            }

            foreach (var item in queue)
            {
                Apply(item);
            }
        }

        /// <summary>Highest projected level per building type, with the slot that holds it.</summary>
        public Dictionary<int, (int Slot, int Level)> Buildings
            => _slots.GroupBy(pair => pair.Value.Gid)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(pair => pair.Value.Level).Select(pair => (pair.Key, pair.Value.Level)).First());

        public int VillageLevel(int gid) => _villageLevels.GetValueOrDefault(gid);

        public int? GidAtSlot(int? slot) => slot is int id && _slots.TryGetValue(id, out var entry) ? entry.Gid : null;

        public int LevelAtSlot(int? slot) => slot is int id && _slots.TryGetValue(id, out var entry) ? entry.Level : 0;

        public int? TakeFreeSlot()
        {
            if (_freeSlots.Count == 0)
            {
                return null;
            }

            var slot = _freeSlots.Min;
            _freeSlots.Remove(slot);
            return slot;
        }

        public void Apply(BuildItem item)
        {
            if (item.SlotId is not int slot)
            {
                return;
            }

            switch (item.Type)
            {
                case "building_construct" when item.Gid is int gid:
                    _slots[slot] = (gid, item.TargetLevel);
                    _freeSlots.Remove(slot);
                    break;
                case "building_upgrade" when _slots.TryGetValue(slot, out var existing):
                    _slots[slot] = (existing.Gid, Math.Max(existing.Level, item.TargetLevel));
                    break;
                case "building_max" when _slots.TryGetValue(slot, out var maxed):
                    _slots[slot] = (maxed.Gid, BuildingCatalogService.MaxLevelFor(maxed.Gid));
                    break;
                case "building_demolish" when _slots.TryGetValue(slot, out var demolished):
                    _slots[slot] = (demolished.Gid, Math.Min(demolished.Level, item.TargetLevel));
                    break;
            }
        }
    }
}
