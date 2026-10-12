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
     needs is queued in front of it. Building templates are reusable build orders you can apply to
     any village.
   - **Hero** — adventures (minimum HP, shortest/first), auto-revive, the order and maximum for
     attribute points, adventure bonus videos, and using the hero's inventory resources when a
     build is short on resources.
   - **Farming** — create farm lists, add targets by coordinates, and send lists on a timer (all
     lists, or only the ones you tick). Targets with losses can be switched off or moved to another
     list. The oasis scan and the Travco inactive-village search find targets you can add to a list.
     The game itself only lets Gold Club accounts press a list's Start button, so without Gold Club
     the bot sends each active target of the selected lists as an ordinary raid from the Rally Point.
     In that mode it remembers which raids are still out and skips those targets, and it reads your
     raid reports: a target whose latest raid lost troops is put on hold until you release it.
   - **Troops** — per-village training rules for Barracks, Stable and Workshop, Smithy upgrade
     targets, and Town Hall and Brewery celebrations.
   - **Transfers** — send resources and reinforcements between your own villages on a schedule.
   - **Catapults** — prepare a first attack plus following waves and send them together.
   - **Under attack** (in Settings) — the bot checks for incoming attacks every few minutes, sends
     a macOS notification, spends resources shortly before the hit and can move your troops out on
     a raid timed to return afterwards. An overflow guard trains troops or queues a field upgrade
     when a resource is about to hit its storage cap.
   - **Settings** — startup (log in and start the bot when the app opens) and session pacing (play
     for a while, close the browser for a break, optional active hours), NPC trade and gold limits, bonus videos (25% faster build, +15% production),
     and marking messages and reports as read.
4. **Start bot**. The Dashboard shows what runs next; the **Log** tab shows everything the bot does.

The game language must be English (Settings → *Set game language to English*).

## AI agents

An agent can read the bot's state and change its tasks and build plans through the app's local
API. While the app runs, `~/Library/Application Support/TbotUltra/agent/` holds the connection
details, a state snapshot, a `tbot` command-line helper and the guide; the same guide is in
[docs/AGENT_API.md](../docs/AGENT_API.md).

## Data

Accounts, settings, the saved browser session and logs live in
`~/Library/Application Support/TbotUltra`. Credentials are stored locally in the `.env` file there
(readable only by your user) and are never sent anywhere except the Travian login page.
Set `TBOT_ROOT` to use a different folder (for example one per account when running several copies).

## Not in the macOS build

The Windows app has more tools than this front-end exposes yet: proxy library and failover, incoming-attack alerts, report screenshots, and the
Windows app's deeper build planning (requirement repair, time estimates).
