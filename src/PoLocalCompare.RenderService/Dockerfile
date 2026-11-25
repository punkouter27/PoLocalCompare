# The vision judge's screenshot service. Build context is the repo root:
#   docker build -f src/PoLocalCompare.RenderService/Dockerfile -t polocalcompare-render .
# scripts/deploy-renderer.ps1 builds, pushes and deploys it.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/PoLocalCompare.Rendering/PoLocalCompare.Rendering.csproj src/PoLocalCompare.Rendering/
COPY src/PoLocalCompare.RenderService/PoLocalCompare.RenderService.csproj src/PoLocalCompare.RenderService/
RUN dotnet restore src/PoLocalCompare.RenderService/PoLocalCompare.RenderService.csproj
COPY src/PoLocalCompare.Rendering/ src/PoLocalCompare.Rendering/
COPY src/PoLocalCompare.RenderService/ src/PoLocalCompare.RenderService/
# PoStamp stands in for git, which the build stage does not have.
RUN dotnet publish src/PoLocalCompare.RenderService/PoLocalCompare.RenderService.csproj \
    -c Release -o /app --no-restore -p:PlaywrightPlatform=linux-x64 -p:PoStamp=docker

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    ASPNETCORE_HTTP_PORTS=8080
WORKDIR /app
COPY --from=build /app .
# Chromium (headless shell) plus the OS libraries it needs, matched to the Playwright package.
RUN dotnet PoLocalCompare.RenderService.dll install-browsers \
    && chmod -R a+rX /ms-playwright \
    && chmod -R a+rx /app/.playwright/node \
    && rm -rf /var/lib/apt/lists/*
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "PoLocalCompare.RenderService.dll"]
