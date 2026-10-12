using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Trap building at the Gaul Trapper. The Trapper page is an ordinary training form with one unit
// (the trap), so it reuses the troop-training form helpers.
public sealed partial class TravianClient
{
    private const int TrapperGid = 36;

    /// <summary>
    /// Queues traps at the Trapper: as many as the page allows (resources and Trapper capacity), or at
    /// most <paramref name="maxPerRun"/> when that is positive.
    /// </summary>
    public async Task<TrapBuildResult> BuildTrapsAsync(int maxPerRun, CancellationToken cancellationToken = default)
    {
        await GotoAsync(Paths.BuildByGid(TrapperGid), cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        if (!await IsCurrentBuildingPageAsync(TrapperGid, cancellationToken))
        {
            return new TrapBuildResult(TrapBuildOutcome.NoTrapper, "Trapper not found in this village. Build a Trapper first.", 0, null);
        }

        var form = await RetryAsync(
            "Trapper: read the trap form",
            () => _page.EvaluateAsync<TrapFormInfo>(
                """
                () => {
                  const clean = (v) => String(v || '').replace(/\s+/g, ' ').trim();
                  const input = document.querySelector('#build form input[type="text"][name^="t"], #build form input.text[name^="t"]');
                  let maximum = 0;
                  if (input) {
                    const details = input.closest('.details') || input.closest('.action') || input.parentElement;
                    for (const link of details ? details.querySelectorAll('.cta a, a') : []) {
                      const digits = clean(link.textContent).replace(/[^\d]/g, '');
                      if (digits) { maximum = parseInt(digits, 10); break; }
                    }
                  }
                  let queueSeconds = 0;
                  for (const timer of document.querySelectorAll('table.under_progress .timer')) {
                    const value = parseInt(timer.getAttribute('value') || '0', 10);
                    if (value > queueSeconds) queueSeconds = value;
                  }
                  const number = (node) => parseInt(clean(node ? node.textContent : '').replace(/[^\d]/g, '') || '0', 10);
                  return {
                    inputName: input ? (input.getAttribute('name') || '') : '',
                    maximum,
                    queueSeconds,
                    present: number(document.querySelector('#build form .tit .furtherInfo')),
                    capacity: number(document.querySelector('#build_value tr.currentLevel .number'))
                  };
                }
                """),
            cancellationToken: cancellationToken);
        Notify($"[traps] form input='{form.InputName}', maximum={form.Maximum}, present={form.Present}/{form.Capacity}, queue={form.QueueSeconds}s.");

        if (string.IsNullOrWhiteSpace(form.InputName))
        {
            return new TrapBuildResult(TrapBuildOutcome.Failed, "Trapper: the trap form was not found on the page.", 0, null);
        }

        if (form.Maximum <= 0)
        {
            // Either the Trapper is full for its level or there are no resources for a single trap.
            var full = form.Capacity > 0 && form.Present >= form.Capacity;
            return new TrapBuildResult(
                TrapBuildOutcome.NothingToBuild,
                full
                    ? $"Trapper: full ({form.Present}/{form.Capacity} traps). Upgrade the Trapper to hold more."
                    : $"Trapper: not enough resources for a trap yet ({form.Present}/{form.Capacity} built).",
                0,
                form.QueueSeconds > 0 ? form.QueueSeconds : null);
        }

        var amount = maxPerRun > 0 ? Math.Min(maxPerRun, form.Maximum) : form.Maximum;
        if (!await SubmitTroopTrainingFromCurrentPageAsync(form.InputName, amount, useMaxShortcut: false, cancellationToken))
        {
            return new TrapBuildResult(TrapBuildOutcome.Failed, "Trapper: the trap form could not be submitted.", 0, null);
        }

        await WaitForPageReadyAsync(cancellationToken);
        Notify($"[traps] queued {amount} trap(s).");
        return new TrapBuildResult(TrapBuildOutcome.Queued, $"Trapper: queued {amount} trap(s).", amount, null);
    }

    private sealed class TrapFormInfo
    {
        public string InputName { get; set; } = string.Empty;
        public int Maximum { get; set; }
        public int QueueSeconds { get; set; }
        public int Present { get; set; }
        public int Capacity { get; set; }
    }
}
