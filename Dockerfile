FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/FileService.Contracts/FileService.Contracts.csproj src/FileService.Contracts/
COPY src/FileService.Api/FileService.Api.csproj src/FileService.Api/
RUN dotnet restore src/FileService.Api/FileService.Api.csproj

COPY src/FileService.Contracts/ src/FileService.Contracts/
COPY src/FileService.Api/ src/FileService.Api/
RUN dotnet publish src/FileService.Api/FileService.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/jjeandervin/file-service" \
      org.opencontainers.image.title="file-service" \
      org.opencontainers.image.description="Shared internal file service: presigned S3 uploads and downloads." \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION"

WORKDIR /app
COPY --from=build --chown=app:app /app/publish .

USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "FileService.Api.dll"]
