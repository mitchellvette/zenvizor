---
name: zenvizor-design
description: Use this skill to generate well-branded interfaces and assets for ZenVizor, either for production or throwaway prototypes/mocks/etc. Contains essential design guidelines, colors, type, fonts, assets, and UI kit components for prototyping.
user-invocable: true
---

Read the `README.md` file within this skill, and explore the other available files.

ZenVizor is a lightweight, passive **Windows network monitor** (WPF-UI / Fluent). Its brand is a *utopian-future* one — porcelain/steel aerodynamic surfaces with luminous **violet** accents (LSU tiger purple anchored), calm and minimally intrusive, "like flying a quiet spaceship." Light and dark are supported equally.

Key files:
- `README.md` — full brand, content/voice, visual foundations, iconography, and a manifest of everything here. **Start here.**
- `colors_and_type.css` — design tokens (color primitives, light/dark semantic tokens, the Fluent type ramp + helper classes, radii, spacing, motion). Import this first in any artifact.
- `assets/icons/` — vendored Microsoft Fluent System Icons (SVG, normalized to `currentColor`); `assets/zv-icon.js` inlines them by `data-ic="name"`. `assets/zenvizor-mark-on{light,dark}.svg` — the visor logo.
- `preview/` — specimen cards (colors, type, spacing, components, icons).
- `ui_kits/zenvizor-app/` — a faithful, interactive recreation of the desktop app; reuse its `WindowFrame`, cards, tables, chips, charts.

If creating visual artifacts (slides, mocks, throwaway prototypes, etc), copy assets out and create static HTML files for the user to view. If working on production code, copy assets and read the rules here to become an expert in designing with this brand.

If the user invokes this skill without any other guidance, ask them what they want to build or design, ask some questions, and act as an expert designer who outputs HTML artifacts _or_ production code, depending on the need.
