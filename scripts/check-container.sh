#!/usr/bin/env bash
# Backwards-compatible filesystem-only gate; see check-blob-storage.py for both profiles.
set -euo pipefail
image=${1:?usage: bash scripts/check-container.sh IMAGE}
exec python3 "$(dirname "$0")/check-blob-storage.py" "$image" --profile filesystem
