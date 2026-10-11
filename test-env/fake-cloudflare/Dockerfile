# Fake Cloudflare API for the Docker test environment. Build context: repository root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY test-env/fake-cloudflare/ test-env/fake-cloudflare/
RUN dotnet publish test-env/fake-cloudflare -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "TurboManager.FakeCloudflare.dll"]
