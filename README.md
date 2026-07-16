# TenderLens

TenderLens is an explainable anomaly-detection demo for Bulgarian public procurement. Enter a Bulgarian company EIK to inspect a procurement-derived supplier profile, five deterministic signals, their evidence, peer context, and the source records behind them.

The deployed container builds an immutable snapshot from the publicly discoverable OCDS JSON resources published by the Bulgarian Public Procurement Agency. A deterministic synthetic fixture remains available for fast local development and tests.

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

The included `Dockerfile` builds the frontend, imports the official CAIS EOP OCDS resources, publishes the ASP.NET Core API, and serves the React application from one container. `render.yaml` targets Render's free web-service plan.

## Data boundary

The public deployment covers suppliers present in the OCDS award/contract releases currently published by AOP. It is not a Commercial Register and does not contain companies without procurement records. Missing or semantically unreliable OCDS fields are shown as insufficient data rather than inferred.
