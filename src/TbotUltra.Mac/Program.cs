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

    try
    {
        await next();
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
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
        alert = engine.Alert,
        prompt = engine.Prompt,
        tribe = engine.Tribe,
        hero = engine.Hero,
        farmLists = engine.FarmLists,
        villages = engine.Villages(active),
        jobs = engine.Jobs(),
        settings = active.Length == 0 ? new MacSettings() : settings.Load(active),
    });
});

app.MapGet("/api/logs", (long after) => Results.Json(log.ReadAfter(after)));

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
        .Select(match => new TbotUltra.Worker.Domain.FarmCoordinate(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)))
        .ToList();
    if (coordinates.Count == 0)
    {
        throw new InvalidOperationException("Enter at least one coordinate such as 12|-34.");
    }

    return Fire(() => engine.AddFarmTargetsAsync(request.ListName, request.TroopType, request.TroopCount, coordinates));
});
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
        village.SmithyTargets = patch.SmithyTargets ?? village.SmithyTargets;
        if (patch.Troops is not null)
        {
            village.Troops = new Dictionary<string, string>(patch.Troops, StringComparer.OrdinalIgnoreCase);
        }
    });
    return Results.Ok();
});

app.MapPost("/api/villages/{key}/build", (string key, BuildItem item) =>
{
    if (item.Type is not ("resources_all" or "resource_field" or "building_upgrade" or "building_construct"))
    {
        throw new InvalidOperationException($"Unknown build item type '{item.Type}'.");
    }

    item.Id = Guid.NewGuid().ToString("N")[..10];
    item.Status = "pending";
    item.Note = string.Empty;
    item.StallCount = 0;
    item.TargetLevel = Math.Clamp(item.TargetLevel, 1, 100);
    settings.Update(RequireAccount(), current => RequireVillage(current, key).BuildQueue.Add(item));
    return Results.Json(new { item.Id });
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
log.Add($"[mac] Tbot Ultra for macOS {ReadVersion()} — data folder: {root}");

// The native window shell reads this line to learn where the UI is served.
Console.WriteLine($"TBOT_LISTENING {address}");
if (args.Contains("--open"))
{
    Process.Start(new ProcessStartInfo("/usr/bin/open", address) { UseShellExecute = false });
}

await app.WaitForShutdownAsync();

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

internal sealed record PageRequest(string Path);

internal sealed record FarmListCreate(string Name, string VillageKey, string TroopType, int TroopCount);

internal sealed record FarmTargetsAdd(string ListName, string TroopType, int TroopCount, string? Coordinates);

internal sealed record FarmListSelection(List<string>? Names, List<string>? Ids);

internal sealed record VillagePatch(bool? Enabled, bool? TownHall, string? SmithyTargets, Dictionary<string, string>? Troops);
