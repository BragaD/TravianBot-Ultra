# Tbot Ultra for macOS

A native macOS build of Tbot Ultra. It reuses the same automation engine as the Windows app
(`TbotUltra.Worker` + `TbotUltra.Core`) with a Mac front-end (`src/TbotUltra.Mac`) shown in a native
window (`macos/TbotUltraApp.m`).

## Build

Requirements: macOS 12+, the .NET 8 SDK (`brew install dotnet@8`), the Xcode command line tools,
and Google Chrome (recommended; without it the bot falls back to Playwright's Chromium).

```bash
./macos/build-mac.sh
```

The app is written to `artifacts/macos/Tbot Ultra.app`. Drag it to Applications or run it in place.

## Use

1. **Accounts** — add an account: a name, your Travian email and password, and the game world URL
   (for example `https://ts1.x1.europe.travian.com`). Tick *Manual login* if you prefer to type the
   password in the browser yourself.
2. **Log in** — a Chrome window opens and logs in. Your villages appear on the Dashboard.
3. Pick what to automate on the **Dashboard**, then configure it:
   - **Building** — per-village build queue: all resource fields to a level, a single field, upgrade
     a building to a level or to its maximum, construct a new one, or demolish one. Items run top to
     bottom. When an item costs more than the Warehouse or Granary can hold, the storage upgrade it
     needs is queued in front of it, and you are warned when the queued upkeep would push the crop
     balance below zero. Building templates are reusable build orders you can apply to
     any village; a template step can raise all fields or one resource type (wood, clay, iron
     or crop).
   - **Hero** — adventures (minimum HP, shortest/first), auto-revive, healing with ointments, the
     order and maximum for attribute points, bonus videos that make adventures hard or shorter,
     and using the hero's inventory resources when a build is short on resources.
   - **Expansion** — culture points and when the next village becomes possible, a checklist of what
     the village still needs, one click to queue a Residence to level 10, and a map search for free
     valleys (croppers flagged). Town Hall celebrations can be limited to the time culture points
     are short for the next village. Training and sending the settlers is still done by hand.
   - **Farming** — create farm lists, add targets by coordinates, and send lists on a timer (all
     lists, or only the ones you tick). Targets with losses can be switched off or moved to another
     list. The oasis scan and the Travco inactive-village search find targets you can add to a list,
     and the bot can grow a list by itself by adding nearby empty oases every few hours.
     The game itself only lets Gold Club accounts press a list's Start button, so without Gold Club
     the bot sends each active target of the selected lists as an ordinary raid from the Rally Point.
     In that mode it remembers which raids are still out and skips those targets, and it reads your
     raid reports: a target whose latest raid lost troops is put on hold until you release it, and
     raid sizes follow how full the last raid came home.
   - **Troops** — per-village training rules for Barracks, Stable and Workshop, Academy research
     (a list of troops, researched one at a time when resources allow), Smithy upgrade targets,
     trap building at the Trapper (Gauls), and Town Hall and Brewery celebrations.
   - **Transfers** — send resources and reinforcements between your own villages on a schedule.
   - **Catapults** — prepare a first attack plus following waves and send them together.
   - **Under attack** (in Settings) — the bot checks for incoming attacks every few minutes, sends
     a macOS notification, spends resources shortly before the hit and can move your troops out on
     a raid timed to return afterwards. An overflow guard trains troops or queues a field upgrade
     when a resource is about to hit its storage cap.
   - **Daily summary** — the Dashboard counts what was done today (builds, troop batches, raids and
     what they carried, adventures, attacks seen) and a notification sums it up each evening.
   - **Settings** — startup (log in and start the bot when the app opens) and session pacing (play
     for a while, close the browser for a break, optional active hours), keeping the Mac awake
     while the bot runs, NPC trade and gold limits, bonus videos (25% faster build, +15%
     production), marking messages and reports as read, and the colour scheme (Appearance).
4. **Start bot**. The Dashboard shows what runs next; the **Log** tab shows everything the bot does.

The game language must be English (Settings → *Set game language to English*).

## Planning

The Building tab shows roughly when each queued item will be done. The estimate starts from today's
stock, waits for the builds already running, raises production as queued field upgrades finish,
and counts the hero's inventory resources when the bot may use them. Costs are exact; build times
use the standard formula for your world's speed and Main Building level, 25% shorter where the
bot watches the bonus video. Task rewards, raids and troop training are not counted. While the
hero is dead the production it adds is missing, so estimates are much longer until it is revived.

## AI agents

An agent can read the bot's state and change its tasks and build plans through the app's local
API. While the app runs, `~/Library/Application Support/TbotUltra/agent/` holds the connection
details, a state snapshot, a `tbot` command-line helper and the guide. Agents can also ask for the
cost and build time of any level and for a time estimate of a plan before queueing it. The guide is in
[docs/AGENT_API.md](../docs/AGENT_API.md).

## Data

Accounts, settings, the saved browser session and logs live in
`~/Library/Application Support/TbotUltra`. Credentials are stored locally in the `.env` file there
(readable only by your user) and are never sent anywhere except the Travian login page.
Set `TBOT_ROOT` to use a different folder (for example one per account when running several copies).

## Not in the macOS build

The Windows app has more tools than this front-end exposes yet: proxy library and failover, report
screenshots, and automatic repair of missing building requirements.

## Development

To change the app, start with [CLAUDE.md](../CLAUDE.md) and
[docs/MAC_DEVELOPMENT.md](../docs/MAC_DEVELOPMENT.md). `./macos/redeploy.sh` quits the running app,
rebuilds it and reopens it.
