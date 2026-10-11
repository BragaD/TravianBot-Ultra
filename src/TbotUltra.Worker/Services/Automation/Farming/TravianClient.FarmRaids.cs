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
        FarmRaidGuard guard,
        bool holdOnSomeLosses,
        bool holdOnAllLost,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        // Loss reports first: a target that just cost troops must not be raided again this round.
        var (newLosses, lastReportId) = await ReadNewFarmRaidLossesAsync(guard.LastReportId, holdOnSomeLosses, holdOnAllLost, cancellationToken);
        var blocked = new HashSet<string>(guard.Blocked, StringComparer.Ordinal);
        foreach (var loss in newLosses)
        {
            blocked.Add(FarmRaidKey(loss.X, loss.Y));
            Notify($"[farm-raid] target ({loss.X}|{loss.Y}) put on hold: its last raid {(loss.AllLost ? "lost all troops" : "had losses")} (report {loss.ReportId}).");
        }

        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken, refreshCurrentPage: true);
        await DismissDeactivatedTargetsNoticeAsync(cancellationToken);

        var targets = await ReadFarmRaidTargetsAsync(selectedNames, selectedIds, cancellationToken);
        Notify($"[farm-raid] {targets.Targets.Count} target(s) ready across {targets.ListCount} list(s); {targets.RunningCount} already being raided.");

        var sent = 0;
        var failed = 0;
        var skippedNoTroops = 0;
        var skippedOnHold = 0;
        var stillRunning = targets.RunningCount;
        var sentRaids = new List<FarmRaidSent>();
        var raidedThisRound = new HashSet<string>(StringComparer.Ordinal);
        var villagesOutOfTroops = new HashSet<int>();
        var now = DateTimeOffset.UtcNow;
        foreach (var target in targets.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = FarmRaidKey(target.X, target.Y);
            if (blocked.Contains(key))
            {
                skippedOnHold++;
                continue;
            }

            // The same target can sit in several lists; one raid per round is enough.
            if (raidedThisRound.Contains(key)
                || (guard.BusyUntilUtc.TryGetValue(key, out var busyUntil) && busyUntil > now))
            {
                stillRunning++;
                continue;
            }

            if (villagesOutOfTroops.Contains(target.VillageId))
            {
                skippedNoTroops++;
                continue;
            }

            var (outcome, returnAtUtc) = await TrySendFarmRaidAsync(target, cancellationToken);
            switch (outcome)
            {
                case FarmRaidOutcome.Sent:
                    sent++;
                    raidedThisRound.Add(key);
                    sentRaids.Add(new FarmRaidSent(target.X, target.Y, returnAtUtc));
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

        Notify($"[farm-raid] done: sent={sent}, stillOut={stillRunning}, onHold={skippedOnHold}, waitingForTroops={skippedNoTroops}, failed={failed}.");
        return new FarmRaidBatchResult(
            targets.ListCount, sent, stillRunning, skippedNoTroops, failed, skippedOnHold, sentRaids, newLosses, lastReportId);
    }

    internal static string FarmRaidKey(int x, int y)
        => $"{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}";

    // Reads the first page of offensive reports and returns raids newer than the last one seen that
    // came back with losses. iReport2 = won with losses, iReport3 = all troops lost.
    private async Task<(IReadOnlyList<FarmRaidLoss> Losses, long LastReportId)> ReadNewFarmRaidLossesAsync(
        long lastReportId,
        bool holdOnSomeLosses,
        bool holdOnAllLost,
        CancellationToken cancellationToken)
    {
        try
        {
            await GotoAsync($"{Paths.Reports}/offensive", cancellationToken);
            var json = await _page.EvaluateAsync<string>(
                """
                () => JSON.stringify(Array.from(document.querySelectorAll('#overview tbody tr')).map(row => {
                  const id = Number(row.querySelector('input.check.report')?.value || 0);
                  const icon = (row.querySelector('img.iReport')?.className || '').match(/iReport(\d+)/);
                  const strip = (text) => (text || '').replace(/[^0-9-]/g, '');
                  const x = strip(row.querySelector('.coordinateX')?.textContent);
                  const y = strip(row.querySelector('.coordinateY')?.textContent);
                  return { id, level: icon ? Number(icon[1]) : 0, x: x === '' ? null : Number(x), y: y === '' ? null : Number(y) };
                }))
                """).WaitAsync(cancellationToken);

            var losses = new List<FarmRaidLoss>();
            var judged = new HashSet<string>(StringComparer.Ordinal);
            var newest = lastReportId;
            using var document = JsonDocument.Parse(json);
            foreach (var row in document.RootElement.EnumerateArray())
            {
                var id = row.GetProperty("id").GetInt64();
                if (id <= 0)
                {
                    continue;
                }

                newest = Math.Max(newest, id);
                var level = row.GetProperty("level").GetInt32();

                // The first read only sets the watermark, so old reports never put a target on hold.
                if (lastReportId <= 0 || id <= lastReportId
                    || row.GetProperty("x").ValueKind != JsonValueKind.Number
                    || row.GetProperty("y").ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                // Rows are newest first: only a target's most recent raid decides, so a clean raid
                // after an unlucky one keeps the target in rotation.
                var x = row.GetProperty("x").GetInt32();
                var y = row.GetProperty("y").GetInt32();
                if (!judged.Add(FarmRaidKey(x, y)))
                {
                    continue;
                }

                if ((level == 2 && holdOnSomeLosses) || (level == 3 && holdOnAllLost))
                {
                    losses.Add(new FarmRaidLoss(x, y, level == 3, id));
                }
            }

            return (losses, newest);
        }
        catch (Exception ex) when (ex is PlaywrightException or JsonException or TimeoutException)
        {
            Notify($"[farm-raid] could not read raid reports for losses: {ex.Message}");
            return ([], lastReportId);
        }
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

    private async Task<(FarmRaidOutcome Outcome, DateTimeOffset ReturnAtUtc)> TrySendFarmRaidAsync(FarmRaidTarget target, CancellationToken cancellationToken)
    {
        // Used when the confirmation page does not expose the travel time.
        var returnAtUtc = DateTimeOffset.UtcNow.AddMinutes(30);
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
                    return (FarmRaidOutcome.NotEnoughTroops, returnAtUtc);
                }
            }

            foreach (var troop in target.Troops)
            {
                if (!await TryFillTroopInputAsync($"t{troop.Key}", $"troop slot {troop.Key}", troop.Value, cancellationToken))
                {
                    Notify($"[farm-raid] could not fill troop slot t{troop.Key} for {label}.");
                    return (FarmRaidOutcome.Failed, returnAtUtc);
                }
            }

            var xText = target.X.ToString(CultureInfo.InvariantCulture);
            var yText = target.Y.ToString(CultureInfo.InvariantCulture);
            if (!await TryTypeHumanlyIntoFirstMatchingInputAsync(["input[name='x']"], xText, cancellationToken)
                || !await TryTypeHumanlyIntoFirstMatchingInputAsync(["input[name='y']"], yText, cancellationToken))
            {
                Notify($"[farm-raid] could not enter coordinates for {label}.");
                return (FarmRaidOutcome.Failed, returnAtUtc);
            }

            // eventType 4 is "Attack: Raid" on the Official send-troops form.
            var raidRadio = _page.Locator("input[type='radio'][name='eventType'][value='4']").First;
            if (await raidRadio.CountAsync() == 0)
            {
                Notify($"[farm-raid] raid option not found on the send-troops form for {label}.");
                return (FarmRaidOutcome.Failed, returnAtUtc);
            }

            await DelayBeforeClickAsync(cancellationToken, "farm raid: raid mode");
            await ClickLocatorAsync(raidRadio, "farm-raid-mode", cancellationToken);

            var firstSend = _page.Locator("button#ok[type='submit'][name='ok']").First;
            if (await firstSend.CountAsync() == 0)
            {
                Notify($"[farm-raid] Send button not found for {label}.");
                return (FarmRaidOutcome.Failed, returnAtUtc);
            }

            await DelayBeforeClickAsync(cancellationToken, "farm raid: Send");
            await ClickLocatorAsync(firstSend, "farm-raid-send", cancellationToken);
            if (!await WaitForSendTroopsConfirmationPageAsync(cancellationToken))
            {
                var error = await ReadReinforcementFormErrorAsync(cancellationToken);
                Notify($"[farm-raid] {label} was rejected: {error ?? "the confirmation page did not load"}.");
                return (FarmRaidOutcome.Failed, returnAtUtc);
            }

            if (await ReadTroopEvasionTravelTimeAsync(cancellationToken) is { } oneWay)
            {
                returnAtUtc = DateTimeOffset.UtcNow + oneWay + oneWay + TimeSpan.FromSeconds(30);
            }

            var finalConfirm = _page.Locator("button#confirmSendTroops.rallyPointConfirm[name='confirmSendTroops']").First;
            if (await finalConfirm.CountAsync() == 0)
            {
                Notify($"[farm-raid] Confirm button not found for {label}.");
                return (FarmRaidOutcome.Failed, returnAtUtc);
            }

            await DelayBeforeClickAsync(cancellationToken, "farm raid: Confirm");
            await ClickLocatorAsync(finalConfirm, "farm-raid-confirm", cancellationToken);
            await WaitForSendTroopsCompletionAsync(cancellationToken);
            if (await _page.Locator("button#confirmSendTroops.rallyPointConfirm[name='confirmSendTroops']").CountAsync() > 0)
            {
                Notify($"[farm-raid] {label}: final confirmation could not be verified.");
                return (FarmRaidOutcome.Failed, returnAtUtc);
            }

            var troopsText = string.Join(", ", target.Troops.Select(troop => $"t{troop.Key}×{troop.Value}"));
            Notify($"[farm-raid] raid sent to {label} with {troopsText}; troops back around {returnAtUtc.ToLocalTime():HH:mm}.");
            return (FarmRaidOutcome.Sent, returnAtUtc);
        }
        catch (PlaywrightException ex)
        {
            Notify($"[farm-raid] {label} failed: {ex.Message}");
            return (FarmRaidOutcome.Failed, returnAtUtc);
        }
    }
}
