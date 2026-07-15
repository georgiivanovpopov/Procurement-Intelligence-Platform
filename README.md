# TenderLens

TenderLens is an explainable anomaly-detection demo for Bulgarian public procurement. Enter a Bulgarian company EIK to inspect a procurement-derived supplier profile, five deterministic signals, their evidence, peer context, and the source records behind them.

The repository currently uses a deterministic synthetic CAIS EOP-shaped fixture. It demonstrates the complete product journey without making claims about real companies.

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

The included `Dockerfile` builds the frontend, creates the deterministic snapshot, publishes the ASP.NET Core API, and serves the React application from one container. `render.yaml` targets Render's free web-service plan.

## Data boundary

All displayed fixture values are synthetic. Before using TenderLens for real procurement analysis, replace the fixture acquisition manifest and ingestion input with checksum-bound public CAIS EOP exports while preserving provenance and snapshot immutability.
