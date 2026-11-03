# UI/UX guide

## Who uses the platform

The platform is multi-tenant and expandable. It ships with five built-in account types (Zr3i admin, Bank, Insurance, Company, Farmer; see Account types at the end of this guide). Each type is a preset on one engine: an organisation is a tenant, people join it through memberships, and what they see is decided by their roles, their scope and the modules their tenant has switched on. A new type is a new preset, not a new code path.

| Concept | Rule for screens |
| --- | --- |
| Organisation | The tenant. Name and optional logo in the header; the module list sets the navigation. |
| Membership and role | A user can belong to several organisations; a switcher in the header changes the organisation, never mixes data. Navigation lists only what the roles allow. Nothing is greyed out for lack of permission. |
| Scope | A membership can be limited to portfolios or farms. Scoped users see only those, and counts reflect only those. |
| Grant | One organisation lets another read defined data for a purpose and a period. Every screen that shows granted data names the grantor and the expiry. |
| Module | Verification (R0) and Risk (CRI, FRI) today. A module adds navigation items, screens and report templates. |

Built-in role templates (copyable per tenant): organisation admin, analyst, viewer, field user. Zr3i staff are the provider organisation: Zr3i admin and Zr3i analyst. Farmers have accounts, either direct or managed by a company or authority such as AgriCash, and can also give consent through a one-page link from their agronomist.

## Entering the platform

1. **Sign in**: email and password, then a six-digit one-time code (`AuthCard`, `Field`). Five failed attempts lock the account for 15 minutes and email the user.
2. **Request access**: for a new account of any type except Zr3i staff (invitation only). The first choice is the account type: Company, Bank, Insurance or Farmer. Fields: organisation or farm name, commercial register number (mobile number for a farmer), work email, contact name, purpose in one sentence, and for a farmer the company or authority that invited them (optional). The confirmation says exactly what happens next: a Zr3i administrator reviews it, usually within two working days, and sends an invitation.
3. **Accept invitation**: set a password, enrol an authenticator app, choose the language. The chosen language is kept for the account and the reports it generates.
4. **Farmers** can create a direct account or be invited by a company or authority. A farmer who has no account can still open a single-use link from their agronomist, read a one-page consent in their language, and tick it.

Never add social sign-in, a "remember me" for over 12 hours, or a public sign-up.

## Screen inventory

| Screen | Role | What it holds |
| --- | --- | --- |
| Sign in | all | credentials, code, language toggle |
| Request access | visitor | the request form and a confirmation |
| Portfolio | any role with portfolio access | tiles and a map by region, crop or state; counts and risk band distribution; drill down to farms |
| Farm | any role with farm access | farm risk index with maturity badge, parcel list with bands and verdicts, farm-level facts, map |
| Parcel risk | any role with risk access | CRI, band, the seven components, missing components, percentile against the reference, method version |
| Methodology | all | index definitions, maturity status, what the index is not |
| Bulk import | analyst, org admin | upload, validation report, commit |
| Parcels | analyst, admin | filters, search, table, selected-parcel panel, namespace bar |
| Parcel evidence | analyst, customer, lender | declared versus measured, findings, imagery, photos, history |
| Imagery viewer | analyst, customer, lender, agronomist | satellite chip with overlays, layers, date slider; field photos |
| Data analysis | analyst, customer | readable share by size class, verdict mix, parcels over time, filters, export |
| Accuracy | analyst | hit rate by size class with intervals, kill line, controls |
| My book | customer | uploaded lists, results per list, reports |
| Tranches | lender | tranche counts, withheld by consent, parcel table, frozen report |
| Reports | analyst, customer, lender | frozen reports in English and Arabic, hash, download |
| Users and roles | organisation admin | invite, change roles and scope, deactivate, grants |
| Organisations | Zr3i admin | tenants, enabled modules, capability flags |
| Audit log | admin | append-only events, filter, export |
| Walk sheet | agronomist | assigned parcels in order, photo and boundary status |
| Consent | farmer | requester, purpose, data shared, duration, give or withdraw |
| Farmer home | farmer | my farms, my parcels with declared and measured area, who can see my data, Withdraw buttons; a banner when a company or authority manages the records |
| Farmers | company | invite, import, consent state per farmer (invited, pending, given) |
| Event evidence | insurance | what the satellite record shows inside an event window, per parcel, with withheld counts |
| Account types | Zr3i admin | the five types, how they relate, what each can do |

## Screen patterns

- **Desktop shell**: `Navigation` sidebar at the start edge, a header with the namespace bar, role switcher, language toggle and account menu, then one content area. Put the synthetic-data banner directly under the header.
- **List and detail**: a `Table` on the left and a selected-object panel on the end side (at least 360px). The panel never shows an action that decides the outcome.
- **Evidence page**: top to bottom: parcel id and `StatusPill`; declared versus measured with the noise; findings, each with its own pill; imagery (`ImageryViewer`); photos (`PhotoCard`); method and provenance; hashes. Anything a finding rests on is one click away.
- **Phones** (agronomist, farmer): one column, 390px reference width, 44px targets, the primary action fixed at the bottom, no tables (use stacked rows).
- **Empty states** say what is missing and why in one sentence ("No parcels in this tranche are readable yet"), never a cheerful illustration.

