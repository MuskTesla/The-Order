# The Order · Phase 7 design package (Claude) — revision 2

Revision 2 replaces revision 1 after the Product Owner's review on 2026-10-08. Revision 1 is kept in `rev1/` and
`previews/rev1-*.html` (git tag `claude-design-rev1`); none of it is an active requirement.

| Doc | Content |
|---|---|
| [01_CREATIVE_DIRECTION.md](01_CREATIVE_DIRECTION.md) | Causes of the three mismatches, revised direction, decisions awaiting the Product Owner |
| [02_UI_LAYOUT_SPECIFICATION.md](02_UI_LAYOUT_SPECIFICATION.md) | Fluent shell, Library header/grid/list, click-to-Details and selection mode, return path, Details, other pages |
| [03_MATERIAL_AND_DESIGN_SYSTEM.md](03_MATERIAL_AND_DESIGN_SYSTEM.md) | Tokens → Fluent resources, Windows accent, Liquid Glass variants and placement, quality modes, accessibility |
| [04_IMPLEMENTATION_AND_REVIEW_HANDOFF.md](04_IMPLEMENTATION_AND_REVIEW_HANDOFF.md) | Identities, slice plan (gated), acceptance, dependencies, GPT review prompts |
| [05_GLASS_ENGINEERING_REPORT.md](05_GLASS_ENGINEERING_REPORT.md) | 连续玻璃 / 完全清透: change, verification, attempt history, integration |

## Previews

- **Review page:** `../../../.artifacts/claude-design/review.html`. It contains WinUI runtime material boards and
  accent captures, plus concept mockups. Every item is labelled as mockup or runtime.
- **Live mockups:** `previews/direction-r2.html?view=grid|list|details|flow|accent|modes|settings&theme=light|dark&accent=blue|purple|orange`.
  Accent colours in mockups are labelled examples; the product reads Windows theme resources.
- `previews/render.ps1` re-renders PNGs with headless Edge using a throwaway profile.
