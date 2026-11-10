FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

# Project files first, so package restore stays cached until a dependency changes.
COPY global.json Directory.Build.props ./
COPY src/Ticketing.Contracts/Ticketing.Contracts.csproj src/Ticketing.Contracts/
COPY src/Ticketing.Api/Ticketing.Api.csproj src/Ticketing.Api/
RUN dotnet restore src/Ticketing.Api/Ticketing.Api.csproj

COPY src/ src/
RUN dotnet publish src/Ticketing.Api/Ticketing.Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ticketing.Api.dll"]
