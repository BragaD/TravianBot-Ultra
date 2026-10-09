using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Farm-list dispatch for accounts without Gold Club. Travian only lets Gold Club accounts press a
// list's Start button, but every account may send the same raids one by one from the Rally Point.
// This reads the lists' targets and troop amounts from the Farm List page and sends each active,
// idle target as an ordinary raid.
public sealed partial class TravianClient
{
    private sealed record FarmRaidTarget(string ListName, int VillageId, string VillageName, int X, int Y, IReadOnlyDictionary<int, long> Troops);

    public async Task<FarmRaidBatchResult> SendFarmListTargetsAsRaidsAsync(
        IReadOnlyCollection<string> selectedNames,
        IReadOnlyCollection<string> selectedIds,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken, refreshCurrentPage: true);
        await DismissDeactivatedTargetsNoticeAsync(cancellationToken);

        var targets = await ReadFarmRaidTargetsAsync(selectedNames, selectedIds, cancellationToken);
        Notify($"[farm-raid] {targets.Targets.Count} target(s) ready across {targets.ListCount} list(s); {targets.RunningCount} already being raided.");

        var sent = 0;
        var failed = 0;
        var skippedNoTroops = 0;
        var villagesOutOfTroops = new HashSet<int>();
        foreach (var target in targets.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (villagesOutOfTroops.Contains(target.VillageId))
            {
                skippedNoTroops++;
                continue;
            }

            var outcome = await TrySendFarmRaidAsync(target, cancellationToken);
            switch (outcome)
            {
                case FarmRaidOutcome.Sent:
                    sent++;
                    break;
                case FarmRaidOutcome.NotEnoughTroops:
                    // Targets are ordered by the list; once the village runs dry the rest wait for the next round.
                    villagesOutOfTroops.Add(target.VillageId);
                    skippedNoTroops++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        Notify($"[farm-raid] done: sent={sent}, waitingForTroops={skippedNoTroops}, failed={failed}, alreadyRunning={targets.RunningCount}.");
        return new FarmRaidBatchResult(targets.ListCount, sent, targets.RunningCount, skippedNoTroops, failed);
    }

    private enum FarmRaidOutcome
    {
        Sent,
        NotEnoughTroops,
        Failed,
    }

    private async Task<(IReadOnlyList<FarmRaidTarget> Targets, int ListCount, int RunningCount)> ReadFarmRaidTargetsAsync(
        IReadOnlyCollection<string> selectedNames,
        IReadOnlyCollection<string> selectedIds,
        CancellationToken cancellationToken)
    {
        // The Farm List page embeds its full state as the viewData argument of React.FarmList.render.
        // Cut that object out with a string-aware brace scan instead of a regex over nested JSON.
        var viewDataJson = await _page.EvaluateAsync<string?>(
            """
            () => {
              const script = Array.from(document.scripts).map(node => node.textContent || '')
                .find(text => text.includes('React.FarmList.render'));
              if (!script) return null;
              const start = script.indexOf('{', script.indexOf('viewData:'));
              if (start < 0) return null;
              let depth = 0, inString = false, escaped = false;
              for (let i = start; i < script.length; i++) {
                const ch = script[i];
                if (inString) {
                  if (escaped) escaped = false;
                  else if (ch === '\\') escaped = true;
                  else if (ch === '"') inString = false;
                  continue;
                }
                if (ch === '"') inString = true;
                else if (ch === '{') depth++;
                else if (ch === '}' && --depth === 0) return script.slice(start, i + 1);
              }
              return null;
            }
            """).WaitAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(viewDataJson))
        {
            throw new InvalidOperationException("Could not read farm list data from the Farm List page.");
        }

        using var document = JsonDocument.Parse(viewDataJson);
        var player = document.RootElement.GetProperty("ownPlayer");
        var villageNames = player.GetProperty("villages").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetInt32(), item => item.GetProperty("name").GetString() ?? string.Empty);
        var names = selectedNames.Select(name => name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = selectedIds.Select(id => id.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectAll = names.Count == 0 && ids.Count == 0;

        var targets = new List<FarmRaidTarget>();
        var listCount = 0;
        var running = 0;
        foreach (var list in player.GetProperty("farmLists").EnumerateArray())
        {
            var listName = list.GetProperty("name").GetString() ?? string.Empty;
            var listId = list.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);
            if (!selectAll && !names.Contains(listName) && !ids.Contains(listId))
            {
                continue;
            }

            listCount++;
            var villageId = list.GetProperty("ownerVillage").GetProperty("id").GetInt32();
            if (!list.TryGetProperty("slots", out var slots) || slots.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var slot in slots.EnumerateArray())
            {
                if (!slot.GetProperty("isActive").GetBoolean())
                {
                    continue;
                }

                if (slot.GetProperty("isRunning").GetBoolean() || slot.GetProperty("runningAttacks").GetInt32() > 0)
                {
                    running++;
                    continue;
                }

                var troops = new Dictionary<int, long>();
                for (var index = 1; index <= 10; index++)
                {
                    if (slot.GetProperty("troop").TryGetProperty($"t{index}", out var amount) && amount.GetInt64() > 0)
                    {
                        troops[index] = amount.GetInt64();
                    }
                }

                if (troops.Count == 0)
                {
                    continue;
                }

                var targetElement = slot.GetProperty("target");
                targets.Add(new FarmRaidTarget(
                    listName,
                    villageId,
                    villageNames.GetValueOrDefault(villageId, string.Empty),
                    targetElement.GetProperty("x").GetInt32(),
                    targetElement.GetProperty("y").GetInt32(),
                    troops));
            }
        }

        return (targets, listCount, running);
    }

