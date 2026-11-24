#!/usr/bin/env bash
# Runs every orchestrator test. Usage: .github/forge/tests/run.sh
set -euo pipefail
d="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bash "$d/parse.test.sh"
bash "$d/flow.test.sh"
bash "$d/await.test.sh"
bash "$d/intake.test.sh"
bash "$d/preflight.test.sh"
bash "$d/workflow.test.sh"
python3 -W ignore "$d/test_claude_scripts.py"
python3 -W ignore "$d/test_deliver.py"
