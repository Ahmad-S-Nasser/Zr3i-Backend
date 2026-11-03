Zr3i is a multi-tenant agricultural risk-intelligence platform. Its modules today are Verification (declared against measured, per parcel) and Risk (CRI per parcel, FRI per farm). The interface states measured facts, shows the evidence and maturity behind each figure, and never advises. Build every screen, report and email from these rules.

## Identity

- Name the product **Zr3i** in running text and set the wordmark as **ZR3I**: Roboto Black caps, very large, white on `zr-sage`, with a short `zr-ember` rule (128 by 4px) beneath it. Above it goes a letter-spaced bold eyebrow in `zr-navy`: "AGRICULTURAL RISK INTELLIGENCE" for the platform, with modules named under it ("Verification", "Risk"). The legal entity, "DigiWell for Digital Solutions LLC (trading as Zr3i)", appears only in contracts, report footers and the about page.
- The look is the Zr3i Overview Deck: a flat `zr-sage` field, flat `zr-field` (deep field green) blocks with `zr-amber` bold upper-case headings and white text, a thin `zr-ember` rule, generous left-aligned margins, a small footer line and a two-digit page number. Corners are softly rounded; nothing is shadowed or gradient.
- DigiWell is an endorsement, not a second brand: "a DigiWell product" in 12px at the foot of the login panel and report footers. No DigiWell logo or colour has been supplied, so none is used.
- Zr3i is a multi-tenant platform. Tenants may add a logo and one accent colour that fills named slots only; status colours, risk bands, banners, maturity badges and the Zr3i footer are never overridden.
- The product speaks in the third person about data and in the second person to the user: "Measured area is 7.3 feddan against 10.0 declared." Never "we think", "looks good" or "high risk".

## Colour

Use tokens, never raw hex. There are two themes, `light` (default) and `dark`; set `data-theme` on the root element.

| Job | Light | Dark | Use |
| --- | --- | --- | --- |
| Page | `ground` | `ground` | behind cards |
| Card, table, form, report page | `surface` | `surface` | holds text |
| Inset wells, table headers, hash blocks | `sunken` | `sunken` | quiet fill |
| Text | `ink`, `ink-muted` | `ink`, `ink-muted` | 6:1 or better on every ground |
| Primary action, active marker | `brand` with `on-brand` text | same tokens | one per view |
| Links | `link` | `link` | inline and text buttons |
| Control borders | `line-strong` | `line-strong` | 3:1 or better |
| Hairlines | `line` | `line` | decoration only |
| Focus ring | `focus` | `focus` | 2px solid, 2px offset |

- **Risk bands** A to E (`band-a` to `band-e`, letters in `on-band-*`) belong to the risk module only. Draw each as a square chip with the large letter and the word (A lowest risk, E highest); never as a bare colour, never in verdict tables, never beside a status pill without the module label.
- **Maturity badge** (`maturity-bg`, `maturity-ink`) marks every index as specified, calibrated or validated, in upper-case Roboto. It cannot be hidden.
- **Identity fields** use the fixed `zr-*` colours in both themes: `zr-sage` for the login panel and report covers with `zr-navy` text; `zr-field` for the sidebar and dark panels with `zr-mist` or white text; `zr-amber` only for small labels on `zr-navy` or `zr-forest`; `zr-ember` only as a hairline rule or a large mark, never text (3.5:1 on white).
- **Status** uses four paired tokens: `confirmed`, `flagged`, `pending`, `not-assessable`, each with a `-bg` fill. Every status shows an icon and a word as well as colour (see `StatusPill`). `flagged` is a burnt orange and `pending` a blue so none of the four depends on a red-green difference.
- **Amber as text** is `accent-ink` (a brown in light, a gold in dark), for the eyebrow and the synthetic-data notice.
- **Imagery overlays** use `img-boundary` (declared) and `img-measured` (measured, dashed), always with a navy halo. NDVI uses `img-ndvi-low`, `img-ndvi-mid`, `img-ndvi-high` plus a legend.
- This is an agriculture product, so green carries the identity: `zr-field` for blocks, sidebar and primary buttons. `zr-navy` is the text colour only. Text on `zr-sage` is `zr-navy` (7.7:1). White on sage is about 2:1: use it only for the very large wordmark, never for words people must read.

## Type

Latin and Arabic are one system.

