# macOS app — developer guide

How the macOS app is put together, how to change it, and what to watch out for. For what the app
does from a user's point of view see [macos/README.md](../macos/README.md); for the HTTP API as a
client sees it see [AGENT_API.md](AGENT_API.md). Engine rules are in
[ENGINEERING_NOTES.md](ENGINEERING_NOTES.md).

## 1. How it fits together

```
Tbot Ultra.app
├─ Contents/MacOS/TbotUltra              native window (macos/TbotUltraApp.m, Cocoa + WKWebView)
└─ Contents/Resources/server/TbotUltraMac  self-contained .NET 8 server (src/TbotUltra.Mac)
        ├─ serves Web/index.html and /api/* on 127.0.0.1:<random port>
        └─ BotEngine → BotTaskRunner (TbotUltra.Worker) → Playwright → Google Chrome
```

1. The shell starts the server as a child process and reads its stdout until it prints
   `TBOT_LISTENING http://127.0.0.1:<port>`, then loads that address in a WKWebView.
2. The server makes a random token at each launch and writes it into the page it serves
   (`__TBOT_TOKEN__` in `index.html`). Every `/api` call must carry it in `X-Tbot-Token`, and the
   `Host` header must be `127.0.0.1` or `localhost`. Keep both checks: the API drives a logged-in
   game session.
3. The page polls `GET /api/state` every 1.5 seconds and redraws itself from the answer. There is no
   client-side store: the server is the only source of truth.
4. On quit the shell sends SIGTERM so the server can close the automated browser, then kills it if
   it has not exited.

## 2. Files

| File | Responsibility |
|---|---|
| `src/TbotUltra.Mac/Program.cs` | Startup, the security middleware, every endpoint, `BuildSummary()` (agent summary), request records. |
| `src/TbotUltra.Mac/BotEngine.cs` | Login, the job loop, job scheduling, and the Mac-only behaviour (attack watch, overflow guard, crop watch, autofarm, pacing, daily summary, keep-awake, notifications). Also the view records sent to the UI. |
| `src/TbotUltra.Mac/SettingsStore.cs` | The persisted model (`MacSettings`, `FeatureToggles`, `VillageSettings`, `BuildItem`, `FarmRaidState`, `DailyStats`) and its load/save. |
| `src/TbotUltra.Mac/MacDefaults.cs` | Default value of every option the UI exposes. The single source of defaults. |
| `src/TbotUltra.Mac/BuildPlanning.cs` | Building templates (`TemplateStore`) and `BuildPlanner`: storage preflight, template expansion, costs, time estimates, crop warning. |
| `src/TbotUltra.Mac/AccountStore.cs` | Accounts in the `.env` file, same format as the Windows app. |
| `src/TbotUltra.Mac/AgentFiles.cs` | Writes the `agent/` folder (connection, state snapshot, guide, `tbot` helper). |
| `src/TbotUltra.Mac/LogBuffer.cs` | In-memory log for the UI plus the daily log file. |
| `src/TbotUltra.Mac/MacPaths.cs` | Data root and embedded resources. |
| `src/TbotUltra.Mac/Web/index.html` | The whole UI: CSS, markup and vanilla JavaScript in one file. No build step, no dependencies. |
| `macos/TbotUltraApp.m` | The native window. |
| `macos/build-mac.sh`, `macos/redeploy.sh` | Packaging and the development loop. |
| `macos/tbot` | Command-line helper for the API; copied into the `agent/` folder at launch. |

`index.html`, `docs/AGENT_API.md`, `macos/tbot` and `.release-template/config/bot.json` are embedded
in the server binary (see the `.csproj`). A change to any of them needs a rebuild to show up.

## 3. Data on disk

Everything lives in `~/Library/Application Support/TbotUltra` (override with `TBOT_ROOT`). The engine
treats this folder as its project root.

| Path | Content |
|---|---|
| `.env` | Accounts and credentials. **Never read, print or copy it.** |
| `config/bot.json` | Engine config, created from the release template on first run. |
| `config/mac_building_templates.json` | Building templates (shared by all accounts). |
| `config/accounts/<name>/mac_settings.json` | `MacSettings` for one account. |
| `config/accounts/<name>/mac_villages.json` | Last known villages, so the UI has data before login. |
| `config/accounts/<name>/mac_hero.json` | Last known hero state and inventory, so estimates count it right after a launch. |
| `config/accounts/<name>/session`, `cache`, `analysis` | Engine-owned browser session and caches. |
| `agent/` | `connection.json` (URL, token, pid), `state.json`, `AGENT_GUIDE.md`, `tbot`. |
| `logs/tbot-YYYY-MM-DD.log` | Everything shown in the Log tab. |
| `diagnostics/` | Game pages saved with `POST /api/diagnostics/page`. |

