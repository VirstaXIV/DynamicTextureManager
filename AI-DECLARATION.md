---
version: "0.1.2"
level: copilot
processes:
  design: copilot
  implementation: copilot
  testing: pair
  documentation: copilot
  review: pair
  deployment: copilot
---

This format is based on [AI-DECLARATION.md](https://ai-declaration.md/en/0.1.2).

## Notes

- The bulk of the plugin is implemented by Claude Code sessions directed by the
  maintainer; commits carry `Co-Authored-By: Claude` trailers.
- The maintainer decides features and scope, verifies every change in game before
  it ships, and authorizes all releases.
- The initial project scaffolding (early 2026) is human-written.
- Submodules (Luna, Penumbra.Api, Penumbra.GameData, Penumbra.String) are
  third-party, human-written projects and not covered by this declaration.
