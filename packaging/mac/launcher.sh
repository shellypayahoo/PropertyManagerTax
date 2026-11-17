#!/bin/bash
# The Mac app's entry point (Contents/MacOS/Property Manager). Starts the app in the background, which then opens
# the browser, and exits, so double-clicking the icon again later just opens the app in the browser.
DIR="$(cd "$(dirname "$0")" && pwd)"
APP="$DIR/../Resources/app/PropertyManager.Web"
if /usr/bin/curl -fs -o /dev/null --max-time 2 http://localhost:5220/; then
    /usr/bin/open http://localhost:5220
    exit 0
fi
nohup "$APP" "$@" >/dev/null 2>&1 &
exit 0