Adding a property to a settings class is backwards compatible as long as it has a default. Do not
rename or remove persisted properties without a migration: users' files would silently lose data.

## 4. The engine loop

`BotEngine.RunLoopAsync` runs **one job at a time**; the browser is a single shared session.

Each pass:

1. `ResolveWakeTime` — if session pacing says it is break time, close the browser and sleep.
2. Cheap checks that need no browser: `RespondToAttacksAsync`, `SendDailySummaryIfDue`,
   `GuardAgainstOverflow`, `WatchCropBalance`.
3. `BuildJobs(settings, account)` — builds the job list from the feature switches and each enabled
   village's build queue, troop rules and celebrations.
4. Picks the job with the earliest due time (`DueAt(key)`), waits if it is not due, otherwise runs
   it through `ExecuteJobCoreAsync`.

A job is `Job(Key, Title, Task, Payload, MinMinutes, MaxMinutes, VillageKey?, BuildItemId?)`:

- `Key` identifies the schedule slot (`hero`, `farming`, `build:<village>:<item>`…).
- `Task` is either an engine task name run with `_runner.ExecuteOnceAsync(options, Log, [task])`, or
  one of the Mac-only constants at the top of `BotEngine` (`MarkReadTask`, `AttackWatchTask`,
  `CultureTask`, `AutoFarmTask`, `AcademyTask`, `TrapsTask`), which `ExecuteJobCoreAsync` handles before the generic path.
- `Payload` holds option overrides for this run only.
- After a run the job is rescheduled with `Schedule(key, delay, resultText)`. On success the delay is
  random between `MinMinutes` and `MaxMinutes`.

How failures are handled in `ExecuteJobCoreAsync`:

| Exception | Meaning | Handling |
|---|---|---|
| `TaskWaitException` | Not possible yet (resources, queue full) | Retry after the wait the engine reports, capped by `MaxDeferral`. |
| `TaskBlockedPermanentlyException` | Cannot succeed as configured | Build item is marked `blocked` with a note; other jobs wait two hours. |
| `AccountAccessException`, `ManualVerificationRequiredException`, `UnexpectedTravianLanguageException` | Needs the user (restricted account, captcha, non-English game) | Rethrown: the loop stops and shows an alert. |
| anything else | Unexpected | Counted in `_failures`, retried after 2, 4, 8… minutes (at most 60). |
| `OperationCanceledException` | Stop or Cancel pressed | Always rethrown. Never swallow it. |

Build items are finished by `CompleteBuildRun`, which reads the engine's `ConstructionTaskOutcome`
and removes the item, keeps it for another level, or marks it blocked.

### Options

The engine is configured through `BotOptions`. `BuildOptions(account, settings)` produces it from a
flat `Dictionary<string, string>`:

1. `MacDefaults.Options`
2. overlaid by `settings.Options` (what the user changed)
3. plus values derived from other settings (farm list names, village settings)
4. applied with `BotOptionsPayloadApplier.Apply`, keyed by `BotOptionPayloadKeys` names.

A value that is a JSON array is expanded to `key:0`, `key:1:Prop`… by `TryExpandJsonList`, which is
how list-shaped options (transfers, reinforcements) are stored.

Keys that start with `mac_` are read only by the Mac app (`OptionOn(settings, "mac_…")`,
`PacingValue`, `PacingMinutes`); the engine ignores them.

## 5. Recipes

### Add a setting

1. Add the key and its default to `MacDefaults.Options`. Use an existing `BotOptionPayloadKeys` name
   if the engine already has the option; otherwise name it `mac_<something>`.
2. Add a control in `index.html` with `boolOption`, `numberOption`, `textOption` or `selectOption`.
   They write the key through `PUT /api/settings/options` on change; nothing else is needed.
3. For a `mac_` key, read it in `BotEngine` with `OptionOn` / `PacingValue`.
4. List the key in the Options table of `docs/AGENT_API.md`.

### Add a feature switch

1. Add a `bool` to `FeatureToggles` (`SettingsStore.cs`).
2. Add it to the `FEATURES` array in `index.html` (camelCase name, label).
3. Add the job in `BotEngine.BuildJobs` behind the switch.
4. Document it under "Feature switches" in `docs/AGENT_API.md`.

### Expose an engine task that already exists

Find the task name in the Worker (`BotTaskRunner` partials; see `ARCHITECTURE.md` §3) and add
`jobs.Add(new Job("key", "Title", "task_name", payload, min, max))` in `BuildJobs`. Options the task
needs go into the payload or the defaults.

### Add a one-off action (a button)

1. Add `public Task XAsync()` on `BotEngine` that calls `RunManualAsync("Title", async ct => { … })`.
   `RunManualAsync` takes the browser lock, sets the activity text and reports errors in the log.
2. Add `app.MapPost("/api/…", () => Fire(engine.XAsync));` in `Program.cs`. `Fire` returns at once
   and runs the work in the background; progress reaches the UI through `/api/state` and the log.
