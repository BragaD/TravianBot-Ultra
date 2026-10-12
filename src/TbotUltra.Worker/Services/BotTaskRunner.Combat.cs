using TbotUltra.Core.Configuration;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Infrastructure;
using TbotUltra.Worker.Services.Automation;
using Microsoft.Playwright;
using System.Text.Json;

namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
    public async Task<TroopEvasionResult> SendTroopEvasionAsync(
        BotOptions options,
        TroopEvasionRequest request,
        Action<string> log,
        IProgress<TroopEvasionProgress>? progress = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        using var priorityRequest = _priorityBrowserWork.EnterPriorityRequest();
        TroopEvasionResult? result = null;
        await ExecuteWithClientAsync(options, log, accountName, interactive: false, cancellationToken, async client =>
        {
            await client.EnsureAccountAccessAllowedAsync(cancellationToken);
            result = await new CombatOperation(client).SendTroopEvasionAsync(request, progress, cancellationToken);
        });
        return result ?? new TroopEvasionResult(TroopEvasionOutcome.Failed, "Troop evasion did not return a result.");
    }

    public async Task<AcademyResearchResult> ResearchTroopsAtAcademyAsync(
        BotOptions options,
        IReadOnlyList<string> troopNames,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        AcademyResearchResult? result = null;
        await ExecuteWithClientAsync(options, log, accountName, interactive: false, cancellationToken, async client =>
        {
            await client.LoginAsync(cancellationToken);
            await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, skipFeatureRefresh: true);
            result = await client.ResearchTroopsAtAcademyAsync(troopNames, options.HeroResourceUseSmithy, cancellationToken);
        });
        return result ?? new AcademyResearchResult(AcademyResearchOutcome.Waiting, "Academy research did not return a result.", 300, troopNames);
    }

    public async Task<TrapBuildResult> BuildTrapsAsync(
        BotOptions options,
        int maxPerRun,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        TrapBuildResult? result = null;
        await ExecuteWithClientAsync(options, log, accountName, interactive: false, cancellationToken, async client =>
        {
            await client.LoginAsync(cancellationToken);
            await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, skipFeatureRefresh: true);
            result = await client.BuildTrapsAsync(maxPerRun, cancellationToken);
        });
        return result ?? new TrapBuildResult(TrapBuildOutcome.Failed, "Trap building did not return a result.", 0, null);
    }

    public async Task<TroopEvasionValidationResult> ValidateTroopEvasionAsync(
        BotOptions options,
        TroopEvasionRequest request,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        using var priorityRequest = _priorityBrowserWork.EnterPriorityRequest();
        TroopEvasionValidationResult? result = null;
        await ExecuteWithClientAsync(options, log, accountName, interactive: true, cancellationToken, async client =>
        {
            await client.EnsureAccountAccessAllowedAsync(cancellationToken);
            result = await new CombatOperation(client).ValidateTroopEvasionAsync(request, cancellationToken);
        });
        return result ?? new TroopEvasionValidationResult(false, "Troop evasion validation did not return a result.");
    }

    public async Task<IReadOnlyDictionary<string, long>> ReadAvailableTroopsForCatapultWavesAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        return await ReadAvailableTroopsForCatapultWavesAsync(
            options,
            log,
            forceRefresh: false,
            accountName,
            cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, long>> ReadAvailableTroopsForCatapultWavesAsync(
        BotOptions options,
        Action<string> log,
        bool forceRefresh,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, long> result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, skipFeatureRefresh: true);
                result = await new CombatOperation(client).ReadAvailableTroopsForCatapultWavesAsync(
                    forceRefresh,
                    cancellationToken);
            });

        return result;
    }

    public async Task<CatapultWaveSetupInfo> ReadCatapultWaveSetupInfoAsync(
        BotOptions options,
        Action<string> log,
        bool forceRefresh,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        CatapultWaveSetupInfo? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, skipFeatureRefresh: true);
                result = await new CombatOperation(client).ReadCatapultWaveSetupInfoAsync(
                    forceRefresh,
                    cancellationToken);
            });

        return result ?? new CatapultWaveSetupInfo(new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase), null);
    }

    public async Task<CatapultWaveRunResult> StartCatapultWavesAsync(
        BotOptions options,
        CatapultWaveRequest request,
        Action<string> log,
        Func<int, CancellationToken, Task<bool>> sendConfirmationRequested,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        CatapultWaveRunResult? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                await TrySwitchToTargetVillageAsync(client, options, log, cancellationToken, skipFeatureRefresh: true);
                result = await new CombatOperation(client).StartCatapultWavesAsync(
                    request,
                    sendConfirmationRequested,
                    cancellationToken);
            });

        return result ?? throw new InvalidOperationException("Could not start catapult waves.");
    }

}
