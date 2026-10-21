#!/bin/bash
while [ "$#" -gt 0 ] && [ "$1" != '-c' ]; do shift; done
shift
if [ "${HP3052_TEST_DETACH:-}" = "1" ] && [[ "$1" == *' & job=$!;'* ]]; then
  exec python3 "$(dirname "$0")/wsl-fixture.py" "$1"
fi
exec /bin/bash -c "$1"
