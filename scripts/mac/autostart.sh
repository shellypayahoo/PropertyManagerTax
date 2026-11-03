#!/bin/bash
# Starts Property Manager by itself whenever you log in to this Mac, restarts it if it stops,
# and keeps the Mac awake (on power) while it runs, so your phone can reach it over Tailscale.
#
#   scripts/mac/autostart.sh install     set it up and start it now
#   scripts/mac/autostart.sh restart     restart it (do this after a git pull)
#   scripts/mac/autostart.sh stop        stop it until the next login (or 'start')
#   scripts/mac/autostart.sh start       start it again
#   scripts/mac/autostart.sh status      is it running, and where to open it
#   scripts/mac/autostart.sh logs        show the latest log lines
#   scripts/mac/autostart.sh uninstall   remove it; the app then only runs when you start it yourself
set -euo pipefail

LABEL="com.propertymanager.app"
PORT="${PORT:-5220}"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
LOGDIR="$HOME/Library/Logs/PropertyManager"
LOG="$LOGDIR/app.log"
DOMAIN="gui/$(id -u)"

if [[ "$(uname)" != "Darwin" ]]; then
    echo "This script is for macOS." >&2
    exit 1
fi

is_loaded() { launchctl print "$DOMAIN/$LABEL" >/dev/null 2>&1; }

install() {
    local dotnet
    dotnet="$(command -v dotnet || true)"
    if [[ -z "$dotnet" ]]; then
        for candidate in /usr/local/share/dotnet/dotnet /opt/homebrew/bin/dotnet /usr/local/bin/dotnet; do
            [[ -x "$candidate" ]] && dotnet="$candidate" && break
        done
    fi
    if [[ -z "$dotnet" ]]; then
        echo "Couldn't find dotnet. Install the .NET SDK first (see the README)." >&2
        exit 1
    fi
    # The real file, not a symlink: that's what Full Disk Access has to be given to.
    while true; do
        dotnet="$(cd "$(dirname "$dotnet")" && pwd -P)/$(basename "$dotnet")"
        [[ -L "$dotnet" ]] || break
        local target
        target="$(readlink "$dotnet")"
        [[ "$target" = /* ]] || target="$(dirname "$dotnet")/$target"
        dotnet="$target"
    done

    if lsof -ti "tcp:$PORT" >/dev/null 2>&1 && ! is_loaded; then
        echo "Something is already using port $PORT, probably the app started from Terminal."
        echo "Stop it first (Control-C in that Terminal window, or: lsof -ti tcp:$PORT | xargs kill), then run this again."
        exit 1
    fi

    mkdir -p "$LOGDIR" "$(dirname "$PLIST")"
    # caffeinate -s keeps the Mac from sleeping while on power and the app is running.
    # The "http" launch profile keeps the Development environment, which is what loads your API keys (user secrets).
    cat > "$PLIST" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$LABEL</string>
    <key>ProgramArguments</key>
    <array>
        <string>/usr/bin/caffeinate</string>
        <string>-s</string>
        <string>$dotnet</string>
        <string>run</string>
        <string>--project</string>
        <string>src/PropertyManager.Web</string>
        <string>--launch-profile</string>
        <string>http</string>
        <string>--urls</string>
        <string>http://0.0.0.0:$PORT</string>
    </array>
    <key>WorkingDirectory</key>
    <string>$REPO</string>
    <key>EnvironmentVariables</key>
    <dict>
        <key>HOME</key>
        <string>$HOME</string>
        <key>PATH</key>
        <string>$(dirname "$dotnet"):/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin</string>
        <key>DOTNET_CLI_TELEMETRY_OPTOUT</key>
        <string>1</string>
    </dict>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <dict>
        <key>SuccessfulExit</key>
        <false/>
    </dict>
    <key>ThrottleInterval</key>
    <integer>30</integer>
    <key>StandardOutPath</key>
    <string>$LOG</string>
    <key>StandardErrorPath</key>
    <string>$LOG</string>
</dict>
</plist>
PLIST

    if is_loaded; then
        launchctl bootout "$DOMAIN/$LABEL" || true
    fi
    launchctl bootstrap "$DOMAIN" "$PLIST"
    echo "Installed. Property Manager now starts by itself when you log in."
    echo
    echo "One more thing for the Messages page: give Full Disk Access to this file"
    echo "  $dotnet"
    echo "(System Settings → Privacy & Security → Full Disk Access → + , then press Command-Shift-G and paste the path),"
    echo "then run: $0 restart"
    echo
    wait_and_report
}

wait_and_report() {
    echo "Starting (the first start after an update builds the app and can take a minute)…"
    for _ in $(seq 1 90); do
        if curl -fsS -o /dev/null "http://localhost:$PORT/"; then
            status
            return
        fi
        sleep 2
    done
    echo "It hasn't answered yet. See what it's doing with: $0 logs"
}

status() {
    if ! is_loaded; then
        echo "Not set up to start automatically (run: $0 install)."
    elif curl -fsS -o /dev/null "http://localhost:$PORT/"; then
        echo "Running. On this Mac: http://localhost:$PORT"
        local name
        name="$(/Applications/Tailscale.app/Contents/MacOS/Tailscale status --self --json 2>/dev/null \
            | /usr/bin/python3 -c 'import json,sys; print(json.load(sys.stdin)["Self"]["DNSName"].split(".")[0])' 2>/dev/null || true)"
        if [[ -n "$name" ]]; then
            echo "From your phone (Tailscale on): http://$name:$PORT"
        else
            echo "From your phone (Tailscale on): http://<this Mac's Tailscale name>:$PORT"
        fi
    else
        echo "Set up, but not answering right now (starting, or stopped). Logs: $0 logs"
    fi
}

case "${1:-}" in
    install) install ;;
    uninstall)
        launchctl bootout "$DOMAIN/$LABEL" 2>/dev/null || true
        rm -f "$PLIST"
        echo "Removed. Start the app yourself from Terminal when you want it."
        ;;
    restart)
        launchctl kickstart -k "$DOMAIN/$LABEL"
        wait_and_report
        ;;
    stop)
        launchctl bootout "$DOMAIN/$LABEL" 2>/dev/null || true
        echo "Stopped. It starts again at your next login, or now with: $0 start"
        ;;
    start)
        is_loaded || launchctl bootstrap "$DOMAIN" "$PLIST"
        wait_and_report
        ;;
    status) status ;;
    logs) tail -n "${2:-60}" "$LOG" ;;
    *)
        sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'
        exit 1
        ;;
esac
