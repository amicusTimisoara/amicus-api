# amicus-api

Backend API for AMiCUS Timișoara, shared by the mobile and web clients.

- **Stack:** .NET 10 (ASP.NET Core) · PostgreSQL 18 · EF Core 10 (Npgsql)
- **Clients:** [`amicus-web`](https://github.com/amicusTimisoara/amicus-web) (React, live at `app.thorsp.net`) and `amicus-mobile` (React Native — not created yet)
- **Live:** `api.thorsp.net` (prod) · `stage.thorsp.net` (stage)

## The domain, in one paragraph

Students book short advice appointments with visiting specialists — a lawyer, a
physician, an accountant, a counsellor. Everything hangs off an **`Event`** (a
congress, an advice day) with a start and end date. An **admin** assigns each
specialist's availability as a **`SlotPattern`** ("Tuesdays 14:00–18:00, 30-minute
slots"); specialists do not manage their own time, so a specialist does not even
need an account. Expanding the patterns across the event's date range
materialises **`Slot`** rows, and a student taking one creates a **`Booking`**.

Scoping to events rather than an open-ended weekly timetable is deliberate: there
are no holiday exceptions to model, because events simply end.

### Two decisions worth knowing before you change anything

**The slot board is shared, the bookings are not.** Every student sees which slots
are taken so nobody double-books — but only free/taken and when, never who. Some
of these specialists are physicians and lawyers, so "who is seeing whom" is
readable by that student, their specialist, and admins. Nothing in the public
board response should ever carry a `Booking`.

**Postgres owns "this slot is taken",** via a partial unique index
(`ux_booking_live_slot` on `slot_id WHERE status <> 'Cancelled'`). Two students
tapping the same slot in the same second is a race an application-level "is it
free?" check cannot reliably win, so the loser gets a unique violation to handle.
The filter is what lets a cancelled booking free the slot while its history stays
on the row. Slots are stored rather than computed on the fly precisely so this
index can exist.

Time handling: pattern times are wall-clock in the event's IANA zone, slot
instants are UTC. `SlotPlanner` is pure (no clock, no database) and owns the DST
edges — a start inside the spring-forward gap is skipped rather than silently
moved, and an ambiguous autumn hour resolves to the first pass. Those cases are
tested; read `SlotPlannerTests` before touching it.

## Layout

```
src/Amicus.Domain          entities + SlotPlanner. No dependencies, no EF, no web.
src/Amicus.Infrastructure  EF Core, DbContext, migrations, Identity user/role types.
src/Amicus.Api             ASP.NET Core host, DI wiring, endpoints.
tests/Amicus.Domain.Tests  unit tests for the pure logic.
```

Identity wiring lives in `Amicus.Api`, not Infrastructure: `AddIdentityApiEndpoints`
is part of the ASP.NET Core shared framework, and pulling that into a class library
would make persistence depend on the web stack.

## Running it

```bash
docker compose up -d                 # Postgres on localhost:5433
dotnet tool restore                  # pins dotnet-ef via dotnet-tools.json
dotnet dotnet-ef database update -p src/Amicus.Infrastructure -s src/Amicus.Api
dotnet run --project src/Amicus.Api
```

Port **5433**, not 5432 — the VerseMate stack already claims 5432, and running
both at once should not be a choice you have to make.

`GET /health` round-trips the database, so a green health check means the API can
actually serve, not just that the process started.

## Database and SQL — how it actually works

**Code-first EF Core migrations. No hand-written SQL, no schema tool, no
`.sql` files to keep in step.**

The C# entities in `Amicus.Domain` plus the Fluent configuration in
`AmicusDbContext.OnModelCreating` *are* the schema definition. The cycle:

1. Change an entity or its configuration.
2. `dotnet dotnet-ef migrations add <Name> -p src/Amicus.Infrastructure -s src/Amicus.Api`
   EF diffs the model against `AmicusDbContextModelSnapshot.cs` and writes a
   migration class with `Up()` and `Down()`, plus an updated snapshot.
3. Review the generated migration. **It is normal code and it is committed** —
   `src/Amicus.Infrastructure/Migrations/` is part of the repo and reviewed in the PR.
4. `dotnet dotnet-ef database update -p src/Amicus.Infrastructure -s src/Amicus.Api`
   applies whatever has not run yet and records it in the `__EFMigrationsHistory`
   table, which is how the database knows where it is.

Rules that matter:

- **Never edit a migration that has already been applied anywhere.** Add a new
  one. The snapshot is the diff baseline, so hand-editing one file and not the
  other produces migrations that generate nothing, or the wrong thing.
- **Read what EF generated before committing it.** A rename looks like a
  drop-plus-add to the differ, which silently discards data.
- `Down()` is generated for free but rarely exercised. Do not rely on it in
  production; roll forward.
- Constraints and indexes belong in `OnModelCreating`, not in a manual script, so
  they travel with the model. That is how `ck_slot_pattern_window_ordered` and the
  partial index `ux_booking_live_slot` came to exist.
- Migrations are **not** applied automatically at startup. Applying them is a
  deliberate step, so a rolling deploy cannot have two versions racing to migrate.

To see the SQL without touching a database:

```bash
dotnet dotnet-ef migrations script -p src/Amicus.Infrastructure -s src/Amicus.Api
```

Queries are LINQ, translated by Npgsql. `UseSnakeCaseNamingConvention()` maps
`StartsAt` to `starts_at`, so the schema reads like ordinary Postgres.

## Auth

ASP.NET Core Identity, mounted under `/auth` — `register`, `login`, `refresh`,
`manage/info`. Email + password works today and returns bearer tokens.

Passwords require 10 characters but no symbol classes: students type these on a
phone, and length carries far more real strength than rules that mostly produce
`Pa$$w0rd`.

### Password reset + email confirmation

Registering an `IEmailSender<AppUser>` (`SmtpEmailSender`, MailKit) is what makes
Identity's `/auth/forgotPassword` + `/auth/resetPassword` actually deliver —
without one they succeed silently and send nothing. Configure SMTP under `Email:*`
(Gmail SMTP is plenty for this volume). When it is unconfigured the sender logs and
no-ops, because `/forgotPassword` always answers 200 so it can't be used to probe
which emails exist.

Registration **auto-confirms** the email (`AutoConfirmUserManager`) so login works
immediately *and* password reset works — Identity only mails a reset to a confirmed
address, and confirmation is not required to sign in. A friendly "verify your
address" email still goes out with a link; clicking it is an idempotent nicety.

Reset and confirmation emails carry **links to the web client** (`Email:WebResetUrl`
→ `/reset`, `Email:WebConfirmUrl` → `/confirm`) when those are set; otherwise a bare
code. `POST /admin/users/reset-password` (Admin) sets a password directly — the
human fallback when email is unavailable.

```jsonc
"Email": {
  "Host": "smtp.gmail.com", "Port": 587,
  "User": "…@gmail.com", "Password": "<app password>",
  "From": "…@gmail.com", "FromName": "AMiCUS Timișoara",
  "WebResetUrl": "https://app.thorsp.net/reset",
  "WebConfirmUrl": "https://app.thorsp.net/confirm"
}
```

### CORS

The web app and the API are different origins (`app.thorsp.net` → `api.thorsp.net`),
so the browser needs CORS. `Cors:Origins` is an exact-origin allowlist and
`Cors:OriginSuffixes` matches origin suffixes (for the `*.amicus-web.pages.dev` PR
previews). Bearer tokens, not cookies, so no credentials mode.

```jsonc
"Cors": {
  "Origins": ["https://app.thorsp.net", "http://localhost:5173"],
  "OriginSuffixes": [".amicus-web.pages.dev"]
}
```

### Google sign-in

**Client-side ID-token flow, not a server redirect.** The web SPA and both mobile
platforms obtain an ID token from Google's own SDK and `POST` it to
`/auth/google`, which verifies it and returns the same `AccessTokenResponse` as
`/auth/login`. No redirect URIs, no deep links, no custom URL schemes, and one
code path for every client.

```jsonc
// appsettings, or user-secrets / environment in production
"Authentication": {
  "Google": {
    // Web, iOS and Android are separate OAuth clients in Google Cloud but one
    // account here, so every client ID that may mint tokens has to be listed.
    "ClientIds": [ "1234-web.apps.googleusercontent.com" ]
  }
}
```

An **unverified** Google email is refused: accounts are matched by address, so
honouring one would let anyone who edits their Google profile email take over
somebody else's account. A verified address that already has a password account
gets **linked** rather than duplicated, so signing in with Google later does not
lock a student out of the account they registered.

The web button is live on `app.thorsp.net`. ⚠ The OAuth **consent screen is in
Testing mode**, so only test users listed on it can complete Google sign-in until
the app is published; everyone else uses email + password.

### Becoming an admin

No default credentials ship anywhere. Register normally, add the address to
`Bootstrap:AdminEmails`, restart — startup promotes the existing account.

## Endpoints

| | |
|---|---|
| `POST /auth/register` · `login` · `refresh` · `manage/info` | email + password |
| `POST /auth/forgotPassword` · `resetPassword` · `confirmEmail` | password reset + email confirm |
| `POST /auth/google` | exchange a Google ID token for ours |
| `GET /events` · `GET /events/{slug}` | published events and their specialists (incl. each specialist's `category`) |
| `GET /events/{slug}/board?from=&to=` | the shared board — free/taken and when, never who |
| `POST /bookings` · `GET /bookings/mine` · `POST /bookings/{id}/cancel` | a student's own bookings |
| `POST /check-in` | scan a QR code (Specialist or Admin only) |
| `GET /admin/events` · `specialists` · `events/{id}/specialists` · `events/{id}/slots` | admin reads (incl. drafts) |
| `POST /admin/...` | create events/specialists, rosters, patterns, generate-slots, publish/unpublish, slot block/unblock |
| `PATCH /admin/specialists/{id}` | edit a specialist (incl. its `category`) |
| `POST /admin/users/reset-password` | admin sets a student's password (fallback) |

`POST /admin/events/{id}/generate-slots` is safe to re-run: existing slots are
left alone, and a slot no longer produced by any pattern is removed **only** if
nobody ever booked it.

Each `Specialist` has a first-class **`category`** (`social`, `spiritual`,
`mentorat`, `medical`, `juridic`, `cariera`; default `social`) so the clients group
and colour them without guessing from the free-text specialty.

## Running on the Raspberry Pi

nginx terminates TLS and proxies each environment's **subdomain** to a systemd
service; Postgres is the same container as dev, each env with its own database and
role. (The legacy `thorsp.net/amicus/` path still works for backward compat.)

| | prod | stage |
|---|---|---|
| Public URL | `https://api.thorsp.net` | `https://stage.thorsp.net` |
| Service | `amicus-api.service` :5090 | `amicus-api-stage.service` :5091 |
| Published app | `~/apps/amicus-api/` | `~/apps/amicus-api-stage/` |
| Settings + secrets | `~/.config/amicus/api.env` | `~/.config/amicus/api-stage.env` |
| Database | `amicus_prod` / `amicus_app` | `amicus_stage` / `amicus_stage` |
| nginx | `sites-available/amicus-subdomains` (server blocks per host) | |

Deploys run through `scripts/deploy.sh <stage|prod>` (below) — you rarely run the
raw commands, but they are: `dotnet publish … -o <appdir>`, source the env file
(`set -a; . …; set +a`), `dotnet ef database update`, `sudo systemctl restart …`.

Things that will bite whoever touches this next:

- **The connection string in `api.env` is quoted, deliberately.** It contains
  semicolons. systemd reads `KEY=VALUE` to end of line and does not care, but
  `. api.env` in a shell takes `Host=localhost` as the value and each following
  `Port=`/`Database=`/`Password=` as a *separate assignment* — leaving a truncated
  string that silently falls back to port 5432.
- **The subdomains are root-served** (`api.thorsp.net/…`), so there's no path
  prefix to handle — `Location` headers are correct as-is. The legacy `/amicus/`
  path still strips its prefix and passes `X-Forwarded-Prefix`, which the app
  applies as `PathBase`; `Results.Created` with a literal path ignores PathBase, so
  those go through `CreatedAt.Path`. The app does **not** strip a prefix left on the
  path — `WebApplication` inserts `UseRouting` before user middleware.
- **Data Protection keys persist to `~/.config/amicus/dp-keys`.** Without a
  configured path they live in memory and every restart invalidates every issued
  token, signing out every student for no visible reason. Verified: a token issued
  before a restart still works after it.
- **Migrations are not applied by the service.** A restart must never change the
  schema.
- **`docker compose` binds Postgres to `127.0.0.1` on purpose.** A bare
  `"5433:5432"` binds `0.0.0.0`, and Docker's published ports **bypass UFW**
  entirely on this host — the whole LAN could then reach the database with the
  password committed in `docker-compose.yml`.
- Registration is open to the internet. Per-IP rate limits
  (`RateLimits:AuthPermitsPerMinute`, default 30/min) are the only guard; an
  allowlist is still an open question.

### Measured footprint

Raspberry Pi 5, 4 cores. 384-slot board, 32 concurrent clients, 30 s, Release
build:

| | idle RSS | peak RSS | throughput | CPU |
|---|---|---|---|---|
| API, Server GC | 190 MB | 252 MB | 263 req/s | 1.68 cores |
| **API, Workstation GC** | **176 MB** | **205 MB** | **255 req/s** | 1.62 cores |
| API, 2016-slot board | 200 MB | 305 MB | 101 req/s | 1.68 cores |
| Postgres container | 59 MB | — | — | ~0 idle |

`ServerGarbageCollection` is therefore **off** (see the comment in
`Amicus.Api.csproj`): Server GC allocates a heap per core and bought ~3% more
throughput for ~47 MB. Flip it back on hardware where RAM is not the constraint.

Idle service under systemd sits around **63 MB**; the ~190 MB above is after the
GC has grown its heaps under load. Budget roughly **250 MB for the API plus 60 MB
for Postgres** on this box.

The 2016-slot row is why `GET /events/{slug}/board` takes optional `from`/`to`:
a whole multi-week event is a genuinely large response, and clients showing one
day should say so.

## Deployment (CI/CD)

Two environments, both on the Pi, deployed by a self-hosted GitHub Actions runner
(`.github/workflows/deploy.yml` → `scripts/deploy.sh`):

| env | trigger | service | port | database | URL |
|---|---|---|---|---|---|
| **stage** | push to `main`, or manual | `amicus-api-stage` | 5091 | `amicus_stage` | `stage.thorsp.net` |
| **prod** | a published GitHub **Release**, or manual | `amicus-api` | 5090 | `amicus_prod` | `api.thorsp.net` |

So: merge a PR → it lands on **stage** automatically; when stage looks good, cut a
**Release** → it goes to **prod**. `workflow_dispatch` deploys either on demand.

`deploy.sh <stage|prod>` publishes, applies migrations to that env's database, restarts
the service, and health-checks it. Secrets live in `~/.config/amicus/api.env` /
`api-stage.env` (not in git). CI (`ci.yml`, build + test) still runs on GitHub-hosted
runners for every PR; only the deploy job uses the Pi.

## Tests

```bash
docker compose up -d && dotnet test
```

`Amicus.Domain.Tests` is pure and needs nothing. `Amicus.Api.Tests` hosts the real
app against a **real Postgres** — it creates an `amicus_test` database beside the
dev one and truncates between tests. Point it elsewhere with
`AMICUS_TEST_POSTGRES` (CI uses a service container).

There is no in-memory provider anywhere on purpose: the double-booking guard is a
Postgres partial index, and a fake provider would let those tests pass while
production stayed broken.

## Contributing

`main` is protected: open a PR, get one approval, merge with **squash** (the only
method enabled). Merged branches delete themselves.

CI fails the build on code warnings, but NuGet audit warnings (NU19xx) only warn —
a newly published advisory against a transitive package should not block every open
PR overnight. Dependabot raises those as its own PRs instead.

`Microsoft.OpenApi` is pinned to 2.7.5 on purpose; the reason is in
`Amicus.Api.csproj` next to the pin. Do not "upgrade" it to 3.x.
