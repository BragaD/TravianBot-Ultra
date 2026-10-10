using Microsoft.Playwright;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services.Automation.Combat;
using System.Globalization;
using System.Text.Json.Serialization;

namespace TbotUltra.Worker.Services;

// Combat surface of the TravianClient facade. The interface list is declared on
// this partial to co-locate the contract with the domain it covers.
public sealed partial class TravianClient : ICombatClient
{
    private const int CatapultPreparationDelayMinMilliseconds = 250;
    private const int CatapultPreparationDelayMaxMilliseconds = 500;

    private static readonly string[] CatapultSendButtonSelectors =
    [
        "button#ok[name='ok'][value='ok'][type='submit']",
        "button#ok",
        "button[name='ok'][value='ok'][type='submit']",
        "input[type='submit'][name='ok'][value='ok']",
    ];

    private static readonly string[] CatapultConfirmButtonSelectors =
    [
        "button#confirmSendTroops.rallyPointConfirm",
        "button#confirmSendTroops",
        "button[name='confirmSendTroops']",
        ".button-container:has(.button-content:text-is('Confirm'))",
        "button:has(.button-content:text-is('Confirm'))",
        "button:has-text('Confirm')",
        "input[type='submit'][value*='Confirm' i]",
        "input[type='button'][value*='Confirm' i]",
        "a:has-text('Confirm')",
        ".button-content:text-is('Confirm')",
    ];

    public async Task<IReadOnlyDictionary<string, long>> ReadAvailableTroopsForCatapultWavesAsync(CancellationToken cancellationToken = default)
    {
        var setupInfo = await ReadCatapultWaveSetupInfoAsync(forceRefresh: false, cancellationToken);
        return setupInfo.AvailableTroops;
    }

    public async Task<IReadOnlyDictionary<string, long>> ReadAvailableTroopsForCatapultWavesAsync(bool forceRefresh, CancellationToken cancellationToken = default)
    {
        var setupInfo = await ReadCatapultWaveSetupInfoAsync(forceRefresh, cancellationToken);
        return setupInfo.AvailableTroops;
    }

    public async Task<CatapultWaveSetupInfo> ReadCatapultWaveSetupInfoAsync(bool forceRefresh, CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        if (forceRefresh)
        {
            await RefreshCatapultSendTroopsPageAsync(cancellationToken);
        }
        else
        {
            await EnsureRallyPointAndOpenSendTroopsPageAsync(cancellationToken, allowReuseCurrentPage: true);
        }

        var tribe = await ReadActiveVillageTribeAsync(cancellationToken);
        if (!TroopCatalog.IsKnownTribe(tribe))
        {
            Notify("[tribe] catapult setup deferred because the active village tribe is unknown.");
            return new CatapultWaveSetupInfo(new Dictionary<string, long>(), await TryReadRallyPointLevelAsync(cancellationToken));
        }

        var availableTroops = await ReadAvailableTroopsOnCurrentSendTroopsPageAsync(tribe, cancellationToken);
        var rallyPointLevel = await TryReadRallyPointLevelAsync(cancellationToken);
        return new CatapultWaveSetupInfo(availableTroops, rallyPointLevel);
    }

