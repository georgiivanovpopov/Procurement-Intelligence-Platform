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
RUN dotnet restore TenderLens.slnx --configfile NuGet.Config && dotnet publish src/backend/TenderLens.Api -c Release -o /app/publish
RUN echo "1f73273c713e3e94f362db727a9c240ab12771541c90b05dd450535b69d553f0  data/releases/tenderlens-historical.db.gz" | sha256sum -c -
RUN gzip -dc data/releases/tenderlens-historical.db.gz > /app/publish/tenderlens.db

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=web /src/dist ./wwwroot
ENV ASPNETCORE_URLS=http://0.0.0.0:10000
ENV SnapshotPath=/app/tenderlens.db
ENV DOTNET_USE_POLLING_FILE_WATCHER=1
ENV DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false
EXPOSE 10000
ENTRYPOINT ["dotnet", "TenderLens.Api.dll"]
