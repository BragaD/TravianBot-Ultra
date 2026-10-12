# Tbot Ultra for macOS — guide for AI agents

This document is for an AI agent (or a script) that supervises a running Tbot Ultra: read what the
bot and the villages are doing, then adjust its tasks and build plans. Everything here works while
the app is open. You talk to the same local HTTP API the app's own window uses.

The running app also writes this guide to `agent/AGENT_GUIDE.md` in its data folder and serves it
at `GET /api/agent/guide`.

## 1. Connect

The data folder is `~/Library/Application Support/TbotUltra` (or `$TBOT_ROOT` if set). While the app
runs, `agent/` inside it contains:

| File | What it is |
|---|---|
| `connection.json` | `url`, `token`, `tokenHeader`, `pid`, `startedAtUtc`. Deleted when the app quits. |
| `state.json` | The same JSON as `GET /api/agent/summary`, rewritten every 15 seconds. Read-only. |
| `AGENT_GUIDE.md` | This guide. |
| `tbot` | A small shell helper around `curl` (see below). |

The port and the token change every time the app starts, so read `connection.json` each session.
If the file is missing, the app is not running. Start it with
`open -a "Tbot Ultra"` (or open the app bundle) and wait for the file to appear.

Every request needs the header `X-Tbot-Token: <token>`. Requests without it get `403`.
Also send `X-Tbot-Agent: <your-name>`; each change you make is then written to the app's log as
`[agent:<your-name>] PUT /api/...`, so the user can see what you did.

### With the helper

```bash
TBOT="$HOME/Library/Application Support/TbotUltra/agent/tbot"
export TBOT_AGENT="planner"
"$TBOT" summary                       # compact state
"$TBOT" get  /api/catalog             # buildings and troops for this tribe
"$TBOT" post /api/start               # start the bot
"$TBOT" put  /api/settings/options '{"hero_min_hp_for_adventure":"40"}'
```

### With curl

```bash
C="$HOME/Library/Application Support/TbotUltra/agent/connection.json"
URL=$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["url"])' "$C")
TOKEN=$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["token"])' "$C")
curl -s -H "X-Tbot-Token: $TOKEN" "$URL/api/agent/summary"
```

### If you can only read files

Read `agent/state.json`. It is at most 15 seconds old (`generatedAtUtc` says exactly). You cannot
change anything this way; changes need the HTTP API.

## 2. Conventions

- All bodies are JSON. Property names are camelCase and matched case-insensitively.
- All times are UTC ISO-8601. A `dueUtc` of year 0001 means "due now".
- Successful changes return `200` (sometimes with a JSON body) or `202 Accepted` for work that
  continues in the background (login, scans). After a `202`, poll the summary until
  `status.busy` is empty, then check `status.alert`.
- Errors return `400` with `{"error": "<plain-language reason>"}`. `403` means a bad token.
- A village is addressed by its `key`, for example `xy:95|80`. In a URL path, encode the pipe:
  `xy:95%7C80`.
- Option values are always strings, even numbers and booleans: `"40"`, `"true"`.

## 3. Read the state

### `GET /api/agent/summary` — start here

