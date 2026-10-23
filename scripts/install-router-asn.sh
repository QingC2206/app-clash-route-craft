#!/bin/sh
# Run on the user's Linux/macOS computer, not inside the router's SSH shell.
set -eu
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
if command -v python3 >/dev/null 2>&1; then
    exec python3 "$script_dir/install-router-asn.py" "$@"
fi
echo 'Python 3.10+ is required on this computer.' >&2
exit 1
