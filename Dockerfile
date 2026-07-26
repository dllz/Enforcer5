FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG CONFIG=Release
WORKDIR /src

# Project file first so the restore layer caches independently of source changes.
COPY Directory.Build.props global.json ./
COPY src/Enforcer5/Enforcer5.csproj src/Enforcer5/
RUN dotnet restore src/Enforcer5/Enforcer5.csproj

COPY . .
RUN dotnet publish src/Enforcer5/Enforcer5.csproj -c "$CONFIG" --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .

# In production this image is only an artifact: DeployBot docker-cp's /app out to disk and the
# bot runs under systemd. The entrypoint is kept correct so the image is runnable for testing.
ENTRYPOINT ["dotnet", "Enforcer.dll"]
