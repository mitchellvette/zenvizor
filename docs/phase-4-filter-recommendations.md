# Phase 4 — Filter Recommendations (deferred from Q7)

**Status:** Recommendation doc. Filters are NOT in Phase 4 scope; they land in the post–Phase-4 UI polish interlude. This document defines what to build then.

**Audience:** UI designer drafting the Per-App / History mockups, and the engineer who'll wire them up.

---

## 1. Guiding principle

ZenVizor's audience includes non-technical end users. Filter labels and toggles MUST read in plain language. Internal field names (`is_user_writable_path`, `signature_status`, `remote_class`) are useful in the data model and the IPC layer but should never appear verbatim in the UI.

Every filter below is written as: **lay label · what it does · underlying field**.

---

## 2. Recommended Per-App view filters

### 2.1 Search box (free text)

- **Label:** `Search apps…` (placeholder text in the box itself)
- **What it does:** Case-insensitive substring match against `image_name` and, if nothing matches, against `image_path`.
- **Underlying field:** `apps.image_name`, `apps.image_path`
- **Recommendation:** Always visible at the top of the Per-App view. Debounced 200ms so it doesn't spam the IPC.

### 2.2 Trust level

- **Label:** `Trust`
- **Options (segmented control or dropdown):**
  - `All apps` *(default)*
  - `Trusted` — signed by a known publisher
  - `Untrusted` — unsigned or invalid signature
  - `Not yet verified` — signature check is still pending
- **Underlying mapping:**
  - Trusted → `signature_status = 'Signed'`
  - Untrusted → `signature_status IN ('Unsigned', 'Invalid')`
  - Not yet verified → `signature_status = 'Unchecked'`
- **Why:** Most users do not distinguish "Unsigned" from "Invalid" — both mean "I can't tell who made this." Collapsing them into "Untrusted" matches the mental model. The "Pending" bucket exists because Phase 1 leaves rows at `Unchecked` until enrichment runs.
- **Recommendation:** Primary filter — most clicked. Default `All apps`. Inline tooltip on the column header explaining each bucket (e.g., "Trusted means the app is signed by a known publisher and Windows accepts the signature").

### 2.3 Install location

- **Label:** `Install location`
- **Options:**
  - `Anywhere` *(default)*
  - `System folders only` — Program Files, Windows, System32, etc.
  - `Personal folders` — Downloads, Desktop, AppData, Temp — anywhere the user can drop a file without admin rights
- **Underlying mapping:**
  - System folders only → `is_user_writable_path = 0`
  - Personal folders → `is_user_writable_path = 1`
- **Why:** The technical concept "user-writable path" is the heuristic for "could a non-admin have planted this file." That distinction matters (a signed Microsoft binary in `%TEMP%` is suspicious in a way it wouldn't be in `System32`), but the term is jargon. "System folders" vs "personal folders" is the user-facing mental model.
- **Recommendation:** Pairs well with Trust filter. The most useful combined query is `Untrusted + Personal folders` — that's the "unsigned thing from a temp folder" check that motivated the heuristic in the first place.

### 2.4 Connection type

- **Label:** `Connections`
- **Options:**
  - `Any connections` *(default)*
  - `Internet only` — apps that talked to addresses outside the LAN
  - `Local network only` — apps that only talked to devices on the LAN
- **Underlying mapping:**
  - Internet only → app has at least one row with `remote_class = 'Wan'`
  - Local network only → all rows have `remote_class = 'Local'`
- **Why:** A printer driver chatting on the LAN is normal; the same image suddenly talking to the Internet is a story. This filter lets that story surface fast.
- **Recommendation:** Secondary filter; deprioritize visually relative to Trust and Install location.

### 2.5 Window (already in scope for Phase 4)

This is the time-window picker from Q9 (Last 1h / 24h / 7d / 30d / 90d / Custom). Not a "filter" in the same sense as the above, but the natural anchor at the top of the page.

---

## 3. Suggested layout (Per-App page)

```
+----------------------------------------------------------------+
|  [ Window: Last 24h ▾ ]   [ Search apps… ]                     |
|                                                                |
|  Trust:           ( All )( Trusted )( Untrusted )( Pending )   |
|  Install location:( Anywhere )( System )( Personal )           |
|  Connections:     ( Any )( Internet )( Local network )         |
+----------------------------------------------------------------+
|  App ▾  | Publisher | Trust | Source | Up | Down | Last seen   |
|  ...                                                           |
+----------------------------------------------------------------+
```

Segmented controls for the three trust/location/connections filters keep state visible without dropdown clicks. The Window dropdown is the only "hidden state" control.

---

## 4. Filters explicitly NOT recommended for the MVP

- **Filter by remote IP / domain.** Until passive DNS lands (post-MVP per PRD §10), remote addresses are raw IPs — useful for debugging but not for end users. Defer until `connections.resolved_host` is populated.
- **Filter by protocol (TCP vs UDP).** Most users don't think about transport protocol; surface protocol in the App detail / connections view, not as a Per-App filter.
- **"Show only background services."** Hard to define crisply; would need a heuristic on `hosted_services` plus session lifetime that we don't have signal for yet.
- **Filter by hosted service name.** Useful for power users (e.g., "all traffic from `Dnscache`") but the right home for it is the App detail view of `svchost.exe`, not the Per-App list.

---

## 5. IPC surface changes (when this lands)

The Phase 4 `GetAppList(window)` method takes only a window today. When filters ship, extend the request DTO additively:

```csharp
public sealed record AppListRequest(
    QueryWindow Window,
    string? SearchText = null,
    TrustFilter Trust = TrustFilter.All,
    InstallLocationFilter Location = InstallLocationFilter.Anywhere,
    ConnectionTypeFilter Connections = ConnectionTypeFilter.Any);

public enum TrustFilter { All, Trusted, Untrusted, Pending }
public enum InstallLocationFilter { Anywhere, SystemOnly, PersonalOnly }
public enum ConnectionTypeFilter { Any, InternetOnly, LocalOnly }
```

Adding optional fields to a record keeps `IpcEnvelope<AppListResult>.SchemaVersion` at 1 (additive change). Older clients calling the bare `GetAppListAsync(QueryWindow)` continue to work — defaults map to "no filtering."

---

## 6. When this lands

Per the user's "UI polish between Phase 4 and Phase 5" decision: filter design and implementation belong in that interlude, not in Phase 4 proper. Phase 4 keeps the IPC surface tight (`GetAppList(window)` only) so when filters arrive they're a clean additive extension rather than a refactor.
