#!/bin/bash
# Development loop: quit the running app, rebuild the bundle, reopen it and wait until the bot runs.
# Needs "log in and start when the app opens" (Settings → Startup) to come back to a running bot;
# without it the script stops waiting after three minutes and prints the state it found.
set -uo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
ROOT="${TBOT_ROOT:-$HOME/Library/Application Support/TbotUltra}"
TBOT="$ROOT/agent/tbot"

# Compile first, so a build error never leaves the user without a running app.
if command -v dotnet >/dev/null 2>&1 || export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH" DOTNET_ROOT="/opt/homebrew/opt/dotnet@8/libexec"; then
  if ! dotnet build "$REPO/src/TbotUltra.Mac" -c Debug 2>&1 | grep -q " 0 Error(s)"; then
    echo "Compile errors; the running app was left alone:" >&2
    dotnet build "$REPO/src/TbotUltra.Mac" -c Debug 2>&1 | grep " error " | sort -u | cut -c1-240 >&2
    exit 1
  fi
fi

osascript -e 'tell application id "com.tbotultra.mac" to quit' 2>/dev/null
sleep 8

if ! "$REPO/macos/build-mac.sh" 2>&1 | grep -E "error|==> Done" | cut -c1-200 | sort -u | tail -8 | grep -q "==> Done"; then
  echo "Build failed. Run ./macos/build-mac.sh to see the errors." >&2
  exit 1
fi

open "$REPO/artifacts/macos/Tbot Ultra.app"
sleep 10
for _ in $(seq 1 45); do
  "$TBOT" summary 2>/dev/null | grep -q '"running":true' && break
  sleep 4
done

echo "UI: $(python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['url'])" "$ROOT/agent/connection.json" 2>/dev/null)"
"$TBOT" summary 2>/dev/null | python3 -c "
import json, sys
status = json.load(sys.stdin)['status']
print('running:', status['running'], '|', status['activity'][:70], '|', status['alert'])"