```jsonc
{
  "generatedAtUtc": "2026-10-11T20:40:00Z",
  "status": {
    "loggedIn": true,          // the automated browser has a game session
    "running": true,           // the automation loop is on
    "sleepingUntilUtc": null,  // set while the bot is on a pacing break (browser closed)
    "busy": "",                // a manual operation in progress, e.g. "Scanning villages"
    "activity": "Waiting — next: Train troops in Alpha at 17:31:34",
    "alert": "",               // last problem that needs attention; empty when fine
    "prompt": null             // a question waiting for an answer, see section 7
  },
  "account": { "name": "main", "server": "https://…", "world": "…", "tribe": "Egyptians",
               "gold": 124, "silver": 1526, "unreadMessages": 0, "unreadReports": 3 },
  "hero": { "state": "Alive", "hp": 96, "adventures": 2,
            "inventory": { "wood": 1464, "clay": 1464, "iron": 1722, "crop": 2884 },
            "updatedUtc": "…" },
  "features": { "construction": true, "hero": true, "farming": true, "troops": true, … },
  "villages": [{
    "key": "xy:95|80", "name": "Alpha", "x": 95, "y": 80, "isCapital": true,
    "population": 82, "automated": true, "updatedUtc": "…",
    "stock": [{ "resource": "wood", "current": 4528, "capacity": 6300,
                "perHour": 3790, "fullAtUtc": "…" }, …],
    "cropBalancePerHour": 1853,   // crop per hour after upkeep; negative means troops will starve
    "warehouseCapacity": 6300, "granaryCapacity": 1700,
    "fields":    [{ "slotId": 1, "type": "wood", "name": "Woodcutter", "level": 6 }, …],
    "buildings": [{ "slotId": 26, "name": "Main Building", "level": 11, "gid": 15 }, …],
    "emptySlots": [23, 25, 27],
    "underConstruction": [{ "name": "Warehouse", "level": 8, "finishUtc": "…" }],
    "training": [{ "building": "Barracks", "queuedBatches": 2, "finishUtc": "…" }],
    "buildQueue": [{ "id": "c24dda0870", "type": "building_upgrade", "slotId": 26,
                     "gid": 15, "name": "Main Building", "targetLevel": 12,
                     "status": "pending", "note": "" }],
    "troopRules": { "troop_training_barracks_enabled": "true", … }
  }],
  "farming": { "lists": [{ "name": "Farms 1", "listId": "79", "village": "Alpha",
                           "active": 3, "total": 3 }],
               "selectedLists": ["Farms 1"],
               "raidsOut": { "89|81": "2026-10-11T20:39:10Z" },
               "onHold":  { "92|85": { "reason": "Last raid had losses", "sinceUtc": "…" } },
               "raidScale": { "89|81": 1.25 } },   // raid size factor per target, from carry reports
  "incomingAttacks": [{ "id": "…", "villageKey": "xy:95|80", "villageName": "Alpha",
                        "arrivalUtc": "…", "type": "Raid", "fromPlayer": "…",
                        "fromVillage": "…", "fromX": 90, "fromY": 77 }],   // soonest first
  "expansion": { "culturePoints": { "usedSlots": 1, "maxVillages": 1, "produced": 88,
                                    "needed": 200, "perDay": 51, "updatedUtc": "…" },
                 "slotFree": false,                 // may a new village be founded right now?
                 "enoughCulturePointsAtUtc": "…",    // null when there are already enough
                 "settleSpots": [{ "x": 91, "y": 77, "layout": "1-1-1-15",
                                   "kind": "15 cropper", "distance": 5.0 }] },   // after a search
  "jobs": [{ "key": "hero", "title": "Hero: adventures", "dueUtc": "…", "lastResult": "…" }],
  "recentEvents": [{ "at": "…", "title": "Train troops in Alpha", "result": "…" }]
}
```

Notes on freshness: village data is as of `updatedUtc`. It is refreshed at login, after each build
job, and by `POST /api/scan`. `stock.current` is a snapshot; estimate now as
`current + perHour × hours since updatedUtc`, capped at `capacity`.

### Other reads

| Request | Returns |
|---|---|
| `GET /api/state` | Everything the app's own window uses: the summary's data plus `settings` (all saved options), `defaults`, `templates`, `accounts`, `catalog`-independent details. Larger; prefer the summary. |
| `GET /api/catalog` | `buildings` (`gid`, `name`, `maxLevel`, `requirements`) and `troops` for the account's tribe. Use it to pick a `gid` for a construction. |
| `GET /api/logs?after=<id>` | Log lines with `id` greater than `after` (use `0` first). Each has `id`, `at`, `text`. |
| `GET /api/agent/guide` | This guide as Markdown. |

## 4. Control the session

| Request | Effect |
|---|---|
| `POST /api/login` | Opens the browser and logs in (202). May raise a prompt (section 7). |
| `POST /api/start` | Starts the automation loop. Requires `loggedIn`. |
| `POST /api/stop` | Stops the loop (202). The browser stays open. |
| `POST /api/logout` | Stops the loop and closes the browser (202). |
| `POST /api/wake` | Ends a pacing break early. |
| `POST /api/scan` | Re-reads every village (202). Use before planning if `updatedUtc` is old. |
| `POST /api/cancel` | Cancels the manual operation shown in `status.busy`. |
| `POST /api/alert/clear` | Clears `status.alert` once you have handled it. |
| `POST /api/jobs/run` `{"key":"<job key>"}` | Makes one scheduled job due now. Keys are in `jobs[].key`: `hero`, `farming`, `collect_tasks`, `build:<village key>`, `troops:<village key>`, … |

While `sleepingUntilUtc` is set the browser is closed: reads still work, but anything that needs the
game (login, scan, manual tools) is refused until the break ends or you call `/api/wake`.

## 5. Plan construction

Each village has an ordered `buildQueue`. The bot works on the **first pending item**, waits when
resources or build slots are short, removes the item when its target is reached, then moves on.
An item whose `status` is `blocked` is skipped; its `note` says why.

### Item types

