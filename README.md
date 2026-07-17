# TenderLens

TenderLens is an explainable anomaly-detection demo for Bulgarian public procurement. Enter a Bulgarian company EIK to inspect a procurement-derived supplier profile, five deterministic signals, their evidence, peer context, and the source records behind them.

The deployed container reads an immutable snapshot built offline from public OCDS data. The historical pipeline can merge the official AOP/CAIS EOP and legacy ROP contract and annex CSV resources from 2020 onward without changing the public API. Deterministic fixtures remain available for fast local development and tests.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js 22](https://nodejs.org/) with Corepack/pnpm

## Run locally

From the repository root, create the immutable review snapshot:

```powershell
dotnet run --project src/backend/TenderLens.Ingestion -- data/snapshot/tenderlens.db data/manifests/fixture-manifest.json
```

Start the API on the port expected by the frontend:

```powershell
dotnet run --project src/backend/TenderLens.Api --no-launch-profile --urls http://localhost:5080
```

In a second terminal:

```powershell
corepack enable
cd src/frontend
pnpm install --frozen-lockfile
pnpm dev
```

Open `http://localhost:5173` and use one of these fixture EIKs:

- `175074752` — complete profile with five signals and evidence
- `000000019` — sparse profile demonstrating insufficient-data behavior

## Verify

```powershell
dotnet test TenderLens.slnx
cd src/frontend
pnpm run lint
pnpm test -- --run
pnpm run build
```

## Deployment

The included `Dockerfile` builds the frontend, expands the validated immutable historical snapshot, publishes the ASP.NET Core API, and serves the React application from one container. `render.yaml` targets Render's free web-service plan. The snapshot is refreshed by running the importer outside Render because the official portal rejects requests from some cloud build IPs.

## Historical refresh (2020 onward)

Acquisition and ingestion are intentionally separate. First build the current OCDS snapshot. The acquisition script then downloads the 20 pinned official CSV resources for 2020–2025 and checksum-binds both those files and the current snapshot into one local manifest. Raw files and working databases are ignored by Git.

```powershell
dotnet run --project src/backend/TenderLens.Ingestion -- data/snapshot/tenderlens-live.db data/manifests/ocds-live-manifest.json
./scripts/acquire-historical.ps1
dotnet run --project src/backend/TenderLens.Ingestion -- data/snapshot/tenderlens-historical.db data/manifests/historical-2020-current.json
./scripts/package-snapshot.ps1
```

The importer verifies every manifest-bound file before opening the build database. It streams CSV rows into SQLite staging, retains source identity, resolves revisions deterministically, attaches annexes, verifies the result, and only then atomically replaces the published path. A failed or missing required resource leaves the previous snapshot untouched.

`data/manifests/historical-fixture-manifest.json` is a small checksum-bound test of multi-year records, duplicate revisions, multiple supplier identifiers, missing values and amendments. It is not a production-data claim.

The checked release covers 2020-01-01 through 2026-06-30, contains 12,994 supplier profiles and 134,490 published records, and compresses to 63,365,029 bytes (SHA-256 `1f73273c713e3e94f362db727a9c240ab12771541c90b05dd450535b69d553f0`). The Docker build uses `data/releases/tenderlens-historical.db.gz`. Regenerate it with `scripts/package-snapshot.ps1`, record its checksums, then build the image and run the smoke checks below. Do not exclude official records merely to reduce the artifact without explicit approval.

Verified demo EIKs with substantial evidence histories include `103267194`, `203283623`, `131268894`, `102227154`, and `831641528`.

## Data boundary

The application covers suppliers identified in the included public-procurement snapshot. It is not a Commercial Register and does not contain companies without procurement records. Missing monetary values are retained as unavailable, not inferred as zero; incompatible currencies are never summed; absent bid counts and unreliable peer groups produce insufficient-data results.
