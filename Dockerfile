FROM node:22-alpine AS web
WORKDIR /src
RUN corepack enable
COPY src/frontend/package.json src/frontend/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY src/frontend/ ./
RUN pnpm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore TenderLens.slnx --configfile NuGet.Config && dotnet run --project src/backend/TenderLens.Ingestion -- /tmp/tenderlens.db data/manifests/fixture-manifest.json && dotnet publish src/backend/TenderLens.Api -c Release -o /app/publish
RUN cp /tmp/tenderlens.db /app/publish/tenderlens.db

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=web /src/dist ./wwwroot
ENV ASPNETCORE_URLS=http://0.0.0.0:10000
ENV SnapshotPath=/app/tenderlens.db
EXPOSE 10000
ENTRYPOINT ["dotnet", "TenderLens.Api.dll"]