    public async Task<CatapultWaveRunResult> StartCatapultWavesAsync(
        CatapultWaveRequest request,
        Func<int, CancellationToken, Task<bool>> sendConfirmationRequested,
        CancellationToken cancellationToken = default)
    {
        var plan = CatapultWavePlanner.BuildPlan(request);
        Notify($"[catapult] starting — target ({request.X}|{request.Y}), {plan.Attacks.Count} attack(s): 1 first + {request.WaveCount} wave(s)");
        Notify($"[catapult] preparation pacing {CatapultPreparationDelayMinMilliseconds}–{CatapultPreparationDelayMaxMilliseconds} ms per attack; final dispatch timing unchanged.");

        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        await EnsureRallyPointAndOpenSendTroopsPageAsync(cancellationToken, allowReuseCurrentPage: true);
        var tribe = await ReadActiveVillageTribeAsync(cancellationToken);
        if (!TroopCatalog.IsKnownTribe(tribe))
        {
            throw new InvalidOperationException("Catapult waves require a known tribe for the active village. Scan the village and try again.");
        }

        var availableTroops = await ReadAvailableTroopsOnCurrentSendTroopsPageAsync(tribe, cancellationToken);
        CatapultWavePlanner.ValidateAvailability(plan, request.WaveCount, availableTroops);

        var prepared = new List<PreparedCatapultAttack>();
        try
        {
            await PrepareCatapultWaveTabsAsync(prepared, plan, request, cancellationToken);
            prepared.Insert(0, await PrepareCatapultFirstAttackAsync(plan.Attacks[0], request, cancellationToken));

            for (var index = 0; index < prepared.Count; index++)
            {
                prepared[index] = await PrepareCatapultConfirmationAsync(prepared[index], request, cancellationToken);
            }

            VerifyCatapultArrivalOrder(prepared);
            await prepared[0].Page.BringToFrontAsync();
            Notify($"[catapult] all {prepared.Count} attack(s) prepared; waiting for Send now or Cancel.");
            if (!await sendConfirmationRequested(prepared.Count, cancellationToken))
            {
                Notify("[catapult] canceled after preparation; no attacks were sent.");
                throw new OperationCanceledException("Catapult waves canceled before sending.", cancellationToken);
            }

            // The user may have reviewed another attack tab while the confirmation was open. Always
            // restore the first attack before the existing tight dispatch loop begins.
            await prepared[0].Page.BringToFrontAsync();
            Notify("[catapult] Send now confirmed; first attack tab activated before dispatch.");
            var sent = 0;
            var failed = 0;
            var dispatched = new List<PreparedCatapultAttack>();
            foreach (var attack in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await attack.Page.BringToFrontAsync();
                var clicked = await TryClickCatapultConfirmButtonAsync(
                    attack.Page,
                    request.TabOpenDelayMilliseconds,
                    cancellationToken);
                if (clicked)
                {
                    dispatched.Add(attack);
                }
                else
                {
                    failed++;
                    Notify($"[catapult] FAILED to confirm {attack.Label.ToLowerInvariant()} to ({request.X}|{request.Y}) (confirm button not clickable)");
                }
            }

            foreach (var attack in dispatched)
            {
                if (await WaitForCatapultSendResultAsync(attack, cancellationToken))
                {
                    sent++;
                    Notify($"[catapult] sent {attack.Label.ToLowerInvariant()} to ({request.X}|{request.Y})");
                }
                else
                {
                    failed++;
                    Notify($"[catapult] FAILED to confirm {attack.Label.ToLowerInvariant()} to ({request.X}|{request.Y})");
                }
            }

            await Task.Delay(request.TabOpenDelayMilliseconds, cancellationToken);
            await EnsureLoggedInAsync(cancellationToken: cancellationToken);

            // Only mention failures when there are any: the word "failed" trips the alarm panel,
            // so a clean run must not contain it, while a partial failure legitimately should.
            Notify(failed > 0
                ? $"[catapult] done — sent {sent}/{prepared.Count} prepared, {failed} failed, target ({request.X}|{request.Y})"
                : $"[catapult] done — sent {sent}/{prepared.Count} prepared, target ({request.X}|{request.Y})");
            return new CatapultWaveRunResult(
                plan.Attacks.Count,
                prepared.Count,
                sent,
                failed,
                request.X,
                request.Y);
        }
        finally
        {
            foreach (var attack in prepared.Skip(1))
            {
                try
                {
                    if (!attack.Page.IsClosed)
                    {
                        await attack.Page.CloseAsync();
                    }
                }
                catch
                {
                    // Best effort cleanup for temporary tabs.
                }
            }
        }
    }

