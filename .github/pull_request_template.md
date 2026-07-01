<!--
Solo-maintainer PR checklist — treat as a note-to-self. Not every box needs to be
ticked for every change (a doc typo doesn't need release notes), but the list
exists so you don't forget the ones that DO matter for the change at hand.
-->

## Summary

<!-- What changed and why. One or two sentences. -->

## Checklist

- [ ] Tests added or updated (or explicitly N/A)
- [ ] Release notes touched in `docs/release-notes/<next>.md` if user-visible
- [ ] `<Version>` bumped in `Directory.Build.props` if this is a release-prep PR
- [ ] Design tokens changed in both surfaces + crosswalk updated (if UI colors/type)
- [ ] Docs (`docs/`, `CLAUDE.md`, `README.md`) updated if behavior or conventions changed
- [ ] CI is green

## Test plan

<!-- How you verified this locally. Commands, screenshots for UI, etc. -->
