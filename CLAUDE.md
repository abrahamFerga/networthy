# CLAUDE.md

@AGENTS.md

Everything above applies. Claude Code does not read `AGENTS.md` natively, so it is imported here —
the import is used rather than a symlink because symlinks need Administrator or Developer Mode on
Windows, and this repo is developed there.

## Claude-specific

- **Use the `run-networthy` skill** in `.claude/skills/run-networthy/` to launch the stack. It
  encodes this repo's launch quirks; reaching for a raw `dotnet run` skips them.
- **Plugins are declared, not vendored.** `.claude/settings.json` and `workflow.json` must agree.
  A marketplace is keyed by the `name` field in its own `.claude-plugin/marketplace.json` — **not**
  by its `owner/repo` slug. Getting that wrong resolves zero plugins, silently: every skill and
  agent simply goes missing, and a scheduled run reports the gap only if it thinks to look.
- **The Plenipo harness is the toolchain here.** Six plugins from the `plenipo-agents` marketplace
  are enabled, and `/plenipo:*` is the front door — `setup`, `launch`, `deliver`, `ship`, `test`,
  `define`, `fleet`, each one bounded tick with a named terminal state. Read `plenipo-platform`
  before writing module code so you extend the platform instead of rebuilding a weaker copy of it,
  `plenipo-module-sdk` while declaring a tool or tab, and `loop-discipline` before claiming
  something is done.
- **Two agents can be delegated to** for work that starts in the running app rather than the source:
  `e2e-tester` (read and run only, never edits) sweeps the product and reports reproductions;
  `product-improver` uses it as a household member would and ships **one** improvement per run as a
  PR. Both boot the app and stop `Blocked` if they cannot — that is deliberate.
- **`.github/workflows/product-*.md`** are GitHub Agentic Workflow definitions with compiled
  `.lock.yml` siblings. Editing the markdown without recompiling the lock file changes nothing that
  runs.
