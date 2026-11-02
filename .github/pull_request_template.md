## What and why

<!-- What this changes, and the problem it solves. Link the issue if there is one. -->

## How it was tested

<!-- The commands you ran and their result lines (CONTRIBUTING.md lists what CI runs). -->

## Checklist

- [ ] CI is green
- [ ] Reaches installed users? `version` in `plugin/.claude-plugin/plugin.json` is bumped
- [ ] User-visible? `CHANGELOG.md` says what changed
- [ ] Changes a tool's name, description or schema? `ImportVisibilityTests` is repinned and this
      description says so
- [ ] Touches `setup.sh` or `setup.ps1`? The twin has the same change
- [ ] Nothing private: no client names, private hosts, tokens, live data or private paths
