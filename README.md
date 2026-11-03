# Zr3i backend

.NET 10 API and worker for the Zr3i platform (modular monolith, MongoDB). The frontend is a separate repo (Zr3i-Frontend).

## Run

Needs the .NET 10 SDK and a local MongoDB.

```
dotnet run --project src/Zr3i.Web
```

Development settings (`src/Zr3i.Web/appsettings.Development.json`) use `mongodb://localhost:27017`, database `zr3i_dev`, run the migrations on start and allow CORS from `http://localhost:5173`. For any other connection string use user-secrets or an environment variable (`Mongo__ConnectionString`); never commit one.

## Test

```
dotnet test
```

Integration tests use a throwaway database on `ZR3I_TEST_MONGO` (default `mongodb://localhost:27017`) and drop it afterwards.

## API (stable error codes; wording lives in the frontend)

| Route | Auth | Purpose |
| --- | --- | --- |
| `GET /health`, `GET /ready` | none | liveness, database check |
| `GET /api/auth/totp-secret` | none | new authenticator secret and `otpauth://` URI |
| `POST /api/invitations/accept` | invitation token | new person sets a password and authenticator |
| `POST /api/auth/sign-in` | none | email and password, returns a challenge |
| `POST /api/auth/verify` | challenge | six-digit code, returns a bearer token (12 h) |
| `POST /api/auth/sign-out` | bearer | revoke the session |
| `GET /api/me` | bearer | profile, organisations, permissions |
| `POST /api/invitations/accept-existing` | bearer | existing user joins another organisation |
| `POST /api/organisations/{id}/invitations` | bearer, `kernel:users:manage` | invite a person, returns the link token |

Auth routes are rate limited per client address.

## Bulk import

Run the API and the worker (`dotnet run --project src/Zr3i.Worker`) together. Upload a CSV, wait until it is `validated`, read the error report, then commit.

| Route | Needs | Purpose |
| --- | --- | --- |
| `POST /api/organisations/{id}/imports?fileName=` (body: the CSV) | `kernel:imports:write` | store the file and queue validation (202) |
| `GET /api/organisations/{id}/imports`, `GET .../imports/{batch}` | `kernel:imports:read` | list, and status with counts and progress |
| `GET .../imports/{batch}/errors`, `GET .../imports/{batch}/errors.csv` | `kernel:imports:read` | the error report as JSON (paged) or CSV |
| `POST .../imports/{batch}/commit`, `POST .../imports/{batch}/discard` | `kernel:imports:write` | apply the valid rows (202), or throw the batch away |

CSV: UTF-8, comma, semicolon or tab. One row per parcel and season. Required columns: `parcel_external_id`, `farm_external_id`, `farm_name`. Optional: `parcel_label`, `portfolio_external_id`, `portfolio_name`, `governorate`, `markaz`, `village`, `cadastral_ref`, `season`, `declared_crop`, `declared_area_feddan`, `declared_sowing_date`, `declared_source`, `boundary_geojson` (a GeoJSON Polygon), `boundary_source`, `captured_by`, `captured_at`, `accuracy_m`, and custom fields as `farm.key`, `parcel.key`, `portfolio.key`, `season.key`.

Importing the same file again changes nothing. A blank cell never erases a value already stored. Errors are stable codes (for example `invalid_number`, `self_intersection`, `parcel_belongs_to_other_farm`); the wording lives in the frontend.

## Account types, sign-up and consent

First start of a new installation: set `Platform:BootstrapAdminEmail`; the API logs a one-time invitation for the first Zr3i administrator.