## Data analysis

- Lead with the count and the verdict mix (`VerdictBar`), then the readable share **by size class**; never pool classes into one rate.
- Show `IntervalChart` for hit rate with the kill line (70%) and target (80%). A class with no control set says "Not established".
- Every number has its unit and denominator ("17 of 135 readable parcels"); every chart has a text table beneath or behind a "View as table" button.
- Filters: namespace, tranche, size class, state, date range. Filters appear above the data and persist in the URL.
- Export gives CSV and the frozen PDF; both carry the report hash and the accuracy status. Do not offer a score, ranking or "top risk" view.

## Imagery and photos

- Satellite imagery is Sentinel-2 L2A at 10 m. A parcel smaller than about 8 whole pixels inside a 5 m buffer is **below resolution**; show the chip anyway and say why it cannot be assessed.
- Always show the declared boundary, the measured footprint and the pixel grid together, with date, sensor and cloud percentage as text.
- The date slider steps through usable scenes only; scenes lost to cloud or shadow appear as disabled ticks with the reason on focus.
- Real field photos carry timestamp, GPS and accuracy, agronomist id and a SHA-256 (`PhotoCard`). They are immutable and compared to the boundary, not to the verdict.
- Imagery in the design files is a labelled placeholder. Replace it with real chips and photos; do not use stock imagery in the product.

## Bilingual and right-to-left

- Every screen and report exists in English and Arabic. The user's language sets `lang` and `dir` on the root element; layouts use logical properties (`margin-inline-start`, `padding-inline-end`, `inset-inline-start`, `text-align: start`) so nothing is mirrored by hand.
- Mirror: navigation order, table column order, progress and back arrows, the side panel. Do not mirror: charts and time axes, play controls, ids, numbers, hashes, code and the status icons.
- Reports are generated in one language per document; each frozen report is rendered in both languages from the same data, with the same hash.
- Test every screen with the longest Arabic strings and with a mixed line (Arabic sentence containing `P-0147` and `7.3`).

## Accessibility

- WCAG 2.2 AA: 4.5:1 body text, 3:1 for large text, borders, focus rings and icons that carry meaning. Both themes are checked.
- Keyboard: every control reachable in reading order, visible `focus` ring, no keyboard trap in the imagery viewer; arrow keys move the date slider.
- Never use colour alone: every state has an icon and a word; charts have a text alternative and a table view.
- Respect `prefers-reduced-motion`; the product uses no animation beyond 150ms fades.
- Reports print at 12pt or larger in body and remain readable in greyscale.

## Risk screens

- An index is shown as a `RiskBand` chip (letter, word, percentile), the `MaturityBadge`, and the method version. Never show the number or the chip without them.
- The seven components are shown as bars with their own value and weight. Components that could not be established are listed, not hidden, and the index says it was renormalised.
- Parcels excluded by the validity floor are listed with the reason.
- Copy says "production-risk percentile", never score, rating, default probability or approval. The first line of every risk screen states: "Specified, not calibrated against harvest outcomes."
- Large portfolios: show distribution first (a band histogram), then a sortable table. No ranking of individuals and no "top risk" view.

## Rules the interface must keep

1. No approve, reject or recommend control, label or colour. Facts only.
2. Below resolution is never shown as "no crop".
3. The accuracy status is visible wherever a measurement-derived figure is shown.
4. Consent gates every read: withheld parcels are counted as withheld, never listed and never included in totals.
5. Synthetic namespaces are bannered on every page and watermarked in every report.
6. Risk bands appear only in the risk module, always with the letter, the word and the maturity badge.
7. Frozen reports cannot be edited; a change is a new report with a new hash.

## Account types

Five built-in account types share one multi-tenant platform. A type is a preset of roles, navigation, modules and views, not a separate code path.

| Type | Model | Home view | Navigation |
| --- | --- | --- | --- |
| Zr3i admin | Provider | Tenants and account types | Tenants, Account types, Methods |
| Bank | B2B lender | Tranche check, portfolio risk | Tranches, Reports |
| Insurance | B2B insurer | Event evidence, portfolio risk | Portfolio, Event evidence, Risk, Methodology |
| Company | B2B tenant, or an authority such as AgriCash | Portfolio | Portfolio, Farms, Farmers, Risk, Imports, Methodology |
| Farmer | B2B and B2B2C | My farms | My farms, Consent, Reports |

- The role label in the top bar names the account type, for example "Company · analyst".
- Sign-up asks for the account type first. Zr3i staff accounts are invitation only.
- A farmer can have a direct account or be managed by a company or authority. A managed farmer still controls consent; the Farmer view shows a banner naming the authority.
- Banks and insurers read another tenant's data only through a grant with a purpose and a period. The farmer view lists every grant with a Withdraw button.
- Farmer pages use plain words, larger text and the same status shapes. "Not assessable" is always explained as "image too coarse", never as "no crop".
