# Tbot Ultra — notes for coding agents

Tbot Ultra automates a Travian account through a real Chrome window (Playwright). This repository is
a fork of `fetmamm/TravianBot-Ultra` that adds a native macOS app. The Windows app (WPF) and the
engine come from upstream; the macOS app is this fork's own work.

## Read these first

| When you are about to… | Read |
|---|---|
| change anything in the macOS app | [docs/MAC_DEVELOPMENT.md](docs/MAC_DEVELOPMENT.md) |
| touch selectors, game paths or engine logic | [docs/ENGINEERING_NOTES.md](docs/ENGINEERING_NOTES.md) (rules, pitfalls, decision log) |
| find where a feature lives | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| use domain terms in code, logs or UI | [CONTEXT.md](CONTEXT.md) |
| drive a running bot instead of editing it | [docs/AGENT_API.md](docs/AGENT_API.md) |

[AGENTS.md](AGENTS.md) holds the upstream author's rules (in Swedish). They apply here too; in short:

- Change only what the task needs. Prefer small local edits over rewrites, and keep existing behaviour
  unless asked otherwise.
- Follow the structure, style and naming already in the file. All code and UI text is English.
- Prefer code that is robust and easy to debug over clever code. Log what new features do.
- Do not add a helper just to save lines: only when the logic is reused, names a domain operation, or
  hides real complexity (retries, error handling, state).
- Handle the obvious failure cases and say why something failed.
- Do not extend the task with your own improvements.

## Layout

| Path | What it is |
|---|---|
| `src/TbotUltra.Core` | Options (`BotOptions`), payload keys, catalogs. No browser, no UI. |
| `src/TbotUltra.Worker` | The engine: `TravianClient` (talks to the game), `BotTaskRunner` (runs named tasks). |
| `src/TbotUltra.Desktop` | Windows WPF app. Does not build on macOS; leave it alone unless asked. |
| `src/TbotUltra.Mac` | macOS app: local web server + single-page UI. Reuses Worker and Core. |
| `macos/` | Native window shell, build script, `tbot` helper, user README. |
| `docs/` | Engineering notes, architecture map, ADRs, agent API guide. |

Dependency direction: `Mac → Worker → Core` and `Desktop → Worker → Core`.

## Build, run, test (macOS)

```bash
./macos/build-mac.sh          # builds artifacts/macos/Tbot Ultra.app
./macos/redeploy.sh           # quit the running app, rebuild, reopen, wait for the bot
dotnet test src/TbotUltra.Worker.Tests   # engine unit tests (about 1,500; all pass on macOS)
```

`dotnet` comes from Homebrew's `dotnet@8`. If it is not on the PATH:

```bash
export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH" DOTNET_ROOT="/opt/homebrew/opt/dotnet@8/libexec"
```

Do not build `TbotUltra.sln` on macOS: it includes the WPF projects. Build or test single projects.

## Rules that are specific to this fork

- **Remotes:** `origin` is upstream (`fetmamm`), `fork` is `BragaD/TravianBot-Ultra`. Mac work lives
  on the `macos-port` branch and is pushed to `fork`. Never push to `origin`.
- **Keep Worker changes minimal.** The Worker is shared with the Windows app and upstream moves fast.
  Put Mac behaviour in `src/TbotUltra.Mac`; change the Worker only for a real engine bug or a
  platform difference, and guard platform code with `OperatingSystem.IsMacOS()` / `IsWindows()`.
- **A live game account is attached.** Running the app logs in to a real Travian account. Reading
  state is always fine. Ask the user before anything that spends gold (NPC trade, instant finish),
  sends an attack, demolishes a building, or deletes an account or farm list.
- **Never read or print credentials.** They live in `~/Library/Application Support/TbotUltra/.env`.
  The user types them in the app; do not open that file, log it, or ask for the password in chat.
- **Update the docs with the code.** A new endpoint or option goes in `docs/AGENT_API.md`; a new
  user-visible feature goes in `macos/README.md`; a new durable rule or pitfall goes in
  `docs/MAC_DEVELOPMENT.md` (Mac) or `docs/ENGINEERING_NOTES.md` (engine).
- **Commit only when the user asks.**
