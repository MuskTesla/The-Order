# The Order · Phase 7 design package (Claude)

| Doc | Content |
|---|---|
| [01_CREATIVE_DIRECTION.md](01_CREATIVE_DIRECTION.md) | Evidence-based audit, three concepts, recommendation (Direction B), decisions awaiting the Product Owner |
| [02_UI_LAYOUT_SPECIFICATION.md](02_UI_LAYOUT_SPECIFICATION.md) | Shell, Library grid/list, inspector, Details, other workflows, responsive and keyboard rules |
| [03_MATERIAL_AND_DESIGN_SYSTEM.md](03_MATERIAL_AND_DESIGN_SYSTEM.md) | Tokens, type, motion, surface roles, lens optics and parameter mapping, quality modes, accessibility |
| [04_IMPLEMENTATION_AND_REVIEW_HANDOFF.md](04_IMPLEMENTATION_AND_REVIEW_HANDOFF.md) | Baseline identity, vertical-slice plan, contracts, acceptance criteria, dependencies, GPT review prompts (Chinese) |
| [05_GLASS_ENGINEERING_REPORT.md](05_GLASS_ENGINEERING_REPORT.md) | Frosted lens body: change, verification, measurements, integration instructions |

## Previews

The previews are concept mockups (browser HTML/SVG), not WinUI.

- Open `previews/concepts.html?c=A|B|C` or
  `previews/direction-b.html?view=grid|list|details|settings|filter|modes&theme=light|dark` directly in a browser.
  Resize the window to see the responsive classes.
- Rendered PNGs and actual WinUI runtime captures are collected in `../../../.artifacts/claude-design/review.html`.
  `.artifacts` is local and not committed.
- `previews/render.ps1` re-renders PNGs with headless Edge using a throwaway profile.

Covers in previews are procedurally generated originals. Runtime captures use the original synthetic Test covers and
the user-authorised local reference image only.
