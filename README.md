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
