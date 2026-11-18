# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Permits.Api/Permits.Api.csproj src/Permits.Api/
RUN dotnet restore src/Permits.Api/Permits.Api.csproj
COPY src/ src/
RUN dotnet publish src/Permits.Api/Permits.Api.csproj -c Release -o /app --no-restore

# Run as the image's built-in non-root user; ASP.NET Core listens on 8080
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY data/sample-permits.json data/
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Permits.Api.dll"]