| `type` | Required fields | Meaning |
|---|---|---|
| `resources_all` | `targetLevel`, optional `resourceTypes` (`"wood,clay,iron,crop"`) | Raise every field of those types to the level, lowest first. |
| `resource_field` | `slotId` (1–18), `targetLevel` | Raise one field. |
| `building_upgrade` | `slotId`, `targetLevel` | Raise an existing building. |
| `building_max` | `slotId` | Raise an existing building to its maximum level. |
| `building_construct` | `slotId` (an entry of `emptySlots`), `gid`, `targetLevel` | Build a new building, then raise it to the level. Rally Point is always slot 39, the wall slot 40. |
| `building_demolish` | `slotId`, `targetLevel` | Demolish down to the level. Needs Main Building 10. |

`name` is optional; the app fills it in. Do not send `id`, `status` or `note`.

### Replace the whole queue (preferred for planning)

```bash
"$TBOT" put "/api/villages/xy:95%7C80/build" '{
  "items": [
    { "type": "resources_all", "targetLevel": 7, "resourceTypes": "wood,clay,iron" },
    { "type": "building_upgrade", "slotId": 26, "targetLevel": 15 },
    { "type": "building_construct", "slotId": 23, "gid": 17, "targetLevel": 5 }
  ],
  "preflight": true
}'
```

Returns `{"queue": [...], "notes": [...]}`. With `"preflight": true` (the default follows the user's
setting) the app inserts any Warehouse or Granary upgrade an item needs so that every level fits in
storage; `notes` lists what it added. Read `notes` — they also report anything it could not plan,
and a "Crop warning" when the queued upkeep would push the village's crop balance below zero.

### Edit one item

| Request | Effect |
|---|---|
| `POST /api/villages/{key}/build` with one item | Appends it (with storage preflight). Returns `{"id", "notes"}`. |
| `POST /api/villages/{key}/build/{id}/up` \| `down` | Moves it one place. |
| `POST /api/villages/{key}/build/{id}/remove` | Removes it. |
| `POST /api/villages/{key}/build/{id}/retry` | Sets a `blocked` item back to `pending`. |

### Templates (reusable build orders)

A template is a named list of steps that is turned into queue items for a specific village: an
existing building becomes an upgrade, a missing one becomes a construction in a free slot, and
anything already at the target level is skipped.

| Request | Effect |
|---|---|
| `GET /api/state` → `templates` | Lists them: `id`, `name`, `steps[]`. |
| `PUT /api/templates` `{"id":"", "name":"…", "steps":[…]}` | Creates (empty `id`) or replaces a template. |
| `DELETE /api/templates/{id}` | Deletes it. |
| `POST /api/villages/{key}/template/{id}` | Appends the template's items to the village's queue. Returns `{"added", "notes"}`. |
| `POST /api/villages/{key}/template-from-queue` `{"name":"…"}` | Saves the village's current queue as a new template. |

A step is `{"kind":"resources","targetLevel":5,"resourceTypes":"wood,clay,iron,crop"}` or
`{"kind":"building","gid":15,"name":"Main Building","targetLevel":10}`.

### Planning facts the bot does not work out for you

- **Prerequisites.** `GET /api/catalog` lists each building's `requirements`. If they are not met
  the item waits; put the prerequisite earlier in the queue.
- **Build slots.** Normally one field and one building at a time (two in total with Travian Plus;
  Romans build a field and a building in parallel). The bot waits for a free slot.
- **Cost.** Costs per level are not in the API. Treat `notes` from preflight as the signal that an
  item is expensive for the current storage.

## 6. Change what the bot does

### Feature switches

`PUT /api/settings/features` replaces the whole object, so read `features` first, change what you
need and send all of it back.

| Key | Controls |
|---|---|
| `construction` | Working through build queues. |
| `hero` | Sending the hero on adventures (and reviving / spending points per options). |
| `heroResources` | Using the hero's inventory resources when a build is short. |
| `farming` | Sending farm lists on a timer. |
| `troops` | Training troops per village rules. |
| `smithy`, `townHall`, `brewery` | Per-village smithy upgrades and celebrations. |
| `collectTasks`, `collectDailyQuests` | Claiming rewards. |
| `resourceTransfer`, `reinforcements` | Sending between own villages. |
| `productionBonus` | Watching the +15% production videos. |
| `markRead` | Marking messages and reports as read. |
| `attackWatch` | Checking each village's Rally Point for incoming attacks every few minutes. |

### Options

`PUT /api/settings/options` merges the keys you send into the saved options:

