# Licensing — WiX Toolset Open Source Maintenance Fee (OSMF)

**Status:** Findings for review.
**Discovered:** Phase 7 implementation, 2026-06-18.
**Affects:** every WiX 6.0.1 package the build depends on
(SDK, Util.wixext, Bal.wixext, NetFx.wixext).
**Context:** ZenVizor is planned for **free distribution + a donateware
path**, so the OSMF's "revenue-generating use" carve-out applies to us
non-trivially. This is not a hypothetical line in a license — it has a
concrete impact on what we owe and to whom.

This doc records the findings honestly so the call can be made
deliberately. It is **not legal advice**. Where the agreement text leaves
room for interpretation, this doc says so rather than picking one.

---

## 1. What changed vs. the brief's assumption

The Phase 7 sprint-plan brief (and the original Phase 6.8a installer
landing) said:

> "WiX 6.0.1 is the last MIT-licensed release. v7+ requires the OSMF
> EULA; do not upgrade past 6.x without revisiting the licensing
> posture of the project."

That framing was **incorrect**. The actual posture, verified by
inspecting each package's `.nuspec` and shipped `OSMFEULA.txt`:

| Package                            | Version | License (binary) | EULA file shipped       |
| ---------------------------------- | ------- | ---------------- | ----------------------- |
| `WixToolset.Sdk`                   | 6.0.1   | OSMF on binary   | `OSMFEULA.txt` present  |
| `WixToolset.Util.wixext`           | 6.0.1   | OSMF on binary   | `OSMFEULA.txt` present  |
| `WixToolset.Bal.wixext`            | 6.0.1   | OSMF on binary   | `OSMFEULA.txt` present  |
| `WixToolset.NetFx.wixext`          | 6.0.1   | OSMF on binary   | `OSMFEULA.txt` present  |

All four `.nuspec` files declare:
`<license type="file">OSMFEULA.txt</license>` and
`<requireLicenseAcceptance>true</requireLicenseAcceptance>`.

The **MIT-only** line for WiX was **5.x**, not 6.x. The OSMF model was
introduced with the 6.0 binary releases. Upgrading from 6.0.1 to 7+
does not change the licensing posture — the OSMF is already in effect
today. Pinning to 6.0.1 was always going to encounter this; we just
hadn't surfaced it.

