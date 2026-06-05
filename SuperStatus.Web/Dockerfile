# syntax=docker/dockerfile:1.7
# Build context: repo root.
#   docker build -f SuperStatus.Web/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore "SuperStatus.Web/SuperStatus.Web.csproj"
RUN dotnet publish "SuperStatus.Web/SuperStatus.Web.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false \
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
