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

