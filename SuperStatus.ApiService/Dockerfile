# syntax=docker/dockerfile:1.7
# Build context: repo root.
#   docker build -f SuperStatus.ApiService/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore "SuperStatus.ApiService/SuperStatus.ApiService.csproj"
RUN dotnet publish "SuperStatus.ApiService/SuperStatus.ApiService.csproj" \
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
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "SuperStatus.ApiService.dll"]