```bash
"$TBOT" put /api/settings/options '{"hero_min_hp_for_adventure":"40","mac_pacing_enabled":"true"}'
```

Current values are `settings.options` in `GET /api/state`; anything not listed there uses
`defaults` from the same response. The most useful keys:

| Key | Values | Meaning |
|---|---|---|
| `hero_min_hp_for_adventure` | `1`–`100` | Do not send the hero below this HP %. |
| `hero_adventure_pick_order` | `shortest` \| `top` | Which adventure to take. |
| `hero_auto_revive`, `hero_auto_assign_points`, `hero_auto_use_ointments` | `true`/`false` | Hero upkeep. |
| `hero_stat_priority` | e.g. `resources,fighting_strength,offence_bonus,defence_bonus` | Order in which free points are spent. |
| `hero_stat_maximums` | e.g. `resources=100,fighting_strength=50,…` | Cap per attribute. |
| `resource_build_strategy` | `lowest_first` \| `smart` | Field order for `resources_all`. |
| `mac_storage_preflight` | `true`/`false` | Auto-insert storage upgrades when queueing. |
| `continuous_farm_dispatch_delay_min_minutes` / `_max_minutes` | minutes | Farm round interval. |
| `continuous_farm_deactivate_red_losses` / `_yellow_losses` | `true`/`false` | Put targets on hold after total / partial losses. |
| `mac_pacing_enabled` | `true`/`false` | Take breaks (close the browser, come back later). |
| `mac_pacing_run_min_minutes` / `_max_minutes`, `mac_pacing_sleep_min_minutes` / `_max_minutes` | minutes | Length of play and rest. |
| `mac_active_from_hour`, `mac_active_to_hour` | `0`–`24` | Hours of the day the bot may play (`0`/`24` = always). |
| `construct_faster_enabled`, `increase_adventures_to_hard`, `reduce_adventure_time` | `true`/`false` | Bonus videos. |
| `hero_ointment_target_hp_percent` | `50`–`100` in tens | Heal the hero with ointments up to this HP (needs `hero_auto_use_ointments`). |
| `mac_attack_notify` | `true`/`false` | macOS notification when a new attack is seen. |
| `mac_attack_spend_enabled`, `mac_attack_spend_lead_minutes` | `true`/`false`, minutes | Run the village's build and training jobs this long before a hit, so less can be plundered. |
| `mac_evasion_enabled`, `mac_evasion_target`, `mac_evasion_lead_minutes`, `mac_evasion_include_hero` | `true`/`false`, `"x\|y"`, minutes, `true`/`false` | Shortly before a hit, send all troops on a raid to the target, timed to return after it. **Sends an attack**: see section 9. |
| `mac_raid_sizing_enabled` | `true`/`false` | Without Gold Club: scale a target's raid up 25% when the last raid came home full, down 20% when under 40% full (limits ×0.5–×4). |
| `mac_autofarm_enabled`, `mac_autofarm_radius`, `mac_autofarm_max_per_run`, `mac_autofarm_interval_hours`, `mac_autofarm_troop`, `mac_autofarm_troop_count`, `mac_autofarm_list` | `true`/`false`, fields, count, hours, troop name, count, list name | Periodically add the nearest empty, unoccupied oases to a farm list. **Adds attack targets**: see section 9. Job key `autofarm`. |
| `mac_overflow_guard_enabled`, `mac_overflow_threshold_percent` | `true`/`false`, `50`–`100` | When a resource reaches this share of storage: train troops, and queue one field upgrade if no build item is pending. |
| `npc_trade_enabled`, `npc_trade_construction_enabled`, `allow_gold_spending`, `gold_limit`, `daily_gold_spending_limit` | see section 9 | NPC trade. **Spends gold.** |

### Per-village settings

`PUT /api/villages/{key}` with any of:

```jsonc
{
  "enabled": true,                // automate this village at all
  "townHall": false, "brewery": false,
  "smithyTargets": "t1=10;t3=5",  // troop slot = target level
  "troops": {                     // replaces the whole rule set for the village
    "troop_training_barracks_enabled": "true",
    "troop_training_barracks_troop_type": "Slave Militia",      // a name from /api/catalog troops
    "troop_training_barracks_amount_mode": "keep_resources",    // or "maximum"
    "troop_training_barracks_keep_resources_percent": "70",
    "troop_training_barracks_max_queue_hours": "no_limit",      // or a number of hours
    "troop_training_barracks_run_mode": "resource_percent",     // or "timed"
    "troop_training_barracks_minimum_resources_percent": "0",
    "troop_training_barracks_timed_min_minutes": "30",
    "troop_training_barracks_timed_max_minutes": "120",
    "troop_training_stable_enabled": "false",
    "troop_training_workshop_enabled": "false"
  }
}
```

