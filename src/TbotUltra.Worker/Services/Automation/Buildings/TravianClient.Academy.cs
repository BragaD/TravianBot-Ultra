using System.Text.Json;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Academy research workflow. Researchable troops are listed like Smithy rows
// (.build_details.researches .research); troops with unmet building requirements are listed in
// #researchFuture. A troop in neither list (and not in the queue) is already researched.
public sealed partial class TravianClient
{
    private const int AcademyGid = 22;

    /// <summary>
    /// Researches the first wanted troop that is not researched yet. A troop that is no longer listed on
    /// the Academy page (and is not in its queue) counts as researched. Starts at most one research per
    /// call; the caller comes back after <see cref="AcademyResearchResult.RetrySeconds"/>.
    /// </summary>
    public async Task<AcademyResearchResult> ResearchTroopsAtAcademyAsync(
        IReadOnlyList<string> troopNames,
        bool useHeroResources,
        CancellationToken cancellationToken = default)
    {
        var wanted = (troopNames ?? [])
            .Select(name => name?.Trim() ?? string.Empty)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Notify($"[academy] research requested for: {string.Join(", ", wanted)}");
        if (wanted.Count == 0)
        {
            return new AcademyResearchResult(AcademyResearchOutcome.AllResearched, "Academy: no troops selected.", null, []);
        }

        var heroTransferAttempted = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await GotoAsync(Paths.BuildByGid(AcademyGid), cancellationToken);
            await WaitForPageReadyAsync(cancellationToken);
            await EnsureLoggedInAsync(cancellationToken: cancellationToken);
            if (!await IsCurrentBuildingPageAsync(AcademyGid, cancellationToken))
            {
                return new AcademyResearchResult(
                    AcademyResearchOutcome.NoAcademy, "Academy not found in this village. Build an Academy first.", null, wanted);
            }

            var page = await ReadAcademyPageAsync(cancellationToken);
            Notify($"[academy] rows: {string.Join("; ", page.Rows.Select(row => $"{row.Name} ready={row.CanResearch} locked={row.Locked}"))}"
                + $" | queue: {string.Join("; ", page.Queue.Select(entry => $"{entry.Name} {entry.RemainingSeconds}s"))}");

            var remaining = new List<string>();
            AcademyRow? ready = null;
            AcademyRow? shortOfResources = null;
            var notes = new List<string>();
            foreach (var name in wanted)
            {
                var row = page.Rows.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                var queued = page.Queue.Any(entry => entry.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                if (row is null && !queued)
                {
                    continue; // not listed any more: researched
                }

                remaining.Add(name);
                if (queued || row is null)
                {
                    continue;
                }

                if (row.CanResearch)
                {
                    ready ??= row;
                }
                else if (row.Locked)
                {
                    notes.Add($"{name} needs {(string.IsNullOrWhiteSpace(row.Detail) ? "other buildings first" : row.Detail)}");
                }
                else
                {
                    shortOfResources ??= row;
                }
            }

            if (remaining.Count == 0)
            {
                return new AcademyResearchResult(AcademyResearchOutcome.AllResearched, "Academy: every selected troop is researched.", null, []);
            }

            var queueWait = page.Queue.Count > 0 ? page.Queue.Min(entry => entry.RemainingSeconds) : (int?)null;
            if (queueWait is int busySeconds)
            {
                return new AcademyResearchResult(
                    AcademyResearchOutcome.Waiting,
                    $"Academy: researching {page.Queue[0].Name}, {TravianParsing.FormatDuration(busySeconds)} left.",
                    busySeconds,
                    remaining);
            }

            if (ready is not null)
            {
                if (!await ClickAcademyResearchButtonAsync(ready.UnitId, ready.Name, cancellationToken))
                {
                    Notify($"[academy] research button for '{ready.Name}' could not be clicked; reloading.");
                    continue;
                }

                await WaitForPageReadyAsync(cancellationToken);
                await Task.Delay(600, cancellationToken);
                var after = await ReadAcademyPageAsync(cancellationToken);
                var started = after.Queue.Count > 0
                    || !after.Rows.Any(item => string.Equals(item.Name, ready.Name, StringComparison.OrdinalIgnoreCase));
                if (!started)
                {
                    Notify($"[academy] no research appeared in the queue after clicking '{ready.Name}'; reloading.");
                    continue;
                }

                var seconds = after.Queue.Count > 0 ? after.Queue.Min(entry => entry.RemainingSeconds) : (int?)null;
                Notify($"[academy] started research of '{ready.Name}'.");
                return new AcademyResearchResult(
                    AcademyResearchOutcome.Started, $"Academy: started researching {ready.Name}.", seconds, remaining, ready.Name);
            }

            if (shortOfResources is not null)
            {
                // A research that costs more than storage holds can never be paid for by waiting.
                if (shortOfResources.Cost is { Length: 4 } cost)
                {
                    var capacity = await ReadVillageStorageCapacitiesFromCurrentPageAsync(cancellationToken);
                    var warehouseNeed = Math.Max(cost[0], Math.Max(cost[1], cost[2]));
                    if (capacity.WarehouseCapacity is long warehouse && warehouseNeed > warehouse)
                    {
                        return new AcademyResearchResult(
                            AcademyResearchOutcome.Blocked,
                            $"Academy: {shortOfResources.Name} costs {warehouseNeed:N0} of one resource but the Warehouse holds {warehouse:N0}. Upgrade the Warehouse first.",
                            null,
                            remaining);
                    }

                    if (capacity.GranaryCapacity is long granary && cost[3] > granary)
                    {
                        return new AcademyResearchResult(
                            AcademyResearchOutcome.Blocked,
                            $"Academy: {shortOfResources.Name} costs {cost[3]:N0} crop but the Granary holds {granary:N0}. Upgrade the Granary first.",
                            null,
                            remaining);
                    }
                }

                if (useHeroResources && !heroTransferAttempted && shortOfResources.UnitId is int unitId)
                {
                    heroTransferAttempted = true;
                    if (await TryHeroResourceTransferForSmithyTroopAsync($"u{unitId}", shortOfResources.Name, cancellationToken))
                    {
                        continue; // read the page again with the hero's resources added
                    }
                }

                return new AcademyResearchResult(
                    AcademyResearchOutcome.Waiting,
                    $"Academy: not enough resources to research {shortOfResources.Name}.",
                    shortOfResources.WaitSeconds,
                    remaining);
            }

            return new AcademyResearchResult(
                AcademyResearchOutcome.Blocked, $"Academy: {string.Join("; ", notes)}.", null, remaining);
        }

        return new AcademyResearchResult(
            AcademyResearchOutcome.Waiting, "Academy: the research could not be started; trying again later.", 300, wanted);
    }