| Route | Access | Purpose |
| --- | --- | --- |
| `GET /api/account-types`, `POST /api/access-requests` | open | the request-access form (fields come from the account type) and its submission |
| `GET /api/platform/access-requests`, `POST .../{id}/approve`, `POST .../{id}/reject` | `platform:access-requests:manage` | the Zr3i administrators' queue; approving creates the organisation and returns the first admin's invitation |
| `POST/GET /api/organisations/{id}/farmers` | `kernel:farmers:write` / `read` | the farmers whose consent governs a set of farms |
| `POST /api/organisations/{id}/consent-requests` | `kernel:consents:write` | ask a farmer to share data; returns the link for the farmer |
| `GET /api/consent/{token}`, `POST /api/consent/{token}`, `POST /api/consent/withdraw/{token}` | the link | the farmer's one-page consent and withdrawal, no account needed |
| `GET/POST /api/organisations/{id}/consents`, `POST .../consents/{grant}/withdraw` | `kernel:consents:read` / `manage` | a farmer with their own account grants and withdraws |
| `GET /api/organisations/{id}/shared`, `GET .../shared/{grant}/parcels?ids=` | `kernel:shared:read` | what has been shared with this organisation, and reading it through the consent gate |

Consent belongs to the farmer: a company can only ask, withdrawal takes effect on the next read, and every read or refusal is audited in both organisations. Parcels outside a grant are returned only as a `withheld` count.

## Readability (can a parcel be tested at 10 m?)

`GET /api/organisations/{id}/readability` (`kernel:readability:read`) returns, per size class, how many parcels have at least 8 whole 10 m pixels inside their boundary after a 5 m inward buffer. The share is always per class, never pooled, and "below resolution" is never "no crop".

The size classes come from configuration, not code. Set the edges in feddan (for example in `appsettings`), or the endpoint answers `409 size_classes_not_configured`:

```
"Verification": { "SizeClassEdgesFeddan": [ 1, 3, 10 ] }
```

## Methods

A method (for example `ZR3I-R0-2026-01`) is a versioned set of parameters. Each parameter has a status (`derived`, `specified`, `starting_rule` or `undecided`) and a source. A draft can be edited; it can only be frozen when nothing is undecided and every provisional rule is approved by name, and a frozen version is sealed with a SHA-256 hash and never changes. Frozen reports may cite only a frozen version.

| Route | Access | Purpose |
| --- | --- | --- |
| `GET /api/methods`, `GET /api/methods/{id}?version=` | signed in | the methodology: every parameter with its status and source, and the seal of a frozen version |
| `PUT /api/platform/methods/{id}/{version}/parameters/{key}` | `platform:methods:manage` | set a parameter on a draft |
| `POST /api/platform/methods/{id}/{version}/freeze` | `platform:methods:manage` | freeze a draft, naming who approves each provisional rule |
| `POST /api/platform/methods/{id}/{version}/new-draft` | `platform:methods:manage` | open the next version as a copy |

## Satellite scenes

`POST /api/organisations/{id}/imagery/discover` (`kernel:imagery:write`, body `{ "from", "to", "maxCloudCover" }`) queues one scene search per map cell that holds a parcel of the organisation; the worker runs them and fills a shared catalogue of Sentinel-2 L2A scenes. `GET /api/imagery/scenes?from=&to=&tile=` lists the catalogue (any signed-in user). Scenes carry the Copernicus attribution and a hash of the provider's item. Run the worker (`dotnet run --project src/Zr3i.Worker`) for the searches to happen.

A test that talks to the real Earth Search service is off by default; run it with `ZR3I_NETWORK_TESTS=1 dotnet test --filter Category=Network`.

`POST /api/organisations/{id}/imagery/ingest` (`kernel:imagery:write`, body `{ "from", "to", "maxCloudCover", "tile" }`) queues one job per catalogue scene in the period; the worker reads each scene for the organisation's parcels and records, per parcel and scene, how many whole pixels are usable and whether the scene is readable. `GET /api/organisations/{id}/parcels/{parcelId}/observations` (`kernel:imagery:read`) returns them with their provenance (scene and item hash, boundary version, methods, reader, a hash of the pixel values). A parcel too small to test is reported as `below_resolution`, never as "no crop". Results made under a draft method say so.

The raster reader needs a CA certificate file for HTTPS: set `ZR3I_CA_BUNDLE` (on Linux the system store is used). `ZR3I_NETWORK_TESTS=1` also runs a test that reads the real spike scene.

