# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /src

COPY backend/AnonPulse.Api/AnonPulse.Api.csproj backend/AnonPulse.Api/
RUN dotnet restore backend/AnonPulse.Api/AnonPulse.Api.csproj

COPY . .
RUN dotnet publish backend/AnonPulse.Api/AnonPulse.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

COPY --from=build --chown=app:app /app/publish .

USER app
ENTRYPOINT ["dotnet", "AnonPulse.Api.dll"]