    /// <summary>True when the open page is the building with this gid (not its construction page).</summary>
    private async Task<bool> IsCurrentBuildingPageAsync(int gid, CancellationToken cancellationToken)
    {
        return await RetryAsync(
            $"Detect building page gid {gid}",
            () => _page.EvaluateAsync<bool>(
                "(gid) => { const build = document.querySelector('#build'); return !!build && build.classList.contains('gid' + gid); }",
                gid),
            cancellationToken: cancellationToken);
    }

    private sealed record AcademyRow(string Name, int? UnitId, bool CanResearch, bool Locked, string Detail, int? WaitSeconds, long[]? Cost = null);

    private sealed record AcademyQueueEntry(string Name, int RemainingSeconds);

    private sealed record AcademyPage(List<AcademyRow> Rows, List<AcademyQueueEntry> Queue);

    private async Task<AcademyPage> ReadAcademyPageAsync(CancellationToken cancellationToken)
    {
        var json = await RetryAsync(
            "Academy: read research rows",
            () => _page.EvaluateAsync<string>(
                """
                () => {
                  const clean = (v) => String(v || '').replace(/\s+/g, ' ').trim();
                  const seconds = (timer) => {
                    if (!timer) return null;
                    const value = String(timer.getAttribute('value') || timer.getAttribute('data-value') || '');
                    if (/^\d+$/.test(value)) return parseInt(value, 10);
                    const parts = clean(timer.textContent).split(':').map(Number);
                    if (parts.length === 3 && parts.every(Number.isFinite)) return parts[0] * 3600 + parts[1] * 60 + parts[2];
                    return null;
                  };
                  const rows = [];
                  // Troops that can be researched now sit in .build_details; the ones whose buildings are
                  // still missing sit in the collapsed #researchFuture list with their unmet requirements.
                  for (const row of document.querySelectorAll('.build_details.researches .research, #researchFuture .research')) {
                    const future = !!row.closest('#researchFuture');
                    const img = row.querySelector('img.unit');
                    const unit = /\bu(\d+)\b/.exec(img ? String(img.className || '') : '');
                    let name = '';
                    for (const a of row.querySelectorAll('.title a')) {
                      const text = clean(a.textContent);
                      if (text) { name = text; break; }
                    }
                    if (!name && img) name = clean(img.getAttribute('alt'));

                    let canResearch = false;
                    for (const b of row.querySelectorAll('button, input[type="submit"], input[type="button"]')) {
                      const text = clean(String(b.getAttribute('value') || '') + ' ' + String(b.textContent || '')).toLowerCase();
                      const cls = String(b.className || '').toLowerCase();
                      if (cls.includes('gold') || /exchange|npc|instant|video|faster|\d+\s*%/.test(text)) continue;
                      if (!/^research\b/.test(text)) continue;
                      canResearch = !(b.disabled === true || cls.includes('disabled') || b.getAttribute('aria-disabled') === 'true');
                      if (canResearch) break;
                    }

                    // A troop whose buildings are missing lists them instead of offering a button.
                    const unmet = Array.from(row.querySelectorAll('.requirements .error')).map(node => clean(node.textContent)).filter(Boolean);
                    const error = row.querySelector('.errorMessage');
                    const errorText = clean(error ? error.textContent : '');
                    const requirementText = unmet.join(', ');
                    if (future) canResearch = false;
                    const locked = future || (!canResearch && unmet.length > 0);
                    rows.push({
                      name,
                      unitId: unit ? parseInt(unit[1], 10) : null,
                      canResearch,
                      locked,
                      detail: requirementText || errorText,
                      cost: Array.from(row.querySelectorAll('.resourceWrapper .inlineIcon.resource .value')).slice(0, 4)
                        .map(node => parseInt(String(node.textContent || '').replace(/[^\d]/g, '') || '0', 10)),
                      waitSeconds: seconds(row.querySelector('.errorMessage .timer, .errorMessage [counting="down"]'))
                    });
                  }

                  const queue = [];
                  for (const timer of document.querySelectorAll('table.under_progress .timer, .under_progress .timer')) {
                    const row = timer.closest('tr, li') || timer.parentElement;
                    const img = row ? row.querySelector('img.unit') : null;
                    const remaining = seconds(timer);
                    if (remaining === null) continue;
                    queue.push({
                      name: clean((row && row.querySelector('.desc, .name, .title') || {}).textContent) || clean(img && img.getAttribute('alt')) || clean(row && row.textContent),
                      remainingSeconds: remaining
                    });
                  }
                  return JSON.stringify({ rows, queue });
                }
                """),
            cancellationToken: cancellationToken);

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<AcademyPage>(json, options) ?? new AcademyPage([], []);
    }

