# CLAUDE.md

@AGENTS.md

Everything above applies. Claude Code does not read `AGENTS.md` natively, so it is imported here —
the import is used rather than a symlink because symlinks need Administrator or Developer Mode on
Windows, and this repo is developed there.

## Claude-specific

- **Use the `run-networthy` skill** in `.claude/skills/run-networthy/` to launch the stack. It
  encodes this repo's launch quirks; reaching for a raw `dotnet run` skips them.
- **Plugins are declared, not vendored.** `.claude/settings.json` is generated from `workflow.json` —
  edit `workflow.json` and re-sync, never hand-edit `settings.json` alone, or `validate-system`
  reports drift and the next sync reverts you. The `enabledPlugins` values for the four `my-skills`
  plugins are derived from the `stage` field.
- **The Plenipo harness is available here.** `harness` and `deliver` from the `plenipo-agents`
  marketplace are enabled: read `plenipo-platform` before writing module code so you extend the
  platform instead of rebuilding a weaker copy of it, `plenipo-module-sdk` while declaring a tool or
  tab, and `loop-discipline` before claiming something is done.
- **Two agents can be delegated to** for work that starts in the running app rather than the source:
  `e2e-tester` (read and run only, never edits) sweeps the product and reports reproductions;
  `product-improver` uses it as a household member would and ships **one** improvement per run as a
  PR. Both boot the app and stop `Blocked` if they cannot — that is deliberate.
- **`.github/workflows/product-*.md`** are GitHub Agentic Workflow definitions with compiled
  `.lock.yml` siblings. Editing the markdown without recompiling the lock file changes nothing that
  runs.
