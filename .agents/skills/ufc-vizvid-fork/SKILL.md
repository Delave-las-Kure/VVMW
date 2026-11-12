---
name: ufc-vizvid-fork
description: >-
  Work with the maintained Delave-las-Kure/VVMW fork for UFC Cinema: choose the
  production worktree and branch, maintain package fixes, and distinguish local
  edits, commits, pushes and VPM releases. Use for this fork's source, branch,
  integration and release workflow.
---

# UFC VizVid fork

## Primary branch

The owner clarified on 12 Nov 2026 that this fork's primary production branch
is **`ufc/cinema-stable`**. The owner's «мейн», «main нашего форка» and
«основная ветка» mean `ufc/cinema-stable`, unless a later request explicitly
chooses another branch. The literal Git branch `main` is not the default target.

The repository is `https://github.com/Delave-las-Kure/VVMW`.
Its local worktrees share the repository at `D:/unityhub/worlds/VVMW Source`.
Before editing, run `git worktree list` and check the actual branch. Use the
worktree holding `ufc/cinema-stable`; at the time of this clarification it is
`D:/unityhub/worlds/VVMW Playback Speed`. Preserve unrelated working changes.

Package versions such as `1.8.3-cinema.6` are version labels, not branch names.
`ufc/video-mirrors` and `ufc/playback-speed-binding` are feature/candidate
branches. Integrate qualified production fixes into `ufc/cinema-stable` within
the user's authorized scope. Do not silently include an experimental candidate.

## Source and delivery

Maintain VizVid changes in this fork. For Cinema, install a verified package
candidate/release while preserving package ID, asset GUIDs and the qualified
clock source; do not use edits to an installed package as the maintained source.

Read the repository's `AGENTS.md`, `CONTRIBUTING.md` and `.editorconfig` before
repository mutations. Keep their approval and formatting rules applicable.
After an authorized push, verify the remote `ufc/cinema-stable` hash. Inspect
the fetch refspec when cached remote-tracking refs may be stale.

Report local edits, saved commits, remote branch updates, installed package
versions and published VPM archives separately. A Git push does not itself mean
a VPM release or a VRChat world upload.
