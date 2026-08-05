# syntax=docker/dockerfile:1.7
# Build context: repo root.
#   docker build -f SuperStatus.Web/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
# Release version stamped into the assembly (issue #249) — release-images.yml
# passes the tag / `edge`; defaults to the dev sentinel for a plain build.
# NB: do NOT name this VERSION. BuildKit exposes build args as env vars to
# RUN, and MSBuild reads env `VERSION` as $(Version) (names are case-insensitive),
# which NuGet then validates as the package version during restore. `edge` is
# not valid SemVer, so restore aborts. A non-colliding name keeps the value
# free-form and confined to InformationalVersion.
ARG SUPERSTATUS_VERSION=0.0.0-dev
COPY . .
RUN dotnet restore "SuperStatus.Web/SuperStatus.Web.csproj"
RUN dotnet publish "SuperStatus.Web/SuperStatus.Web.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false \
    /p:InformationalVersion="${SUPERSTATUS_VERSION}" \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish ./
COPY --from=build /src/locales ./locales
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "SuperStatus.Web.dll"]
