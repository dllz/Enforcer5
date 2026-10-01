FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG CONFIG=Release
WORKDIR /src

# Project files first so the restore layer caches independently of source changes. Restoring the
# test project restores the app it references too.
COPY Directory.Build.props global.json ./
COPY src/Enforcer5/Enforcer5.csproj src/Enforcer5/
COPY tests/Enforcer5.Tests/Enforcer5.Tests.csproj tests/Enforcer5.Tests/
RUN dotnet restore tests/Enforcer5.Tests/Enforcer5.Tests.csproj

COPY . .
# The unit tests gate the image: a failure stops the build before anything is published, so CI
# pushes nothing and DeployBot is never notified. They run in seconds, here rather than in a CI
# job of their own, which would cost a separately billed runner on every push. Each config is
# tested as it is built, so the PREMIUM code paths are covered by the premium image.
RUN dotnet test tests/Enforcer5.Tests/Enforcer5.Tests.csproj -c "$CONFIG" --no-restore
RUN dotnet publish src/Enforcer5/Enforcer5.csproj -c "$CONFIG" --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .

# In production this image is only an artifact: DeployBot docker-cp's /app out to disk and the
# bot runs under systemd. The entrypoint is kept correct so the image is runnable for testing.
ENTRYPOINT ["dotnet", "Enforcer.dll"]