3. Call it from the page: `api("POST", "/api/…")`.

### Add data to the UI

1. Keep the data in a field on `BotEngine` (guarded by `_sync`) and expose a view record.
2. Add it to the anonymous object returned by `GET /api/state` in `Program.cs`, and to
   `BuildSummary()` if agents should see it.
3. Render it in the matching `render…()` function in `index.html`.

### Add an engine capability

Only when the Worker cannot already do it. Follow the Worker's own conventions
(`ENGINEERING_NOTES.md`): `TravianClient` partials own page interaction, `BotTaskRunner` partials
expose it. Add a unit test in `src/TbotUltra.Worker.Tests` for any parsing. Keep the diff small —
this project is shared with the Windows app and merged from upstream.

## 6. The web UI

- One file, no framework. `state` is the last `/api/state` answer; `refresh()` calls every
  `render…()` function, each of which replaces the `innerHTML` of its container.
- `renderStatic()` draws the parts that hold option controls. It is skipped while an input has focus.
- **Wrap any container that has unsaved form inputs in `keep(containerId, key, render)`.** WKWebView
  does not give focus to selects, checkboxes or buttons on click, so "is the user editing?" cannot be
  answered from focus; without `keep` a re-render 1.5 seconds later resets what the user picked.
  `keep` saves the values of inputs that have an `id` (and unbound checkboxes) and restores them after
  the render. Controls that save on change (`data-opt`, `data-feature`, …) are left alone.
- Give every form input an `id` so `keep` can restore it.
- Escape anything that comes from the game or the user with `esc()` before putting it in HTML.
- Colours are CSS variables on `:root`, overridden per theme with `:root[data-theme="…"]`. Use the
  variables (`--btn`, `--btn-border`, `--field`, `--link`, …), never literal colours. Add a theme by
  adding a block of variables and an entry in `THEMES`. The choice is stored in `localStorage` under
  `tbot-theme`; `?theme=<name>` previews one without saving.
- Number inputs show their steppers permanently (`::-webkit-inner-spin-button { opacity: 1 }`).

### Looking at the UI while developing

The same page works in any browser: open the `url` from `agent/connection.json`. The token is
already in the served page. Chromium renders some controls differently from WKWebView (focus,
number steppers, native selects), so confirm visual changes in the app window as well.

## 7. Build, run, test

```bash
./macos/build-mac.sh     # full bundle → artifacts/macos/Tbot Ultra.app
./macos/redeploy.sh      # quit, rebuild, reopen, wait until the bot is running
dotnet build src/TbotUltra.Mac -c Debug          # quick compile check
dotnet test src/TbotUltra.Worker.Tests           # engine unit tests
```

To run the server without the bundle (UI in a browser, separate data folder so the installed app's
data is untouched):

```bash
TBOT_ROOT=/tmp/tbot-dev dotnet run --project src/TbotUltra.Mac -- --port 5123
```

Then read the token-bearing page at `http://127.0.0.1:5123/`.

Talking to a running app:

```bash
T="$HOME/Library/Application Support/TbotUltra/agent/tbot"
"$T" summary                       # compact state
"$T" get /api/state                # full state
"$T" post /api/jobs/run '{"key":"hero"}'   # run a job now instead of waiting for its timer
```

There is no test project for `TbotUltra.Mac`. Logic worth testing (planning, time resolution) is
written as `internal static` methods so one can be added; until then verify through the API.

### Verifying a change

1. `dotnet build src/TbotUltra.Mac` must be free of errors and new warnings.
2. `./macos/redeploy.sh`, then watch the Log tab or `logs/tbot-<date>.log`.
3. Trigger the job with `POST /api/jobs/run` rather than waiting for its timer.
4. Say plainly what could not be exercised. Several features cannot be tested on a one-village
   account without Gold Club (see §9).

## 8. Platform pitfalls

- **dotnet** is Homebrew's `dotnet@8`, usually not on the PATH. `build-mac.sh` finds it itself; for
  other commands export `PATH=/opt/homebrew/opt/dotnet@8/bin:$PATH` and
  `DOTNET_ROOT=/opt/homebrew/opt/dotnet@8/libexec`.
- **Do not build the solution.** `TbotUltra.sln` contains WPF projects. Build single projects.
- **clang include path.** Some machines have stale headers in `/usr/local/include` that break the
  default search path. `build-mac.sh` compiles with `-nostdlibinc -isystem $SDK/usr/include
  -iframework $SDK/System/Library/Frameworks`; keep those flags. For the same reason the shell is
  Objective-C, not Swift.
- **Ad-hoc signing.** Each rebuild is a new binary to macOS, so it may ask again for permissions
  (notifications, controlling Chrome). The user has to quit and reopen the app once after granting.