    private async Task<FarmRaidOutcome> TrySendFarmRaidAsync(FarmRaidTarget target, CancellationToken cancellationToken)
    {
        var label = $"({target.X}|{target.Y}) from list '{target.ListName}'";
        try
        {
            if (!string.IsNullOrWhiteSpace(target.VillageName))
            {
                await SwitchToVillageByIdentityAsync(
                    target.VillageName,
                    $"dorf1.php?newdid={target.VillageId.ToString(CultureInfo.InvariantCulture)}",
                    targetVillageKey: null,
                    cancellationToken,
                    skipFeatureRefresh: true);
            }

            await EnsureRallyPointAndOpenSendTroopsPageAsync(cancellationToken, allowReuseCurrentPage: false);
            foreach (var troop in target.Troops)
            {
                var available = await ReadAvailableTroopCountAsync($"t{troop.Key}", cancellationToken) ?? 0;
                if (available < troop.Value)
                {
                    Notify($"[farm-raid] not enough troops for {label}: slot t{troop.Key} needs {troop.Value}, {available} at home.");
                    return FarmRaidOutcome.NotEnoughTroops;
                }
            }

            foreach (var troop in target.Troops)
            {
                if (!await TryFillTroopInputAsync($"t{troop.Key}", $"troop slot {troop.Key}", troop.Value, cancellationToken))
                {
                    Notify($"[farm-raid] could not fill troop slot t{troop.Key} for {label}.");
                    return FarmRaidOutcome.Failed;
                }
            }

            var xText = target.X.ToString(CultureInfo.InvariantCulture);
            var yText = target.Y.ToString(CultureInfo.InvariantCulture);
            if (!await TryTypeHumanlyIntoFirstMatchingInputAsync(["input[name='x']"], xText, cancellationToken)
                || !await TryTypeHumanlyIntoFirstMatchingInputAsync(["input[name='y']"], yText, cancellationToken))
            {
                Notify($"[farm-raid] could not enter coordinates for {label}.");
                return FarmRaidOutcome.Failed;
            }

            // eventType 4 is "Attack: Raid" on the Official send-troops form.
            var raidRadio = _page.Locator("input[type='radio'][name='eventType'][value='4']").First;
            if (await raidRadio.CountAsync() == 0)
            {
                Notify($"[farm-raid] raid option not found on the send-troops form for {label}.");
                return FarmRaidOutcome.Failed;
            }

            await DelayBeforeClickAsync(cancellationToken, "farm raid: raid mode");
            await ClickLocatorAsync(raidRadio, "farm-raid-mode", cancellationToken);

            var firstSend = _page.Locator("button#ok[type='submit'][name='ok']").First;
            if (await firstSend.CountAsync() == 0)
            {
                Notify($"[farm-raid] Send button not found for {label}.");
                return FarmRaidOutcome.Failed;
            }

            await DelayBeforeClickAsync(cancellationToken, "farm raid: Send");
            await ClickLocatorAsync(firstSend, "farm-raid-send", cancellationToken);
            if (!await WaitForSendTroopsConfirmationPageAsync(cancellationToken))
            {
                var error = await ReadReinforcementFormErrorAsync(cancellationToken);
                Notify($"[farm-raid] {label} was rejected: {error ?? "the confirmation page did not load"}.");
                return FarmRaidOutcome.Failed;
            }

            var finalConfirm = _page.Locator("button#confirmSendTroops.rallyPointConfirm[name='confirmSendTroops']").First;
            if (await finalConfirm.CountAsync() == 0)
            {
                Notify($"[farm-raid] Confirm button not found for {label}.");
                return FarmRaidOutcome.Failed;
            }

            await DelayBeforeClickAsync(cancellationToken, "farm raid: Confirm");
            await ClickLocatorAsync(finalConfirm, "farm-raid-confirm", cancellationToken);
            await WaitForSendTroopsCompletionAsync(cancellationToken);
            if (await _page.Locator("button#confirmSendTroops.rallyPointConfirm[name='confirmSendTroops']").CountAsync() > 0)
            {
                Notify($"[farm-raid] {label}: final confirmation could not be verified.");
                return FarmRaidOutcome.Failed;
            }

            var troopsText = string.Join(", ", target.Troops.Select(troop => $"t{troop.Key}×{troop.Value}"));
            Notify($"[farm-raid] raid sent to {label} with {troopsText}.");
            return FarmRaidOutcome.Sent;
        }
        catch (PlaywrightException ex)
        {
            Notify($"[farm-raid] {label} failed: {ex.Message}");
            return FarmRaidOutcome.Failed;
        }
    }
}