    // Clicks Research in the row of this unit (by icon id, or by name when the icon is missing).
    private async Task<bool> ClickAcademyResearchButtonAsync(int? unitId, string name, CancellationToken cancellationToken)
    {
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                ([unitId, name]) => {
                  const clean = (v) => String(v || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  for (const row of document.querySelectorAll('.build_details.researches .research')) {
                    const img = row.querySelector('img.unit');
                    const unit = /\bu(\d+)\b/.exec(img ? String(img.className || '') : '');
                    const rowName = clean((row.querySelector('.title a') || {}).textContent) || clean(img && img.getAttribute('alt'));
                    const match = unitId !== null && unit ? parseInt(unit[1], 10) === unitId : rowName === clean(name);
                    if (!match) continue;
                    for (const b of row.querySelectorAll('button, input[type="submit"], input[type="button"]')) {
                      const text = clean(String(b.getAttribute('value') || '') + ' ' + String(b.textContent || ''));
                      const cls = String(b.className || '').toLowerCase();
                      if (cls.includes('gold') || /exchange|npc|instant|video|faster|\d+\s*%/.test(text)) continue;
                      if (!/^research\b/.test(text)) continue;
                      if (b.disabled === true || cls.includes('disabled') || b.getAttribute('aria-disabled') === 'true') continue;
                      b.click();
                      return true;
                    }
                  }
                  return false;
                }
                """,
                new object?[] { unitId, name });
        }
        catch (Microsoft.Playwright.PlaywrightException ex) when (!BrowserFailureClassifier.IsTargetCrash(ex))
        {
            Notify($"[academy] click failed: {ex.Message}");
            return false;
        }
    }
}