The same keys exist with `stable` and `workshop` in place of `barracks`.

### Expansion

| Request | Effect |
|---|---|
| `POST /api/expansion/culture` | Re-reads culture points (202). They are also refreshed every hour or two. |
| `POST /api/expansion/spots` `{"villageKey","radius"}` | Searches the map for unoccupied valleys within `radius` (1–40) fields (202). Results: `expansion.settleSpots`, nearest first; `layout` is wood-clay-iron-crop field counts. |
| `POST /api/villages/{key}/expansion/prepare` | Queues what the village needs to train settlers (Main Building 5, Residence 10), with storage preflight. Returns `{"added","notes"}`. |

The bot does not train settlers or found the village; tell the user when `slotFree` is true and the
Residence is level 10 so they can do that in the game.

### Farming

| Request | Effect |
|---|---|
| `POST /api/farmlists/refresh` | Re-reads the farm lists from the game (202). |
| `PUT /api/settings/farmlists` `{"names":[…],"ids":[…]}` | Which lists to send. Empty arrays mean all lists. |
| `POST /api/farmlists/create` `{"name","villageKey","troopType","troopCount"}` | Creates a list (202). |
| `POST /api/farmlists/targets` `{"listName","troopType","troopCount","coordinates":"12\|-34 15\|-30","oases":false}` | Adds targets (202). The game refuses very small raids; `status.alert` reports refusals. |
| `POST /api/farmlists/release` `{"key":"89\|81"}` | Takes a target off hold. |
| `POST /api/scan/oases` `{"x","y","radius","includeOccupied","types":["Wood","Clay","Iron","Crop","Wood+Crop","Clay+Crop","Iron+Crop"]}` | Scans the map (202). Results appear in `GET /api/state` → `scan.rows` with `x`, `y`, `kind`, `detail` (animals), `distance`. |

## 7. Prompts

Sometimes the bot needs an answer, for example which game world to enter. Then `status.prompt` is:

```json
{ "id": "bed2…", "kind": "world", "message": "Choose the one to play:",
  "options": [{ "value": "1959…", "label": "INTERNATIONAL - X10 …" }] }
```

Answer with `POST /api/prompt` `{"id":"bed2…","value":"1959…"}`. An empty `value` cancels.
Kinds: `world` (pick a game world), `manual_login` (the user must log in by hand in the browser;
only answer `ok` if they confirm they did), `confirm` (send prepared attack waves).
**Do not answer `confirm` or `manual_login` prompts on your own; ask the user.**

## 8. A good supervising loop

1. Read `connection.json`; if missing, the app is not running.
2. `GET /api/agent/summary`.
3. If `status.alert` is not empty, read it, act or tell the user, then `POST /api/alert/clear`.
4. If `status.prompt` is set, handle it as in section 7.
5. If `status.loggedIn` is false and `sleepingUntilUtc` is null, the session ended:
   `POST /api/login`, wait for `busy` to clear, then `POST /api/start`.
6. For each village: if `buildQueue` is empty or has `blocked` items, plan and `PUT` a new queue.
   If a `stock` entry is near `capacity` with nothing queued to spend it, queue something.
7. If `incomingAttacks` is not empty, tell the user; do not change evasion settings yourself.
   Check `recentEvents` for repeated failures of the same job.
8. Wait. Polling the summary once a minute is plenty; the bot's own jobs run on timers of minutes.

Do not poll faster than every few seconds, and do not call `POST /api/scan` in a loop: each scan
navigates the real game.

## 9. What needs the user's say-so

These cost something real or act on other players. Do them only when the user has asked for them:

- **Spending gold**: turning on `allow_gold_spending`, `npc_trade_enabled` or
  `npc_trade_construction_enabled`, or lowering `gold_limit`.
- **Attacks**: turning on `mac_autofarm_enabled`, turning on `mac_evasion_enabled` or changing `mac_evasion_target`,
  `POST /api/catapults/send`, adding farm targets, creating farm lists, releasing a
  target that is on hold after losses, answering a `confirm` prompt.
- **Demolition**: `building_demolish` items.
- **Accounts**: `POST /api/accounts`, `DELETE /api/accounts/{name}`. Never read or write the `.env`
  file in the data folder; it holds the user's game password.
- **Turning pacing off** (`mac_pacing_enabled=false`) or widening active hours: it makes the account
  easier to detect as automated.

Automating a game account can break the game's rules and get the account banned. The user has
accepted that risk for themselves; do not widen it without being asked.
