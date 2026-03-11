# KPACS Share Relay

Prototype HTTPS relay for asynchronous KPACS study sharing.

## MVP scope

- Register users by email and display name
- Register viewer devices with public encryption/signing keys
- Search recipients
- Create shares and upload an encrypted package blob
- List inbox items for a recipient
- Download a package and record recipient acknowledgements

The current prototype intentionally stores only encrypted package blobs. The relay never needs plaintext DICOM data.

## Configuration

### Local development

Set the Postgres connection in `appsettings.json` or override it with environment variables:

- `ConnectionStrings__RelayDb`
- `Auth__ApiKeys__0`
- `Auth__HeaderName`
- `RateLimiting__PermitLimit`
- `RateLimiting__WindowSeconds`
- `Storage__PackagesRoot`

Default local connection string:

- `Host=localhost;Port=5432;Database=kpacs_share_relay;Username=postgres;Password=postgres`

### Railway

Railway can provide Postgres through `DATABASE_URL`. This project detects that format automatically and converts it to an `Npgsql` connection string.

Recommended Railway variables:

- `DATABASE_URL`
- `Auth__ApiKeys__0=<long-random-secret>`
- `RateLimiting__PermitLimit=60`
- `RateLimiting__WindowSeconds=60`
- `Storage__PackagesRoot=/data/packages`
- `ASPNETCORE_ENVIRONMENT=Production`

For a persistent prototype, mount a volume and point `Storage__PackagesRoot` at that volume.

## Railway deployment

This repository is a monorepo. The git root is the `CSharp` folder, so the Railway service must point at `KPACS.ShareRelay` as its root directory.

### One-time Railway setup

1. Push this branch to GitHub.
2. In Railway, create a new project.
3. Add a PostgreSQL service to that project.
4. Add a new GitHub-backed service from `andreasknopke/KPACS-neo`.
5. In the new service settings, set the root directory to `KPACS.ShareRelay`.
6. Leave the builder on Dockerfile autodetect, because `KPACS.ShareRelay/Dockerfile` is already included.
7. Add a Railway volume and mount it at `/data`.
8. In the service variables, set:
	- `Auth__ApiKeys__0` = a long random secret
	- `Storage__PackagesRoot` = `/data/packages`
	- `ASPNETCORE_ENVIRONMENT` = `Production`
	- `RateLimiting__PermitLimit` = `60`
	- `RateLimiting__WindowSeconds` = `60`
9. Connect the PostgreSQL service so Railway injects `DATABASE_URL` into the relay service.
10. Redeploy the relay service.

### What Railway should show when it works

- Build log runs `dotnet publish KPACS.ShareRelay.csproj -c Release -o /app/publish`
- Deploy log starts `KPACS.ShareRelay.dll`
- The service gets a public Railway domain
- `GET /health` returns JSON with `"status": "ok"`

### First smoke test after deploy

Use your Railway domain in these checks.

1. Open `https://YOUR-RAILWAY-DOMAIN/health`
	- Expected result: JSON response with service name and current UTC time.
2. Call the protected contact search endpoint with the API key:

	`curl -H "X-Relay-Api-Key: YOUR_SECRET" "https://YOUR-RAILWAY-DOMAIN/api/v1/contacts/search?query="`

	- Expected result: `200 OK` with an empty `contacts` array on a fresh database.

### Important notes for Railway

- Do not set `ConnectionStrings__RelayDb` when Railway already provides `DATABASE_URL`.
- The mounted volume is important; otherwise uploaded packages disappear on redeploy.
- If you change the API key later, update the viewer setup in the Email tab as well.
- The current prototype stores encrypted package blobs and metadata in Postgres, but it does not yet implement production-grade user authentication.

## Prototype authentication

All `/api/v1/*` endpoints now require a relay API key.

- Header: `X-Relay-Api-Key: <key>`
- Or bearer token: `Authorization: Bearer <key>`

Default values:

- Development: `dev-relay-key`
- Base config placeholder: `change-me-relay-key`

Change the production key before deploying anywhere public.

Example:

- `curl -H "X-Relay-Api-Key: dev-relay-key" http://localhost:5082/api/v1/contacts/search`

## Rate limiting and audit logging

The relay now applies a fixed-window per-IP limit to `/api/v1/*` requests.

- Default window: `60` seconds
- Default permits: `60` requests per window
- Exceeding the limit returns `429 Too Many Requests`

Each API request is also emitted as a structured audit log entry containing:

- method and path
- response status
- remote IP
- request duration
- query-based actor identifiers when present

## API sketch

- `POST /api/v1/users/register`
- `POST /api/v1/devices/register`
- `GET /api/v1/contacts/search?query=...&excludeUserId=...`
- `POST /api/v1/shares`
- `PUT /api/v1/shares/{shareId}/package?actorUserId=...`
- `GET /api/v1/inbox?recipientUserId=...`
- `GET /api/v1/shares/{shareId}?actorUserId=...`
- `GET /api/v1/shares/{shareId}/package?actorUserId=...`
- `POST /api/v1/shares/{shareId}/ack`

## Notes

- Trust model is Option A: server-assisted discovery plus device public keys.
- Package encryption is expected to happen client-side in the viewer.
- This is still a prototype foundation; real user/device authentication, stronger audit retention, and production key verification still need to be added before hospital deployment.