| Role | Latin | Arabic | Styles |
| --- | --- | --- | --- |
| Wordmark, headings, labels, buttons | Roboto (700 and 900) | Cairo (600 and 700) | `display-xl`, `display-lg`, `display-md`, `label`, `button`; `ar-display`, `ar-label` |
| Body and interface text | Lato (400 and 700) | Noto Sans Arabic (400 and 600) | `title`, `body`, `body-sm`; `ar-title`, `ar-body`, `ar-small` |
| Data, ids, hashes | Roboto Mono | Roboto Mono (Western digits) | `data`, `hash` |

- Wire families by token: `--font-display`, `--font-sans`, `--font-mono`, `--font-arabic`, `--font-arabic-display`. The `sans` and `display` stacks list the Arabic face second, so mixed text falls back correctly without markup.
- All faces are free Google Fonts and load from Google Fonts; no font binary is stored here. Embed the same faces in PDFs.
- Eyebrows and block headings are Roboto Bold, upper case, letter-spaced 0.14em to 0.3em (`label`). Arabic has no upper case: use `ar-label` with `letter-spacing: 0`.
- Body is 15px on 24px leading; Arabic body is 16px on 28px. Use `ar-*` styles on any element whose `dir` is `rtl`.
- Numbers, parcel ids (`P-0147`), dates and hashes use Western digits and stay left-to-right in both languages, wrapped in `dir="ltr"`. This is a product decision that can be revisited with Arabic-reading customers.
- No more than two weights per screen apart from the wordmark.

## Space, shape and depth

- Spacing is a 4px scale: `space-1` 4, `space-2` 8, `space-3` 12, `space-4` 16, `space-5` 24, `space-6` 32, `space-7` 48, `space-8` 64. Cards pad `space-4`; screens pad `space-6` on desktop and `space-4` on phones.
- Corners are soft, never sharp and never bubbly: `radius-sm` 6px (pills, tags, band chips), `radius-md` 10px (buttons, inputs, banners), `radius-lg` 14px (cards, blocks, tables, modals), `radius-pill` for filter chips, `radius-dot` for status circles and map dots. Full-bleed edges stay square: the sidebar, the top bar and report pages.
- Content blocks are flat: a `zr-field` block with white text and an amber heading, or a `surface` block with a 1px `line` hairline. `shadow-pop` is for menus and tooltips only.
- Content width is capped at `content-max` (1200px). Report pages are A4, `page-a4-w` 794px wide, with 18mm margins.
- Interactive targets are at least `tap-min` (44px) in both axes.

## Iconography

- Icons are 16px or 20px outline glyphs with a 1.5px stroke, drawn on a 24px grid, in `ink`, `ink-muted` or a status token. Use Lucide square-cap outline icons for the interface set; state this choice in code, do not redraw its icons.
- The four status icons are fixed and are not Lucide: filled circle (confirmed), triangle (flagged), half circle (pending), dashed circle (not assessable). They are the only icons that carry meaning on their own and they always sit next to a word.
- Do not use emoji, illustrated characters, gradients or decorative leaf marks in the product.

## Content

- Write short, factual sentences in sentence case. Say what was measured, with the unit and the noise. Examples to copy: "Measured area is 7.3 feddan against 10.0 declared." "Gap 27%, outside the ±4.6% measurement noise." "34 parcels withheld by consent."
- Words that never appear in the product: approve, reject, recommend, score, rating, risk (in R0), safe, good, bad, passed, failed. A parcel is Confirmed, Flagged, Pending or Not assessable.
- "Below resolution" means the parcel is too small to test at 10 m; say so, and never write "no crop" for it.
- Show **Accuracy: NOT_ESTABLISHED** wherever a figure rests on a measurement whose hit rate has not been established. Show **ILLUSTRATIVE · SYNTHETIC DATA** on every page of a synthetic namespace.
- Dates are `YYYY-MM-DD` with a time offset when time matters. Area is in feddan with the unit in the column header.

| English | Arabic |
| --- | --- |
| Confirmed | مؤكَّدة |
| Flagged | مُعلَّمة |
| Pending | قيد الانتظار |
| Not assessable | غير قابلة للتقييم |
| Below resolution | أقل من دقة الصورة |
| Measurement noise | هامش القياس |
| Declared | المُعلنة |
| Measured | المقيسة |
| Hit rate | نسبة الإصابة |
| Synthetic data | بيانات اصطناعية |
| Pilot | المشروع التجريبي |
| Parcel | قطعة |
| Feddan | فدان |

The Arabic terms are a first draft and must be reviewed by a native speaker before any customer sees them.
