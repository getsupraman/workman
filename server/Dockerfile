# syntax=docker/dockerfile:1
# Build context is server/.

# The SPA is built here rather than by the API project's `-p:BuildSpa=true` target, because that
# target would put Node inside the SDK image and make every C# layer depend on the frontend's
# lockfile. Vite's outDir is ../Workman.Api/wwwroot, so the assets land beside this stage's
# frontend/ directory and the build stage copies them in.
# The build stages run on the builder's own platform and cross-target, because dotnet under QEMU
# aborts (exit 134 on restore, measured) when CI builds linux/arm64 on an amd64 runner.
FROM --platform=$BUILDPLATFORM node:24-slim AS spa
WORKDIR /src/frontend

RUN npm install --global pnpm@9.15.4

COPY frontend/package.json frontend/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile

COPY frontend/ ./
RUN pnpm run build

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY Directory.Build.props ./
COPY Workman.Core/Workman.Core.csproj Workman.Core/
COPY Workman.Mcp/Workman.Mcp.csproj Workman.Mcp/
COPY Workman.Api/Workman.Api.csproj Workman.Api/
RUN dotnet restore Workman.Api/Workman.Api.csproj -a $TARGETARCH

COPY . .
COPY --from=spa /src/Workman.Api/wwwroot Workman.Api/wwwroot
RUN dotnet publish Workman.Api/Workman.Api.csproj -c Release -a $TARGETARCH --self-contained false -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    WORKMAN_HEALTHCHECK_URL=http://127.0.0.1:8080/health
EXPOSE 8080

# The app probes itself, so the runtime image needs no curl and the build needs no apt.
HEALTHCHECK --interval=15s --timeout=10s --start-period=20s --retries=3 \
    CMD ["dotnet", "/app/Workman.Api.dll", "--health-check"]

USER $APP_UID
ENTRYPOINT ["dotnet", "Workman.Api.dll"]
