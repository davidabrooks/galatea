# AGENTS.md: how coding agents work in this repo

These rules apply to every agent (and human) changing code here. They are adapted from
[Ponytail](https://github.com/DietrichGebert/ponytail) by Dietrich Gebert (MIT License), the "lazy senior dev"
guideline. Only the ideas are adopted; the plugin is not vendored.

## Write less
- **YAGNI.** Build what the task asks for, nothing speculative. No unrequested abstractions, options or layers.
- **Reuse what exists** in this repo first, then the standard library, the platform, and dependencies already installed.
  Add a new dependency only when none of those can do it, and say why in the PR.
- **Shortest correct diff.** Don't reformat or refactor code you weren't asked to touch.
- **Fix root causes**, not symptoms.

## Never lazy about
- **Validation at trust boundaries:** anything from the network, SL, other residents, job inputs or files that turns into
  paths, URLs, commands or queries.
- **Error handling where data can be lost** (logs, inventory, offers, persisted state).
- **Security:** secrets live in `/home/box/.secrets` and are loaded into the environment only inside commands; never
  printed, logged or committed.

## Leave a trail
- Non-trivial logic gets **one small runnable check** (a script or test that runs with one command).
- Mark a deliberate corner-cut with a `ponytail:` comment that names its ceiling, i.e. when it stops being good enough:
  `# ponytail: 512 px texture cap; ceiling = blurry close-ups`.

## Project rules that come with these
- Galatea's live account and her text client are not touched by development work (see docs/unreal-viewer/DESIGN.md §8).
- Second Life viewer code (Linden Lab, Firestorm) is LGPL-2.1: read it to understand behavior, reimplement in our own
  words, never copy it into this BSD-3 repo.
