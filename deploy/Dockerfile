# The two server images as two targets of one build, so they share the restore and the compile of
# the projects both reference. The build context is the repository root, which .dockerignore
# keeps to the server sources and the content they copy:
#
#   docker build -f deploy/Dockerfile --target game-service -t shooter-mmo/game-service:local .
#   docker build -f deploy/Dockerfile --target simulation-worker -t shooter-mmo/simulation-worker:local .
#
# Every image tag is pinned to a version, and Dependabot proposes the newer ones. The SDK stays on
# the feature band of global.json, and the runtime images on the newest patch of the .NET version
# that the projects target; change the three together. docs/DEPLOYMENT.md describes the images.

# The context, once, so the stages below copy from one place.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS sources
WORKDIR /src
COPY . .

# Only the files that decide the restore, so that its layer stays cached while only code changes.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS restore-inputs
COPY --from=sources /src /src
RUN find /src -type f ! \( -name '*.csproj' -o -name 'packages.lock.json' -o -name '*.props' \
        -o -name '*.targets' -o -name 'global.json' \) -delete

FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
ENV DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true
WORKDIR /src
COPY --from=restore-inputs /src ./
RUN dotnet restore src/Server/GameService/Mmorpg.GameService.csproj --locked-mode \
    && dotnet restore src/Server/SimulationWorker/Mmorpg.SimulationWorker.csproj --locked-mode
COPY --from=sources /src ./
# Framework-dependent and portable: the runtime images provide .NET, and `dotnet <dll>` starts each
# service. The images run outside Development only, so the Development settings and the
# development item packages stay out, and with them any way to start Development with its
# wide-open settings.
RUN dotnet publish src/Server/GameService/Mmorpg.GameService.csproj --configuration Release \
        --no-restore --output /out/game-service -p:UseAppHost=false \
    && dotnet publish src/Server/SimulationWorker/Mmorpg.SimulationWorker.csproj --configuration Release \
        --no-restore --output /out/simulation-worker -p:UseAppHost=false \
    && rm -rf /out/game-service/Config/appsettings.Development.json /out/game-service/Content \
        /out/game-service/web.config /out/simulation-worker/Config/appsettings.Development.json

# The chiseled runtime images have no shell and no package manager, no ICU, so globalization is
# invariant, and they run as the user app, never as root. Their health check is the service's own
# `probe` command, which the Compose file names.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble-chiseled AS game-service
WORKDIR /app
COPY --from=build /out/game-service ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mmorpg.GameService.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0.15-noble-chiseled AS simulation-worker
WORKDIR /app
COPY --from=build /out/simulation-worker ./
USER $APP_UID
EXPOSE 27015/udp
ENTRYPOINT ["dotnet", "Mmorpg.SimulationWorker.dll"]
