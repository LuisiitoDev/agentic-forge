---
name: forge-spike
description: Spike. Proves that the Copilot engine runs non-interactively and that its run title can be read by a workflow_run watcher. Remove after the first run.
run-name: "forge-spike ${{ inputs.target }}#${{ inputs.issue }}"
on:
  workflow_dispatch:
    inputs:
      target:
        description: Repository, owner/name
        required: true
        type: string
      issue:
        description: Any number
        required: true
        type: string
permissions:
  contents: read
concurrency:
  job-discriminator: ${{ inputs.target }}#${{ inputs.issue }}
engine: copilot
timeout-minutes: 10
network: defaults
---

Reply with the single word `ok`. Do not call any tool and do not change any file.