The Phase 6.8a installer that landed end of 2026-06-17 — the MSI built
via `installer/ZenVizor.wixproj` — is built with `WixToolset.Sdk 6.0.1`
and `WixToolset.Util.wixext 6.0.1`. Both are OSMF-on-binary. So the
OSMF question is **not new with Phase 7**; Phase 7 just made it
unavoidable to notice (the Bal package's OSMFEULA was the prompt).

---

## 2. The agreement text, verbatim

This is the full text shipped in `OSMFEULA.txt` inside every package
listed above. Read it directly — the rest of this doc paraphrases.

```
End User License Agreement

This Open Source Maintenance Fee Agreement ("Agreement") is a legal agreement
between you ("User") and WiX Toolset ("Project") for the use of the
WiX Toolset ("Software"), an open source software project licensed under
the Microsoft Reciprocal License ("OSI License"), an OSI-approved open source license.
Project offers a Binary Release of the Software to Users in exchange for a
maintenance fee ("Fee"). "Binary Release" refers to pre-compiled executable
versions of the Software provided by Project. By accessing or using the
Binary Release, User agrees to be bound by the terms of this Agreement.

1. Applicability

Project agrees to provide User with the Binary Release in exchange for the
Fees outlined in Section 2, subject to the terms of this Agreement. The Fee
applies only to Users that generate revenue by the Software.
Non-revenue-generating use of the Software is exempt from this Fee. In
addition, Users who pay separate support and/or maintenance fees to the
maintainers of the Software are exempt from the Fee outlined in this
Agreement. This distinction ensures that duplicate fees are not imposed,
promoting fairness and consistency while respecting alternative support
arrangements.

2. Monthly Fee and Payment Terms

Revenue-generating Users required to pay the Fee shall follow the payment
terms set forth by the Project. Failure to comply with these terms may result
in suspending access to the Binary Release. However, this does not restrict
the User from obtaining or redistributing binaries from other sources or
self-compiling them.

3. Nature of the Fee

The Fee is not a license fee. The Software's source code is licensed to User
under the OSI License and remains freely distributable under the terms of the
OSI License and any applicable open-source licenses.

4. Conflicts with OSI License

To the extent any term of this Agreement conflicts with User's rights
under the OSI License regarding the Software, the OSI License shall govern.
This Agreement applies only to the Binary Release and does not limit User's
ability to access, modify, or distribute the Software's source code or
self-compiled binaries. User may independently compile binaries from the
Software's source code without this Agreement, subject to OSI License terms.
User may redistribute the Binary Release received under this Agreement,
provided such redistribution complies with the OSI License (e.g., including
copyright and permission notices). This Agreement imposes no additional
restrictions on such rights.

5. Disclaimer of Warranty and Limitation of Liability
[boilerplate — omitted here]
```

---

## 3. What the agreement does and does not say

**The Fee is on the binary distribution, not on the source code.**
The WiX *source* (Microsoft Reciprocal License — OSI-approved) is
freely distributable forever. Section 4 explicitly carves out self-
compilation: "User may independently compile binaries from the
Software's source code without this Agreement." This is a real escape
hatch — see §5 Option C below.

**Exemption triggers.** Per Section 1, the Fee does **not** apply to:

1. **Non-revenue-generating users.** "Non-revenue-generating use of the
   Software is exempt from this Fee."
2. **Users paying separate support/maintenance fees** to WiX
   maintainers ("to avoid duplicate fees").

**What "revenue-generating by the Software" means is not defined in the
agreement.** This is the load-bearing ambiguity. See §4.

**The fee amount is not stated in the EULA.** Section 2 refers to
"payment terms set forth by the Project" — i.e., the GitHub Sponsors
tier structure on the WiX repo. The `opensourcemaintenancefee.org`
consumer guidance describes a typical threshold of "minimum annual
revenue (typically US$10,000)" below which payment is not expected;
this is framework-wide guidance, not WiX-project-specific. The current
specific WiX sponsor tiers should be confirmed against the WiX GitHub
Sponsors page at the time of review.

**Failure to pay does not lose the right to redistribute** — Section 2
last sentence: "this does not restrict the User from obtaining or
redistributing binaries from other sources or self-compiling them."
The penalty is loss of access to *future* binary releases from
Project, not loss of right to use what you already have.

---

## 4. The donateware question

This is the one that matters for ZenVizor specifically. The
agreement applies to "Users that generate revenue **by** the Software."
ZenVizor's planned distribution model is **free** software with an
**optional donateware path** (voluntary contributions from users who
want to support the project, no paywalled features, no required
payment). Donations are revenue *for the project*, but it is not
obviously settled that they are revenue *"by the Software"* in the
sense the agreement uses.

Three honest readings:

**(a) Narrow.** "Revenue by the Software" means commercial sale of, or
licensed access to, the software. Donateware doesn't qualify — the
software is free, the donations are voluntary, no exchange of value
specific to the software's use. Under this reading, ZenVizor is exempt
regardless of donation totals.

**(b) Broad.** Any inflow of money the project would not receive
without the existence of the software is "revenue by the software." A
"donate to support ZenVizor" button on the project page is revenue
*by* ZenVizor. Under this reading, ZenVizor is revenue-generating from
the first dollar; only the $10K threshold (if applicable) provides
relief.

**(c) Threshold-based.** Whatever the legal reading, in practice the
OSMF framework targets organizations with material commercial activity
(the $10K threshold cited on `opensourcemaintenancefee.org/consumers/`).
Donateware projects raising trivial sums fall outside the practical
enforcement scope.

None of the three is obviously correct from the text alone. None of
the three is obviously *wrong* either. The fact that the framework
doesn't address donateware explicitly is itself a data point — it
suggests the model was designed with commercial vendors in mind, and
donateware is a gap the maintainers haven't ruled on publicly.

If certainty is required, the only ways to get it are:

- Ask WiX maintainers in writing (their GitHub Discussions /
  issues are the documented channel; or a direct sponsor inquiry).
- Pay the OSMF voluntarily, removing the question.
- Avoid the binary releases entirely (self-compile — see Option C).

This doc takes **no position** on which reading governs. That call
belongs to whoever signs off on ZenVizor's distribution.

---

## 5. Options for resolving

Each option is concrete and reversible. Pick deliberately; document the
choice (and the reason) in `CLAUDE.md` so it doesn't drift.

### Option A — Continue with WiX 6.0.1 binaries, treat ZenVizor as exempt while non-revenue-generating

**Posture.** Today, ZenVizor generates zero revenue. Section 1 exempts
"non-revenue-generating use" cleanly. While that holds, no fee is owed
and no further action is required.

**Trigger to revisit.** The donateware path going live. At that point
the §4 question above must be resolved. The cleanest trigger is "first
donation received" — at which point pause and either commit to one of
the readings, ask WiX maintainers, or switch options.

**Effort.** None today. Decision deferred to the donateware launch.

**Risk.** If a future audit (or WiX maintainer's public clarification)
adopts reading (b), the project owes back fees from the donateware
launch date forward. Quantifiable only after the fee tier is locked.

**Documentation cost.** Update `Directory.Packages.props` and
`docs/zenvizor-sprint-plan.md` with the deferred-decision note —
already done as part of the Phase 7 closeout commit.

This is the default if no other option is picked.

### Option B — Pay the OSMF voluntarily

**Posture.** Sponsor the WiX project at the published tier. Removes
the ambiguity entirely; supports a tool the project depends on.

**Effort.** Set up GitHub Sponsors funding for the project entity (not
the developer's personal account, unless that's the chosen sponsoring
identity). Recurring monthly cost at the published tier rate (confirm
against current WiX GitHub Sponsors page).

**Risk.** Recurring expense. If ZenVizor itself never generates revenue
sufficient to cover the sponsor fee, the project subsidises WiX out of
the maintainer's pocket.

**When this is the right call.** Donateware income is reliably above
the sponsor fee + a margin; or the maintainer specifically wants to
support WiX regardless.

### Option C — Self-compile WiX from source

**Posture.** Section 4 of the OSMFEULA explicitly carves out
self-compilation: "User may independently compile binaries from the
Software's source code without this Agreement." If we build the WiX
toolchain locally (and on CI) from the MS-RL-licensed source, the
OSMF doesn't apply to us at all — we never consume a Binary Release.

**Effort.** Material. The WiX source repository
(`github.com/wixtoolset/wix`) is not trivially built — it produces
the SDK, several extensions (Util, Bal, NetFx, etc.), and the CLI.
Add a build-step pipeline (one-time setup, recurring CI cost). Pin a
known-good WiX source SHA; don't track HEAD.

**Risk.** Build-system divergence over time (WiX's own build needs
maintenance; their source structure may change). Slower CI (compiling
WiX from source every clean build adds minutes). Build environment
complexity (WiX requires a specific .NET SDK + MSBuild surface).
Probably not worth the friction unless OSMF cost vs. donateware
revenue tips the math.

**When this is the right call.** Only if the donateware path goes
big *and* the OSMF interpretation lands on reading (b), *and* the
maintainer wants to keep the project entirely free of upstream fees
on principle. Premature otherwise.

### Option D — Switch installer technology entirely

**Posture.** Replace WiX with a permissively-licensed installer
framework. Candidates:

- **Inno Setup** (Modified BSD-like): mature, scriptable, no .msi
  output (uses its own format). Would require rewriting Phase 6.8a +
  Phase 7 installer surfaces.
- **NSIS** (zlib license): similar profile, different scripting
  surface. Same rewrite cost.
- **Roll-our-own MSI via the Windows Installer XML SDK + raw MSI
  tooling**: massive effort, not realistic.

**Effort.** Multi-week. Throws away Phase 6.8a + Phase 7 work and the
WiX-specific assumptions baked into the installer design (ServiceInstall
elements, util:PermissionEx ACLs, Burn bootstrapper for runtime
chaining, etc.). Inno Setup specifically does NOT produce .msi — that's
a meaningful regression for enterprise admins who use SCCM/Intune (they
expect .msi). NSIS has similar limitations.

**Risk.** Loss of MSI ecosystem benefits (Group Policy deployment,
Add/Remove Programs integration, repair semantics). Significant rework
risk just to avoid the OSMF question.

**When this is the right call.** Only if (a) the OSMF interpretation
lands on reading (b), (b) self-compile is rejected, and (c) the
project's distribution audience does NOT need MSI format. Unlikely
combination.

---

## 6. Recommendation

**Option A (continue, defer decision to donateware launch).**

Rationale:

- The OSMF is not unique to Phase 7; Phase 6.8a was already in this
  posture. We didn't notice because Util.wixext is less visible than
  Bal.wixext.
- ZenVizor is non-revenue-generating today. The exemption is cleanly
  applicable.
- The donateware-vs-revenue question is real but not load-bearing
  *yet*. Deciding it before the donateware path is even built is
  premature optimisation against an uncertain outcome.
- Options B, C, D have meaningful ongoing costs (fee, CI complexity,
  rework) — none is worth incurring without a concrete trigger.
- Option C (self-compile) is the architectural fallback if needed
  later; it stays available.

The trigger to revisit this doc is: **first donation received on the
donateware path.** At that point — and not before — pick a reading of
§4 above, or move to Option B, or ask WiX maintainers in writing.
Whatever you pick, write the rationale into `CLAUDE.md` so future
contributors don't re-litigate it.

---

## 7. References

- The full OSMFEULA text is shipped in every WiX 6.0.x package under
  `OSMFEULA.txt`. Quoted in §2 above.
- `opensourcemaintenancefee.org/consumers/` — framework-wide consumer
  guidance, including the typical $10K threshold reference.
- `github.com/wixtoolset/wix` — WiX project README documents
  payment-via-GitHub-Sponsors and links to the OSMFEULA. Source code
  is MS-RL-licensed.
- The exemption carve-out for self-compile lives in §4 of the
  OSMFEULA text — verify against the version shipped in the actual
  package you depend on, not against a copy on a third-party site.

The licensing posture itself can shift with WiX version bumps. Re-verify
the `OSMFEULA.txt` text and the `<license>` element of each `.nuspec`
each time the WiX SDK or any wixext is upgraded.