    private async Task RefreshCatapultSendTroopsPageAsync(CancellationToken cancellationToken)
    {
        await EnsureRallyPointAndOpenSendTroopsPageAsync(cancellationToken, allowReuseCurrentPage: true);
        await ReloadPageTracedAsync(
            _page,
            "refresh catapult send-troops page",
            new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded },
            cancellationToken);
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        if (!await SendTroopsNavigator.IsSendTroopsPageAsync(_page, cancellationToken))
        {
            await EnsureRallyPointAndOpenSendTroopsPageAsync(cancellationToken, allowReuseCurrentPage: false);
        }
    }

    private async Task PrepareCatapultWaveTabsAsync(
        ICollection<PreparedCatapultAttack> prepared,
        CatapultWavePlan plan,
        CatapultWaveRequest request,
        CancellationToken cancellationToken)
    {
        if (request.WaveCount == 0)
        {
            return;
        }

        var wave = plan.Attacks[1];
        Notify($"[catapult:verbose] filling wave troops before opening {request.WaveCount} tab(s)");
        await FillCatapultAttackFormAsync(_page, wave, request, cancellationToken);

        foreach (var attack in plan.Attacks.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await OpenCatapultWaveTabAsync(_page, cancellationToken);
            page.SetDefaultTimeout(_config.TimeoutMs);
            _browserTrace.AttachPage(page, "catapult-wave-tab");
            prepared.Add(new PreparedCatapultAttack(page, attack.Label, attack.Troops, null));
            Notify($"[catapult:verbose] opened tab for {attack.Label.ToLowerInvariant()} to ({request.X}|{request.Y})");
        }
    }

    private async Task<PreparedCatapultAttack> PrepareCatapultFirstAttackAsync(
        CatapultWaveAttackPlan attack,
        CatapultWaveRequest request,
        CancellationToken cancellationToken)
    {
        if (!await SendTroopsNavigator.IsSendTroopsPageAsync(_page, cancellationToken))
        {
            throw new InvalidOperationException("Could not open Send Troops page for catapult waves.");
        }

        Notify($"[catapult:verbose] changing original tab to {attack.Label.ToLowerInvariant()} to ({request.X}|{request.Y})");
        await FillCatapultAttackFormAsync(_page, attack, request, cancellationToken);
        await DelayBeforeCatapultPreparationActionAsync(cancellationToken);

        if (!await TryClickCatapultSendButtonAsync(_page, 0, cancellationToken))
        {
            throw new InvalidOperationException($"Could not open confirmation page for {attack.Label.ToLowerInvariant()}.");
        }

        var attackError = await TryReadAttackErrorAsync(_page, cancellationToken);
        if (!string.IsNullOrWhiteSpace(attackError))
        {
            throw new InvalidOperationException(FormatCatapultAttackError(attack.Label, attackError));
        }

        if (!await WaitForSendTroopsConfirmationPageAsync(_page, cancellationToken))
        {
            attackError = await TryReadAttackErrorAsync(_page, cancellationToken);
            if (!string.IsNullOrWhiteSpace(attackError))
            {
                throw new InvalidOperationException(FormatCatapultAttackError(attack.Label, attackError));
            }

            throw new InvalidOperationException($"Confirmation page did not load for {attack.Label.ToLowerInvariant()}.");
        }

        return new PreparedCatapultAttack(_page, attack.Label, attack.Troops, null);
    }

    private async Task FillCatapultAttackFormAsync(
        IPage page,
        CatapultWaveAttackPlan attack,
        CatapultWaveRequest request,
        CancellationToken cancellationToken)
    {
        await ClearTroopInputsAsync(page, cancellationToken);
        if (!await SetCatapultInputValueAsync(page, ["input[name='x']", "input[name='xCoord']", "input[id*='xCoord' i]"], request.X.ToString(CultureInfo.InvariantCulture), null, cancellationToken) ||
            !await SetCatapultInputValueAsync(page, ["input[name='y']", "input[name='yCoord']", "input[id*='yCoord' i]"], request.Y.ToString(CultureInfo.InvariantCulture), null, cancellationToken))
        {
            throw new InvalidOperationException("Could not fill catapult target coordinates.");
        }

        foreach (var troop in attack.Troops)
        {
            var troopIndex = TroopCatalog.ResolveTroopIndex(troop.Key);
            if (troopIndex is null)
            {
                throw new InvalidOperationException($"Could not resolve troop slot for '{troop.Key}'.");
            }

            var filled = await TryFillTroopInputAsync(page, $"t{troopIndex.Value}", troop.Key, troop.Value, cancellationToken);
            if (!filled)
            {
                throw new InvalidOperationException($"Could not fill troop field '{troop.Key}' for {attack.Label.ToLowerInvariant()}.");
            }

            if (string.Equals(attack.Label, CatapultWavePlanner.FirstAttackLabel, StringComparison.OrdinalIgnoreCase))
            {
                Notify($"[catapult:verbose] filled first-attack troop {troop.Key}={troop.Value}");
            }
        }

        if (!await TrySelectAttackModeAsync(page, request.RaidAttack, cancellationToken))
        {
            var attackMode = request.RaidAttack ? "raid" : "normal attack";
            throw new InvalidOperationException($"Could not select {attackMode} for {attack.Label.ToLowerInvariant()}.");
        }
    }

    private async Task<PreparedCatapultAttack> PrepareCatapultConfirmationAsync(
        PreparedCatapultAttack attack,
        CatapultWaveRequest request,
        CancellationToken cancellationToken)
    {
        await attack.Page.BringToFrontAsync();
        await EnsureCatapultConfirmReadyAsync(attack, request.TabOpenDelayMilliseconds, cancellationToken);

        var attackError = await TryReadAttackErrorAsync(attack.Page, cancellationToken);
        if (!string.IsNullOrWhiteSpace(attackError))
        {
            throw new InvalidOperationException(FormatCatapultAttackError(attack.Label, attackError));
        }

        if (HasCatapultTroops(attack.Troops))
        {
            var firstAttack = string.Equals(attack.Label, CatapultWavePlanner.FirstAttackLabel, StringComparison.OrdinalIgnoreCase);
            var targetResult = await TrySelectCatapultTargetsAsync(
                attack.Page,
                firstAttack ? request.Target1 : null,
                firstAttack ? request.Target2 : null,
                cancellationToken);
            if (!targetResult.Success)
            {
                throw new InvalidOperationException(targetResult.Message);
            }
        }

        return attack with { DurationSeconds = await TryReadAttackDurationSecondsAsync(attack.Page, cancellationToken) };
    }

    private async Task<IPage> OpenCatapultWaveTabAsync(IPage sourcePage, CancellationToken cancellationToken)
    {
        var newPageTask = sourcePage.Context.WaitForPageAsync();
        await DelayBeforeCatapultPreparationActionAsync(cancellationToken);
        if (!await TryClickCatapultSendButtonAsync(sourcePage, 0, [KeyboardModifier.ControlOrMeta], cancellationToken))
        {
            throw new InvalidOperationException("Could not open a catapult wave tab from Send Troops.");
        }

        IPage newPage;
        try
        {
            newPage = await newPageTask.WaitAsync(TimeSpan.FromMilliseconds(_config.TimeoutMs), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("Catapult wave tab did not open after Ctrl+Send.");
        }

        if (!await SendTroopsNavigator.IsSendTroopsPageAsync(sourcePage, cancellationToken))
        {
            throw new InvalidOperationException("Opening a catapult wave tab navigated the original Send Troops tab.");
        }

        return newPage;
    }

    private static Task DelayBeforeCatapultPreparationActionAsync(CancellationToken cancellationToken)
    {
        var delayMilliseconds = Random.Shared.Next(
            CatapultPreparationDelayMinMilliseconds,
            CatapultPreparationDelayMaxMilliseconds + 1);
        return Task.Delay(delayMilliseconds, cancellationToken);
    }

    private async Task EnsureCatapultConfirmReadyAsync(
        PreparedCatapultAttack attack,
        int delayMilliseconds,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await attack.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
                {
                    Timeout = Math.Min(_config.TimeoutMs, 1000),
                });
            }
            catch (TimeoutException)
            {
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
            }

            var error = await TryReadAttackErrorAsync(attack.Page, cancellationToken);
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException(FormatCatapultAttackError(attack.Label, error));
            }

            if (await HasVisibleSelectorAsync(attack.Page, CatapultConfirmButtonSelectors))
            {
                return;
            }

            await Task.Delay(delayMilliseconds, cancellationToken);
        }

        throw new InvalidOperationException($"Confirmation page is not ready for {attack.Label.ToLowerInvariant()}.");
    }

    private async Task<bool> WaitForCatapultSendResultAsync(PreparedCatapultAttack attack, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await attack.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
                {
                    Timeout = Math.Min(_config.TimeoutMs, 800),
                });
            }
            catch (TimeoutException)
            {
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
            }

            var error = await TryReadAttackErrorAsync(attack.Page, cancellationToken);
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException(FormatCatapultAttackError(attack.Label, error));
            }

            if (!await HasVisibleSelectorAsync(attack.Page, CatapultConfirmButtonSelectors))
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return false;
    }

    private async Task ClearTroopInputsAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await page.EvaluateAsync(
            """
            () => {
              const isTroopInput = (input) => {
                const name = (input.getAttribute('name') || '').toLowerCase();
                const id = (input.id || '').toLowerCase();
                return /(^|\[)t\d+(\]|$)/.test(name) || /^t\d+$/.test(id);
              };

              for (const input of Array.from(document.querySelectorAll('input[type="text"], input[type="number"], input:not([type])'))) {
                if (!isTroopInput(input) || input.disabled || input.readOnly || input.getAttribute('disabled') !== null) continue;
                input.value = '';
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
              }
            }
            """);
    }

    private async Task<IReadOnlyDictionary<string, long>> ReadAvailableTroopsOnCurrentSendTroopsPageAsync(string tribe, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var troopType in TroopCatalog.ResolveTroopTypesForTribe(tribe))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var troopIndex = TroopCatalog.ResolveTroopIndex(troopType);
            if (troopIndex is null)
            {
                result[troopType] = 0;
                continue;
            }

            result[troopType] = await ReadAvailableTroopCountAsync($"t{troopIndex.Value}", cancellationToken) ?? 0;
        }

        return result;
    }

    private async Task<int?> TryReadRallyPointLevelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _page.EvaluateAsync<int?>(
                """
                () => {
                  const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
                  const candidates = [
                    ...Array.from(document.querySelectorAll('h1, h2, .titleInHeader, .buildingTitle, .content .title, .content')),
                  ].map(element => clean(element.textContent));

                  for (const text of candidates) {
                    if (!/rally\s*point/i.test(text)) continue;
                    const match = text.match(/(?:level|lvl)\s*(\d{1,2})/i)
                      || text.match(/(\d{1,2})\s*(?:level|lvl)/i);
                    if (match) return Number(match[1]);
                  }

                  const body = clean(document.body?.innerText || '');
                  const bodyMatch = body.match(/rally\s*point.{0,80}(?:level|lvl)\s*(\d{1,2})/i)
                    || body.match(/(?:level|lvl)\s*(\d{1,2}).{0,80}rally\s*point/i);
                  return bodyMatch ? Number(bodyMatch[1]) : null;
                }
                """);
        }
        catch (Exception ex)
        {
            Notify($"[catapult:verbose] could not read Rally Point level: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> HasVisibleSelectorAsync(IPage page, IEnumerable<string> selectors)
    {
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            if (await locator.IsVisibleAsync())
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> TryFillTroopInputAsync(IPage page, string fieldToken, string troopType, int troopCountToSend, CancellationToken cancellationToken)
    {
        return await SetCatapultInputValueAsync(
            page,
            BuildTroopInputSelectors(fieldToken),
            troopCountToSend.ToString(CultureInfo.InvariantCulture),
            troopType,
            cancellationToken);
    }

    private async Task<bool> SetCatapultInputValueAsync(
        IPage page,
        IEnumerable<string> selectors,
        string value,
        string? troopType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await page.EvaluateAsync<bool>(
            """
            (args) => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const inputs = Array.from(document.querySelectorAll('input[type="text"], input[type="number"], input:not([type])'));
              const isUsable = (node) => {
                if (node.disabled || node.readOnly || node.getAttribute('disabled') !== null) return false;
                const style = window.getComputedStyle(node);
                const rect = node.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
              };
              const candidate = args.selectors
                .map(selector => document.querySelector(selector))
                .find(node => node instanceof HTMLInputElement && isUsable(node))
                || (!args.troopType ? null : inputs.find(node => {
                if (!isUsable(node)) return false;
                const text = normalize(`${node.closest('tr, td, div, label, li, .troop_details, .details')?.textContent || ''} ${node.getAttribute('title') || ''} ${node.getAttribute('aria-label') || ''}`);
                return text.includes(normalize(args.troopType));
              }));
              if (!candidate) return false;
              candidate.focus();
              const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
              if (setValue) setValue.call(candidate, args.value);
              else candidate.value = args.value;
              candidate.dispatchEvent(new Event('input', { bubbles: true }));
              candidate.dispatchEvent(new Event('change', { bubbles: true }));
              return candidate.value === args.value;
            }
            """,
            new { selectors = selectors.ToArray(), value, troopType });
    }

    private async Task<bool> TrySelectAttackModeAsync(IPage page, bool raidAttack, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await page.EvaluateAsync<bool>(
            """
            (raidAttack) => {
              const eventTypeValue = raidAttack ? '4' : '3';
              const radio = document.querySelector(`input[type="radio"][name="eventType"][value="${eventTypeValue}"]`);
              if (!radio) return false;
              radio.checked = true;
              radio.dispatchEvent(new Event('input', { bubbles: true }));
              radio.dispatchEvent(new Event('change', { bubbles: true }));
              return radio.checked;
            }
            """,
            raidAttack);
    }

    private async Task<bool> WaitForSendTroopsConfirmationPageAsync(IPage page, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded)
                    .WaitAsync(cancellationToken);
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
            }

            if (await HasVisibleSelectorAsync(page, CatapultConfirmButtonSelectors))
            {
                return true;
            }

            if (attempt < 12)
            {
                await Task.Delay(200, cancellationToken);
            }
        }

        return false;
    }

    private Task<bool> TryClickCatapultSendButtonAsync(
        IPage page,
        int delayMilliseconds,
        CancellationToken cancellationToken)
        => TryClickCatapultButtonAsync(page, CatapultSendButtonSelectors, delayMilliseconds, null, cancellationToken);

    private Task<bool> TryClickCatapultSendButtonAsync(
        IPage page,
        int delayMilliseconds,
        IReadOnlyList<KeyboardModifier> modifiers,
        CancellationToken cancellationToken)
        => TryClickCatapultButtonAsync(page, CatapultSendButtonSelectors, delayMilliseconds, modifiers, cancellationToken);

    // Intentionally separate from SendTroops' TryClickConfirmButtonAsync: Catapult has a distinct
    // confirmation form and must never click Send again after the page reload.
    private Task<bool> TryClickCatapultConfirmButtonAsync(
        IPage page,
        int delayMilliseconds,
        CancellationToken cancellationToken)
        => TryClickCatapultButtonAsync(page, CatapultConfirmButtonSelectors, delayMilliseconds, null, cancellationToken);

    private async Task<bool> TryClickCatapultButtonAsync(
        IPage page,
        IReadOnlyList<string> selectors,
        int delayMilliseconds,
        IReadOnlyList<KeyboardModifier>? modifiers,
        CancellationToken cancellationToken)
    {
        foreach (var selector in selectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var locator = page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            if (!await locator.IsVisibleAsync())
            {
                continue;
            }
            await DelayBeforeCatapultWaveClickAsync(delayMilliseconds, cancellationToken);
            await locator.ClickAsync(new LocatorClickOptions
            {
                Timeout = _config.TimeoutMs,
                Modifiers = modifiers,
            });
            return true;
        }

        return false;
    }

    private async Task DelayBeforeCatapultWaveClickAsync(int delayMilliseconds, CancellationToken cancellationToken)
    {
        Notify($"[catapult:verbose] click pacing {delayMilliseconds} ms");
        await Task.Delay(delayMilliseconds, cancellationToken);
    }

    private async Task<string?> TryReadAttackErrorAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await page.EvaluateAsync<string?>(
                """
                () => {
                  const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
                  const error = Array.from(document.querySelectorAll('p.error, .error'))
                    .map(node => normalize(node.textContent))
                    .find(text => text.length > 0);
                  return error || null;
                }
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return null;
        }
    }

    private static string FormatCatapultAttackError(string attackLabel, string error)
    {
        return $"ALARM: Could not send catapult {attackLabel.ToLowerInvariant()}: {error}";
    }

    private async Task<CatapultTargetSelectResult> TrySelectCatapultTargetsAsync(IPage page, string? target1, string? target2, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await page.EvaluateAsync<CatapultTargetSelectResult>(
            """
            (args) => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const wanted = [args.target1, args.target2].map(value => normalize(value) || 'random');
              const explicitSecondTarget = normalize(args.target2).length > 0;
              const selects = Array.from(document.querySelectorAll('select')).filter(select => {
                const name = normalize(`${select.getAttribute('name') || ''} ${select.id || ''}`);
                const context = normalize(select.closest('tr, div, td, label, form')?.textContent || '');
                const options = normalize(Array.from(select.options || []).map(option => option.textContent || '').join(' '));
                return name.includes('ctar') || name.includes('target') || name.includes('kata') || name.includes('building') ||
                  context.includes('target') || context.includes('building') || options.includes('random');
              });

              if (selects.length === 0) {
                return { success: false, message: 'Could not find catapult target fields on the confirmation page.' };
              }

              const chooseOption = (select, desired) => {
                const options = Array.from(select.options || []);
                let match = options.find(option => normalize(option.textContent) === desired || normalize(option.value) === desired);
                if (!match && desired !== 'random') {
                  match = options.find(option => normalize(option.textContent).includes(desired));
                }
                if (!match) {
                  match = options.find(option => normalize(option.textContent).includes('random') || ['0', '-1', '99'].includes((option.value || '').trim()));
                }
                if (!match) {
                  match = options[0];
                }
                if (!match) {
                  return false;
                }

                select.value = match.value;
                select.dispatchEvent(new Event('input', { bubbles: true }));
                select.dispatchEvent(new Event('change', { bubbles: true }));
                return true;
              };

              if (!chooseOption(selects[0], wanted[0])) {
                return { success: false, message: `Could not select catapult target '${args.target1 || 'Random'}'.` };
              }

              if (selects.length > 1) {
                if (!chooseOption(selects[1], wanted[1])) {
                  return { success: false, message: `Could not select second catapult target '${args.target2 || 'Random'}'.` };
                }
              } else if (explicitSecondTarget) {
                return { success: false, message: 'Second catapult target was requested, but only one target field is available.' };
              }

              return { success: true, message: '' };
            }
            """,
            new { target1, target2 });
    }

    private async Task<int?> TryReadAttackDurationSecondsAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await page.EvaluateAsync<int?>(
            """
            () => {
              const text = (document.body?.innerText || '').replace(/\s+/g, ' ').trim();
              const patterns = [
                /(?:Duration|Dauer|Durée|Durata|Varaktighet|Tijd)[^\d]*(\d{1,2}):(\d{2}):(\d{2})/i,
                /(?:Duration|Dauer|Durée|Durata|Varaktighet|Tijd)[^\d]*(\d{1,2}):(\d{2})/i
              ];
              for (const pattern of patterns) {
                const match = text.match(pattern);
                if (!match) continue;
                if (match.length === 4) {
                  return Number(match[1]) * 3600 + Number(match[2]) * 60 + Number(match[3]);
                }
                return Number(match[1]) * 60 + Number(match[2]);
              }
              return null;
            }
            """);
    }

    private sealed record PreparedCatapultAttack(
        IPage Page,
        string Label,
        IReadOnlyDictionary<string, int> Troops,
        int? DurationSeconds);

    private sealed class CatapultTargetSelectResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; init; }

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;
    }

    private static bool HasCatapultTroops(IReadOnlyDictionary<string, int> troops)
    {
        return troops.Any(pair => pair.Value > 0 && TroopCatalog.ResolveTroopIndex(pair.Key) == 8);
    }

    private static void VerifyCatapultArrivalOrder(IReadOnlyList<PreparedCatapultAttack> prepared)
    {
        if (prepared.Count <= 1)
        {
            return;
        }

        var firstDuration = prepared[0].DurationSeconds;
        if (firstDuration is null)
        {
            return;
        }

        foreach (var wave in prepared.Skip(1))
        {
            if (wave.DurationSeconds is not int waveDuration)
            {
                continue;
            }

            if (firstDuration.Value > waveDuration)
            {
                throw new InvalidOperationException(
                    $"First attack travel time is longer than {wave.Label.ToLowerInvariant()}. Add slower troops to waves or use a faster first attack.");
            }
        }
    }
}