- **Keyboard modifiers.** Playwright code must use `ControlOrMeta`, never `Control`. `Control+A` on
  macOS does not select all; it moves the caret, which once turned a troop count of 2 into 20.
- **File modes.** Guard `File.SetUnixFileMode` with `!OperatingSystem.IsWindows()` (CA1416).
- **Sleep.** A sleeping Mac stops the bot. While the bot runs, `caffeinate -i -w <pid>` keeps the
  machine awake (`mac_keep_awake`); stop it whenever the loop stops.
- **Notifications** go through `osascript` (`BotEngine.Notify`). Escape the text.
- **Game language must be English.** The engine matches English page text.

## 9. Game behaviour worth knowing

- **Farm lists without Gold Club.** The game refuses a list's Start button without Gold Club. The
  bot then sends each active target as a normal raid from the Rally Point
  (`Farming/TravianClient.FarmRaids.cs`) and tracks what the list cannot show: raids still out
  (`BusyUntilUtc`), targets on hold after losses (`OnHold`), and raid size per target (`TroopScale`).
- **Build time estimates** use the standard formula (same as kirilloid's: base time ÷ world speed ×
  0.964 per Main Building level above 1), 25% shorter where the bot watches the bonus video
  (`BuildPlanner.WithVideo`). Do not calibrate from the durations the engine logs: on upgrade pages
  it sometimes reads the troop-training or video-discounted time instead. Production is read from
  the game and collapses while the hero is dead.
- **Read the page after acting.** Several false failures came from reading too early: the hero
  inventory before its items rendered, the hero status right after sending an adventure. Wait for the
  element, or re-read once, before reporting failure.
- **Contextual help bubbles** can sit on top of a button and swallow the click; `BrowserSession`
  injects CSS that lets clicks through.
- **Academy research and traps** are verified live on a Gaul account (trap queued, Swordsman
  researched with a hero top-up). On the Academy page, researchable troops are in
  `.build_details.researches`, troops with unmet requirements in the collapsed `#researchFuture`
  list, and a researched troop is in neither. If a round misbehaves, save the page
  (`POST /api/diagnostics/page` with `{"path":"/build.php?gid=22"}` or `gid=36`) and compare it
  with the selectors in `TravianClient.Academy.cs` / `TravianClient.Traps.cs`.
- **Not verified live** (the first test account had one village, no Gold Club, Egyptian tribe): Brewery
  celebrations, resource transfers and reinforcements between villages, Travco results, loss
  handling with Gold Club, a real incoming attack with evasion, the celebration-pause branch. Code for
  these exists and follows the Windows app's behaviour; treat bug reports there as plausible.

## 10. Worker files changed for macOS

Kept short on purpose; these are the places to look when merging from upstream.

| File | Change |
|---|---|
| `Infrastructure/PlaywrightDriverLayout.cs`, `BrowserSession*.cs`, `ChromiumInstaller.cs`, `LaunchedBrowserRegistry.cs` | Chrome detection, driver paths and process handling on macOS. |
| `Core/TravianClient.Navigation.cs`, `Combat/TravianClient.Catapults.cs` | `ControlOrMeta` instead of `Control`. |
| `Farming/TravianClient.FarmLists.cs`, `FarmAdd.cs`, `FarmRaids.cs`, `BotTaskRunner.Farming.cs` | Farming without Gold Club. |
| `Resources/TravianClient.NpcTrade.cs`, `Buildings/TravianClient.Buildings.ConstructFlow.cs` | NPC trade when constructing a new building. |
| `Buildings/TravianClient.Academy.cs`, `Training/TravianClient.Traps.cs`, `Domain/AcademyAndTrapModels.cs`, `BotTaskRunner.Combat.cs`, `Core/TravianClient.Selectors.cs` (`BuildByGid`) | New: Academy research and trap building. Not in upstream; called directly from `BotEngine`, not registered as engine tasks. |
| `Hero/TravianClient.Hero.Inventory.cs`, `Hero/TravianClient.Hero.Adventures.cs` | Wait for inventory items; re-check the hero after sending an adventure. |
| `Features/MapOasisApiParser.cs`, `Features/TravianClient.MapOasis.cs`, `BotTaskRunner.VillageReads.cs` | Free valleys and culture points for expansion planning. |
| `Travco/TravcoInactiveSearch.cs`, `Core/TravianUrls.cs` | Parser and path fixes. |

## 11. Git

- `origin` = upstream `fetmamm/TravianBot-Ultra`; `fork` = `BragaD/TravianBot-Ultra`.
- Work on `macos-port`, push with `git push fork macos-port`. Never push to `origin`.
- Commit messages: `Mac app: <what>` for the app, `Worker: <what>` for engine changes.
- To take upstream changes: `git fetch origin && git merge origin/main`, then run the Worker tests
  and re-check the files in §10.
