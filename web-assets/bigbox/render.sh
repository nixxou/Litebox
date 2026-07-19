#!/bin/bash
# Render a screen to PNG via Edge headless for visual self-check.
# Usage: bash render.sh <screen-name>   (e.g. bash render.sh games-list)
EDGE="/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"
[ -f "$EDGE" ] || EDGE="/c/Program Files/Microsoft/Edge/Application/msedge.exe"
BASE="C:/Users/mehdi/source/repos/scrapper-project/project/ExtendDB/ExtendDB/BigBoxWeb/web"
NAME="$1"
"$EDGE" --headless=new --disable-gpu --hide-scrollbars --window-size=1280,720 \
  --screenshot="$BASE/renders/$NAME.png" "file:///$BASE/screens/$NAME.html" 2>/dev/null
echo "rendered renders/$NAME.png"
