using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using TbotUltra.Core.Travian;
using TbotUltra.Mac;
using TbotUltra.Worker.Services;

var root = MacPaths.ResolveDataRoot();
var port = ResolvePort(args);
var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var log = new LogBuffer(Path.Combine(root, "logs"));
var accounts = new AccountStore(Path.Combine(root, ".env"));
var settings = new SettingsStore(root);
var engine = new BotEngine(root, accounts, settings, log);
var templates = new TemplateStore(root);

var app = builder.Build();

// The API drives a logged-in game session, so only this app's own page may call it: requests must
// target the loopback host (blocks DNS rebinding) and carry the per-launch token (blocks other sites).
app.Use(async (context, next) =>
{
    var host = context.Request.Host.Host;
    if (host is not ("127.0.0.1" or "localhost"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    if (context.Request.Path.StartsWithSegments("/api")
        && !string.Equals(context.Request.Headers["X-Tbot-Token"], token, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    // Agents identify themselves with X-Tbot-Agent so every change they make is visible in the log.
    if (context.Request.Headers.TryGetValue("X-Tbot-Agent", out var agentName)
        && !HttpMethods.IsGet(context.Request.Method))
    {
        var name = new string(agentName.ToString().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ' ').Take(40).ToArray());
        log.Add($"[agent:{name}] {context.Request.Method} {context.Request.Path}");
    }

    try
    {
        await next();
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or System.Text.Json.JsonException or BadHttpRequestException)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

app.MapGet("/", () => Results.Content(
    MacPaths.ReadEmbeddedText("TbotUltra.Mac.Web.index.html").Replace("__TBOT_TOKEN__", token, StringComparison.Ordinal),
    "text/html; charset=utf-8"));

app.MapGet("/api/state", () =>
{
    var active = accounts.ActiveAccountName();
    return Results.Json(new
    {
        version = ReadVersion(),
        dataRoot = root,
        accounts = accounts.List().Select(account => new
        {
            account.Name,
            account.Username,
            account.ServerName,
            account.ServerUrl,
            account.ManualLogin,
            account.ProxyEnabled,
            account.ProxyServer,
            hasPassword = account.Password.Length > 0,
        }),
        activeAccount = active,
        loggedIn = engine.LoggedIn,
        running = engine.Running,
        busy = engine.Busy,
        activity = engine.Activity,
        sleepingUntil = engine.SleepingUntilUtc,
        alert = engine.Alert,
        prompt = engine.Prompt,
        tribe = engine.Tribe,
        hero = engine.Hero,
        scan = engine.Scan,
        account = engine.Account,
        events = engine.Events(),
        attacks = engine.Attacks(),
        culture = engine.Culture,
        celebrationsWanted = active.Length == 0 || engine.CelebrationsWanted(settings.Load(active)),
        today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        settleSpots = engine.SettleSpots,
        catapultSetup = engine.CatapultSetup,
        farmLists = engine.FarmLists,
        villages = engine.Villages(active),
        jobs = engine.Jobs(),
        settings = active.Length == 0 ? new MacSettings() : settings.Load(active),
        defaults = MacDefaults.Options,
        templates = templates.Load(),
    });
});

app.MapGet("/api/logs", (long after) => Results.Json(log.ReadAfter(after)));

app.MapGet("/api/agent/summary", () => Results.Json(BuildSummary()));

app.MapGet("/api/agent/guide", () => Results.Text(MacPaths.ReadEmbeddedText("TbotUltra.Mac.Agent.guide.md"), "text/markdown; charset=utf-8"));

// Replaces a village's whole build queue in one call: the natural shape for a planning agent.
app.MapPut("/api/villages/{key}/build", (string key, QueueReplacement request) =>
{
    var items = request.Items ?? [];
    var allowed = new[] { "resources_all", "resource_field", "building_upgrade", "building_max", "building_construct", "building_demolish" };
    foreach (var item in items)
    {
        if (!allowed.Contains(item.Type))
        {
            throw new InvalidOperationException($"Unknown build item type '{item.Type}'. Use one of: {string.Join(", ", allowed)}.");
        }

        if (item.Type != "resources_all" && item.SlotId is null)
        {
            throw new InvalidOperationException($"Build item '{item.Type}' needs slotId.");
        }

        if (item.Type == "building_construct" && item.Gid is null)
        {
            throw new InvalidOperationException("building_construct needs gid (see GET /api/catalog).");
        }
    }

    var account = RequireAccount();
    var village = RequireVillageView(account, key);
    var notes = new List<string>();
    var queued = new List<BuildItem>();
    settings.Update(account, current =>
    {
        foreach (var item in items)
        {
            item.Id = Guid.NewGuid().ToString("N")[..10];
            item.Status = "pending";
            item.Note = string.Empty;
            item.StallCount = 0;
            item.TargetLevel = Math.Clamp(item.TargetLevel, 0, 100);
            FillName(item, village);

            if (request.Preflight ?? StoragePreflightEnabled(current))
            {
                var plan = BuildPlanner.PlanStorage(village, queued, item);
                queued.AddRange(plan.Items);
                notes.AddRange(plan.Notes);
            }

            if (BuildPlanner.CropWarning(village, queued, item) is { } cropWarning)
            {
                notes.Add(cropWarning);
            }

            queued.Add(item);
        }

        RequireVillage(current, key).BuildQueue = queued;
    });
    engine.RunJobNow($"build:{key}");
    return Results.Json(new { queue = queued, notes });
});

app.MapGet("/api/catalog", (string? tribe) =>
{
    var resolvedTribe = string.IsNullOrWhiteSpace(tribe) ? engine.Tribe : tribe;
    return Results.Json(new
    {
        buildings = BuildingCatalogService.GetCatalogForTribe(resolvedTribe)
            .Where(entry => !string.Equals(entry.Category, "resource_field", StringComparison.OrdinalIgnoreCase))
            .Select(entry => new
            {
                entry.Gid,
                entry.Name,
                maxLevel = BuildingCatalogService.MaxLevelFor(entry.Gid),
                requirements = entry.Requirements.Select(requirement => $"{requirement.Name} {requirement.Level}"),
            }),
        troops = new
        {
            barracks = TroopCatalog.ResolveTroopTypesForTribe(resolvedTribe, TroopTrainingBuildingType.Barracks),
            stable = TroopCatalog.ResolveTroopTypesForTribe(resolvedTribe, TroopTrainingBuildingType.Stable),
            workshop = TroopCatalog.ResolveTroopTypesForTribe(resolvedTribe, TroopTrainingBuildingType.Workshop),
            all = TroopCatalog.ResolveTroopTypesForTribe(resolvedTribe)
                .Select(name => new { name, index = TroopCatalog.ResolveTroopIndex(name) }),
        },
    });
});

app.MapPost("/api/accounts", (AccountEntry account) =>
{
    RequireStopped();
    var saved = accounts.Save(account, setActive: true);
    log.Add($"[mac] account '{saved.Name}' saved for {saved.ServerUrl}.");
    return Results.Json(new { saved.Name });
});

app.MapPost("/api/accounts/{name}/activate", (string name) =>
{
    RequireStopped();
    accounts.SetActive(name);
    return Results.Ok();
});

app.MapDelete("/api/accounts/{name}", (string name) =>
{
    RequireStopped();
    accounts.Delete(name);
    log.Add($"[mac] account '{name}' removed.");
    return Results.Ok();
});

app.MapPost("/api/login", () => Fire(engine.LoginAsync));
app.MapPost("/api/scan", () => Fire(engine.ScanVillagesAsync));
app.MapPost("/api/farmlists/refresh", () => Fire(engine.RefreshFarmListsAsync));
app.MapPost("/api/farmlists/create", (FarmListCreate request) =>
    Fire(() => engine.CreateFarmListAsync(request.Name, request.VillageKey, request.TroopType, request.TroopCount)));
app.MapPost("/api/farmlists/targets", (FarmTargetsAdd request) =>
{
    // Accepts "12|-34", "(12|-34)" or "12,-34", separated by spaces, semicolons or new lines.
    var coordinates = System.Text.RegularExpressions.Regex.Matches(request.Coordinates ?? string.Empty, @"(-?\d+)\s*[|,/]\s*(-?\d+)")
        .Select(match => new TbotUltra.Worker.Domain.FarmCoordinate(
            int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), RequireUnoccupiedOasis: request.Oases, IsOasis: request.Oases))
        .ToList();
    if (coordinates.Count == 0)
    {
        throw new InvalidOperationException("Enter at least one coordinate such as 12|-34.");
    }

    return Fire(() => engine.AddFarmTargetsAsync(request.ListName, request.TroopType, request.TroopCount, coordinates));
});
app.MapPost("/api/inbox/read", () => Fire(engine.MarkInboxReadAsync));
app.MapPost("/api/expansion/culture", () => Fire(engine.ReadCultureAsync));
app.MapPost("/api/expansion/spots", (SpotSearch request) =>
    Fire(() => engine.FindSettleSpotsAsync(request.VillageKey, Math.Clamp(request.Radius, 1, 40))));

// Queues what a village needs before it can train settlers: a Residence at level 10.
app.MapPost("/api/villages/{key}/expansion/prepare", (string key) =>
{
    var account = RequireAccount();
    var village = RequireVillageView(account, key);
    PlanResult? plan = null;
    settings.Update(account, current =>
    {
        var queue = RequireVillage(current, key).BuildQueue;
        var template = new BuildTemplate
        {
            Name = "Expansion",
            Steps =
            [
                new TemplateStep { Kind = "building", Gid = 15, Name = "Main Building", TargetLevel = 5 },
                new TemplateStep { Kind = "building", Gid = 25, Name = "Residence", TargetLevel = 10 },
            ],
        };

        // A Palace or Command Center already does the Residence's job.
        if (village.Buildings.Any(building => building.Gid is 26 or 44))
        {
            var seat = village.Buildings.First(building => building.Gid is 26 or 44);
            template.Steps[1] = new TemplateStep { Kind = "building", Gid = seat.Gid, Name = seat.Name, TargetLevel = 10 };
        }

        plan = BuildPlanner.PlanTemplate(template, village, queue, engine.Tribe);
        queue.AddRange(plan.Items);
    });
    engine.RunJobNow($"build:{key}");
    return Results.Json(new { added = plan!.Items.Count, notes = plan.Notes });
});
app.MapPost("/api/hero/bonus/{kind}", (string kind) => Fire(() => engine.RunAdventureBonusAsync(kind)));
app.MapPost("/api/production-bonus", () => Fire(engine.RunProductionBonusAsync));
app.MapPost("/api/scan/oases", (OasisScanRequest request) =>
    Fire(() => engine.ScanOasesAsync(request.X, request.Y, request.Radius, request.IncludeOccupied, request.Types ?? [])));
app.MapPost("/api/scan/travco", (TravcoRequest request) =>
    Fire(() => engine.SearchTravcoAsync(request.X, request.Y, Math.Max(1, request.DaysInactive), request.OrderBy ?? "distance")));
app.MapPost("/api/catapults/setup", (VillageRequest request) => Fire(() => engine.ReadCatapultSetupAsync(request.VillageKey)));
app.MapPost("/api/catapults/send", (CatapultPlan plan) => Fire(() => engine.StartCatapultWavesAsync(plan)));
app.MapPost("/api/hero/refresh", () => Fire(engine.RefreshHeroAsync));
app.MapPost("/api/language/english", () => Fire(engine.SetLanguageToEnglishAsync));
app.MapPost("/api/diagnostics/page", (PageRequest request) => Fire(() => engine.SavePageHtmlAsync(request.Path)));
app.MapPost("/api/logout", () => Fire(engine.LogoutAsync));
app.MapPost("/api/cancel", () =>
{
    engine.CancelManual();
    return Results.Ok();
});

app.MapPost("/api/start", () =>
{
    engine.Start();
    return Results.Ok();
});

app.MapPost("/api/stop", () => Fire(engine.StopAsync));

app.MapPost("/api/daily/send", () =>
{
    engine.SendDailySummary(RequireAccount());
    return Results.Ok();
});

app.MapPost("/api/notify/test", () =>
{
    engine.TestNotification();
    return Results.Ok();
});

app.MapPost("/api/wake", () =>
{
    engine.WakeNow();
    return Results.Ok();
});

app.MapPut("/api/settings/startup", (StartupSettings startup) =>
{
    settings.Update(RequireAccount(), current => current.Startup = startup);
    return Results.Ok();
});

app.MapPost("/api/farmlists/release", (FarmTargetKey target) =>
{
    engine.ReleaseFarmTarget(target.Key);
    return Results.Ok();
});

app.MapPost("/api/alert/clear", () =>
{
    engine.ClearAlert();
    return Results.Ok();
});

app.MapPost("/api/prompt", (PromptAnswer answer) =>
{
    engine.AnswerPrompt(answer.Id, answer.Value);
    return Results.Ok();
});

app.MapPost("/api/jobs/run", (JobRequest request) =>
{
    engine.RunJobNow(request.Key);
    return Results.Ok();
});

app.MapPut("/api/settings/features", (FeatureToggles features) =>
{
    settings.Update(RequireAccount(), current => current.Features = features);
    return Results.Ok();
});

app.MapPut("/api/settings/options", (Dictionary<string, string> options) =>
{
    settings.Update(RequireAccount(), current =>
    {
        foreach (var pair in options)
        {
            current.Options[pair.Key] = pair.Value;
        }
    });
    return Results.Ok();
});

app.MapPut("/api/settings/farmlists", (FarmListSelection selection) =>
{
    settings.Update(RequireAccount(), current =>
    {
        current.FarmListNames = selection.Names ?? [];
        current.FarmListIds = selection.Ids ?? [];
    });
    return Results.Ok();
});

app.MapPut("/api/villages/{key}", (string key, VillagePatch patch) =>
{
    settings.Update(RequireAccount(), current =>
    {
        var village = RequireVillage(current, key);
        village.Enabled = patch.Enabled ?? village.Enabled;
        village.TownHall = patch.TownHall ?? village.TownHall;
        village.Brewery = patch.Brewery ?? village.Brewery;
        village.SmithyTargets = patch.SmithyTargets ?? village.SmithyTargets;
        village.AcademyTargets = patch.AcademyTargets ?? village.AcademyTargets;
        village.Traps = patch.Traps ?? village.Traps;
        village.TrapsPerRun = Math.Clamp(patch.TrapsPerRun ?? village.TrapsPerRun, 0, 100000);
        if (patch.Troops is not null)
        {
            village.Troops = new Dictionary<string, string>(patch.Troops, StringComparer.OrdinalIgnoreCase);
        }
    });
    return Results.Ok();
});

app.MapPost("/api/villages/{key}/build", (string key, BuildItem item) =>
{
    if (item.Type is not ("resources_all" or "resource_field" or "building_upgrade" or "building_max" or "building_construct" or "building_demolish"))
    {
        throw new InvalidOperationException($"Unknown build item type '{item.Type}'.");
    }

    item.Id = Guid.NewGuid().ToString("N")[..10];
    item.Status = "pending";
    item.Note = string.Empty;
    item.StallCount = 0;
    item.TargetLevel = Math.Clamp(item.TargetLevel, 0, 100);
    var account = RequireAccount();
    var village = RequireVillageView(account, key);
    var notes = new List<string>();
    settings.Update(account, current =>
    {
        var queue = RequireVillage(current, key).BuildQueue;
        if (StoragePreflightEnabled(current))
        {
            // Storage that is too small would leave the item waiting forever, so queue the upgrade first.
            var plan = BuildPlanner.PlanStorage(village, queue, item);
            queue.AddRange(plan.Items);
            notes.AddRange(plan.Notes);
        }

        if (BuildPlanner.CropWarning(village, queue, item) is { } cropWarning)
        {
            notes.Add(cropWarning);
        }

        queue.Add(item);
    });
    return Results.Json(new { item.Id, notes });
});

// Planning data: what each level costs and how long it takes in a given village.
app.MapGet("/api/planning/costs", (int gid, int? from, int? to, string? villageKey) =>
{
    var account = RequireAccount();
    var village = villageKey is null ? engine.Villages(account).FirstOrDefault() : RequireVillageView(account, villageKey);
    var mainBuilding = village?.Buildings.FirstOrDefault(building => building.Gid == 15)?.Level ?? 1;
    var current = village?.Buildings.Where(building => building.Gid == gid).Select(building => building.Level ?? 0).DefaultIfEmpty(0).Max() ?? 0;
    var fromLevel = from ?? current;
    return Results.Json(new
    {
        gid,
        name = BuildingCatalogService.NameForGid(gid),
        maxLevel = BuildingCatalogService.MaxLevelFor(gid),
        currentLevel = current,
        serverSpeed = ServerSpeed(account),
        mainBuildingLevel = mainBuilding,
        requirements = BuildingCatalogService.RequirementsFor(gid).Select(requirement => new { requirement.Name, requirement.Level }),
        fasterVideo = Video(settings.Load(account)),
        levels = BuildPlanner.Costs(
            gid, fromLevel, to ?? BuildingCatalogService.MaxLevelFor(gid), ServerSpeed(account), mainBuilding,
            Video(settings.Load(account))),
    });
});

// How long the current queue (GET) or a proposed list of items (POST) would take in this village.
app.MapGet("/api/villages/{key}/plan/estimate", (string key) =>
{
    var account = RequireAccount();
    var current = settings.Load(account);
    var queue = RequireVillage(current, key).BuildQueue.Where(item => item.Status == "pending").ToList();
    return Results.Json(BuildPlanner.Estimate(
        RequireVillageView(account, key), queue, ServerSpeed(account), Video(current), HeroResources(current), engine.HeroDown));
});

app.MapPost("/api/villages/{key}/plan/estimate", (string key, QueueReplacement request) =>
{
    var account = RequireAccount();
    var village = RequireVillageView(account, key);
    foreach (var item in request.Items ?? [])
    {
        FillName(item, village);
    }

    return Results.Json(BuildPlanner.Estimate(
        village, request.Items ?? [], ServerSpeed(account), Video(settings.Load(account)), HeroResources(settings.Load(account)), engine.HeroDown));
});

app.MapPut("/api/templates", (BuildTemplate template) =>
{
    template.Id = string.IsNullOrWhiteSpace(template.Id) ? Guid.NewGuid().ToString("N")[..10] : template.Id;
    return Results.Json(templates.Save(template));
});

app.MapDelete("/api/templates/{id}", (string id) =>
{
    templates.Delete(id);
    return Results.Ok();
});

app.MapPost("/api/villages/{key}/template/{id}", (string key, string id) =>
{
    var template = templates.Load().FirstOrDefault(item => item.Id == id)
        ?? throw new InvalidOperationException("That template no longer exists.");
    var account = RequireAccount();
    var village = RequireVillageView(account, key);
    PlanResult? plan = null;
    settings.Update(account, current =>
    {
        var queue = RequireVillage(current, key).BuildQueue;
        plan = BuildPlanner.PlanTemplate(template, village, queue, engine.Tribe);
        queue.AddRange(plan.Items);
    });
    engine.RunJobNow($"build:{key}");
    return Results.Json(new { added = plan!.Items.Count, notes = plan.Notes });
});

app.MapPost("/api/villages/{key}/template-from-queue", (string key, TemplateName request) =>
{
    var account = RequireAccount();
    var village = RequireVillageView(account, key);
    var queue = RequireVillage(settings.Load(account), key).BuildQueue;
    var steps = new List<TemplateStep>();
    foreach (var item in queue)
    {
        if (item.Type == "resources_all")
        {
            steps.Add(new TemplateStep { Kind = "resources", Name = "Resource fields", TargetLevel = item.TargetLevel, ResourceTypes = item.ResourceTypes });
        }
        else if (item.Type is "building_upgrade" or "building_max" or "building_construct")
        {
            var gid = item.Gid ?? village.Buildings.FirstOrDefault(building => building.SlotId == item.SlotId)?.Gid;
            if (gid is int value)
            {
                var level = item.Type == "building_max" ? BuildingCatalogService.MaxLevelFor(value) : item.TargetLevel;
                steps.Add(new TemplateStep { Kind = "building", Gid = value, Name = BuildingCatalogService.NameForGid(value), TargetLevel = level });
            }
        }
    }

    if (steps.Count == 0)
    {
        throw new InvalidOperationException("The build queue has nothing a template can hold. Add some items first.");
    }

    return Results.Json(templates.Save(new BuildTemplate { Name = request.Name, Steps = steps }));
});

app.MapPost("/api/villages/{key}/build/{id}/{action}", (string key, string id, string action) =>
{
    settings.Update(RequireAccount(), current =>
    {
        var queue = RequireVillage(current, key).BuildQueue;
        var index = queue.FindIndex(item => item.Id == id);
        if (index < 0)
        {
            return;
        }

        var item = queue[index];
        switch (action)
        {
            case "remove":
                queue.RemoveAt(index);
                break;
            case "up" when index > 0:
                (queue[index - 1], queue[index]) = (queue[index], queue[index - 1]);
                break;
            case "down" when index < queue.Count - 1:
                (queue[index + 1], queue[index]) = (queue[index], queue[index + 1]);
                break;
            case "retry":
                item.Status = "pending";
                item.Note = string.Empty;
                item.StallCount = 0;
                break;
        }
    });
    engine.RunJobNow($"build:{key}");
    return Results.Ok();
});

app.MapPost("/api/quit", () =>
{
    _ = Task.Run(async () =>
    {
        await engine.LogoutAsync();
        await app.StopAsync();
    });
    return Results.Ok();
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    // Close the automated browser with the app so no orphaned Chrome window is left behind.
    try
    {
        engine.LogoutAsync().Wait(TimeSpan.FromSeconds(15));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Shutdown cleanup failed: {ex.Message}");
    }
});

await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
var agentFiles = new AgentFiles(root, address, token, () => BuildSummary());
agentFiles.Start();
app.Lifetime.ApplicationStopping.Register(agentFiles.Stop);
log.Add($"[mac] Tbot Ultra for macOS {ReadVersion()} — data folder: {root}");

_ = Task.Run(async () =>
{
    try
    {
        await engine.RunStartupAsync();
    }
    catch (Exception ex)
    {
        log.Add($"[mac] automatic startup failed: {ex.Message}");
    }
});

// The native window shell reads this line to learn where the UI is served.
Console.WriteLine($"TBOT_LISTENING {address}");
if (args.Contains("--open"))
{
    Process.Start(new ProcessStartInfo("/usr/bin/open", address) { UseShellExecute = false });
}

await app.WaitForShutdownAsync();

object BuildSummary()
{
    var active = accounts.ActiveAccountName();
    var account = accounts.Find(active);
    var current = active.Length == 0 ? new MacSettings() : settings.Load(active);
    var now = DateTimeOffset.UtcNow;
    return new
    {
        generatedAtUtc = now,
        status = new
        {
            loggedIn = engine.LoggedIn,
            running = engine.Running,
            sleepingUntilUtc = engine.SleepingUntilUtc,
            busy = engine.Busy,
            activity = engine.Activity,
            alert = engine.Alert,
            prompt = engine.Prompt,
        },
        account = new
        {
            name = active,
            server = account?.ServerUrl,
            world = account?.ServerName,
            tribe = engine.Tribe,
            gold = engine.Account?.Gold,
            silver = engine.Account?.Silver,
            unreadMessages = engine.Account?.UnreadMessages,
            unreadReports = engine.Account?.UnreadReports,
        },
        hero = engine.Hero,
        features = current.Features,
        villages = engine.Villages(active).Select(village =>
        {
            current.Villages.TryGetValue(village.Key, out var villageSettings);
            return new
            {
                village.Key,
                village.Name,
                village.X,
                village.Y,
                village.IsCapital,
                village.Population,
                automated = villageSettings?.Enabled ?? true,
                village.UpdatedUtc,
                stock = village.Stock,
                cropBalancePerHour = village.Stock?.FirstOrDefault(entry => entry.Resource == "crop")?.PerHour,
                freeCrop = village.FreeCrop,
                warehouseCapacity = village.Warehouse,
                granaryCapacity = village.Granary,
                fields = village.Fields,
                buildings = village.Buildings.Where(building => !string.Equals(building.Name, "Empty", StringComparison.OrdinalIgnoreCase)),
                emptySlots = village.Buildings.Where(building => string.Equals(building.Name, "Empty", StringComparison.OrdinalIgnoreCase)).Select(building => building.SlotId),
                underConstruction = village.Constructions.Where(item => item.FinishUtc is null || item.FinishUtc > now),
                training = village.Training,
                buildQueue = villageSettings?.BuildQueue ?? [],
                troopRules = villageSettings?.Troops ?? [],
            };
        }),
        farming = new
        {
            lists = engine.FarmLists,
            selectedLists = current.FarmListNames,
            raidsOut = current.FarmRaids.BusyUntilUtc.Where(pair => pair.Value > now).ToDictionary(pair => pair.Key, pair => pair.Value),
            onHold = current.FarmRaids.OnHold,
            raidScale = current.FarmRaids.TroopScale,
        },
        daily = current.Daily.OrderByDescending(pair => pair.Key, StringComparer.Ordinal).Take(7)
            .ToDictionary(pair => pair.Key, pair => pair.Value),
        incomingAttacks = engine.Attacks(),
        expansion = new
        {
            culturePoints = engine.Culture,
            slotFree = engine.Culture?.SlotFree,
            celebrationsWanted = engine.CelebrationsWanted(current),
            enoughCulturePointsAtUtc = engine.Culture?.ReadyAtUtc,
            settleSpots = engine.SettleSpots.Take(40),
        },
        jobs = engine.Jobs(),
        recentEvents = engine.Events(),
    };
}

IResult Fire(Func<Task> action)
{
    _ = Task.Run(async () =>
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            log.Add($"[mac] {ex.GetType().Name}: {ex.Message}");
        }
    });
    return Results.Accepted();
}

string RequireAccount()
{
    var name = accounts.ActiveAccountName();
    return name.Length > 0 ? name : throw new InvalidOperationException("Add an account first.");
}

// Speed worlds carry their multiplier in the host name, e.g. "rog.x10.international.travian.com".
// How the bot is set to use the "25% faster build" video, for time estimates.
BuildPlanner.FasterVideo? Video(MacSettings current)
{
    string Option(string key) => current.Options.GetValueOrDefault(key, MacDefaults.Options.GetValueOrDefault(key, string.Empty));
    bool On(string key) => string.Equals(Option(key), "true", StringComparison.OrdinalIgnoreCase);
    double Number(string key, double fallback)
        => double.TryParse(Option(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

    if (!On("construct_faster_enabled"))
    {
        return null;
    }

    return new BuildPlanner.FasterVideo(
        On("construct_faster_min_build_time_enabled") ? Number("construct_faster_min_build_minutes", 30) * 60 : 0,
        On("construct_faster_random_enabled") ? Math.Clamp(Number("construct_faster_random_chance_percent", 50), 0, 100) / 100 : 1);
}

// What the hero carries, when the bot is allowed to spend it on construction.
long[]? HeroResources(MacSettings current)
{
    var allowed = current.Features.HeroResources
        && !string.Equals(current.Options.GetValueOrDefault("hero_resource_use_construction", MacDefaults.Options["hero_resource_use_construction"]), "false", StringComparison.OrdinalIgnoreCase);
    return allowed && engine.Hero?.Inventory is { } inventory ? [inventory.Wood, inventory.Clay, inventory.Iron, inventory.Crop] : null;
}

double ServerSpeed(string account) => BotEngine.WorldSpeed(accounts.Find(account)?.ServerUrl);

// Agents may leave the name out; the app works it out from the gid or the slot.
static void FillName(BuildItem item, VillageView village)
{
    if (string.IsNullOrWhiteSpace(item.Name))
    {
        item.Name = item.Gid is int gid
            ? BuildingCatalogService.NameForGid(gid)
            : (item.SlotId is <= 18 ? village.Fields.FirstOrDefault(field => field.SlotId == item.SlotId)?.Name : null)
              ?? village.Buildings.FirstOrDefault(building => building.SlotId == item.SlotId)?.Name
              ?? "Resource fields";
    }
}

VillageView RequireVillageView(string account, string key)
    => engine.Villages(account).FirstOrDefault(village => village.Key == key)
        ?? throw new InvalidOperationException("Unknown village. Log in or scan villages first.");

static bool StoragePreflightEnabled(MacSettings current)
    => string.Equals(
        current.Options.GetValueOrDefault("mac_storage_preflight") ?? MacDefaults.Options["mac_storage_preflight"],
        "true",
        StringComparison.OrdinalIgnoreCase);

void RequireStopped()
{
    if (engine.LoggedIn || engine.Running)
    {
        throw new InvalidOperationException("Log out before changing accounts.");
    }
}

static VillageSettings RequireVillage(MacSettings current, string key)
    => current.Villages.TryGetValue(key, out var village)
        ? village
        : throw new InvalidOperationException("Unknown village. Log in or scan villages first.");

static int ResolvePort(string[] args)
{
    var index = Array.IndexOf(args, "--port");
    if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var requested))
    {
        return requested;
    }

    // Any free loopback port: several copies (one per data folder) can then run side by side.
    using var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    return ((IPEndPoint)probe.LocalEndpoint).Port;
}

static string ReadVersion()
    => typeof(MacPaths).Assembly.GetName().Version?.ToString(3) ?? "dev";

internal sealed record PromptAnswer(string Id, string? Value);

internal sealed record JobRequest(string Key);

internal sealed record TemplateName(string Name);

internal sealed record SpotSearch(string VillageKey, int Radius);

internal sealed record QueueReplacement(List<BuildItem>? Items, bool? Preflight);

internal sealed record FarmTargetKey(string Key);

internal sealed record PageRequest(string Path);

internal sealed record FarmListCreate(string Name, string VillageKey, string TroopType, int TroopCount);

internal sealed record FarmTargetsAdd(string ListName, string TroopType, int TroopCount, string? Coordinates, bool Oases = false);

internal sealed record FarmListSelection(List<string>? Names, List<string>? Ids);

internal sealed record VillagePatch(
    bool? Enabled, bool? TownHall, bool? Brewery, string? SmithyTargets, Dictionary<string, string>? Troops,
    string? AcademyTargets = null, bool? Traps = null, int? TrapsPerRun = null);

internal sealed record VillageRequest(string VillageKey);

internal sealed record OasisScanRequest(int X, int Y, int Radius, bool IncludeOccupied, List<string>? Types);

internal sealed record TravcoRequest(int X, int Y, int DaysInactive, string? OrderBy);
