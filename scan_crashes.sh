#!/bin/bash
PATTERN='Fatal|Unhandled|abort|TypeLoad|FatalEngine|Aborted|Crashed|Exception|ERROR|exited|terminated|Segfault'
for f in /home/pi/.local/share/opentap/SessionLogs/Session-*/Latest.txt; do
  hits=$(grep -cE "$PATTERN" "$f" 2>/dev/null)
  if [ "$hits" -gt 0 ]; then
    dir=$(dirname "$f")
    name=$(basename "$dir")
    mtime=$(stat -c %y "$f" | cut -d. -f1)
    echo "=== $name [$hits hits, last=$mtime] ==="
    grep -nE "$PATTERN" "$f" | head -8
    echo ""
  fi
done
