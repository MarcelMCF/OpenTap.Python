#!/bin/bash
cd /home/pi/.local/share/opentap
for i in 1 2 3 4 5; do
  echo "=== Run $i ==="
  ./tap run /tmp/test_rootcause.TapPlan 2>&1 | grep -E 'PythonMem|completed suc'
  echo "---"
done
