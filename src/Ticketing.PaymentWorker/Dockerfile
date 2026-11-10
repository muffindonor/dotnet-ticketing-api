FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

# Project files first, so package restore stays cached until a dependency changes.
COPY global.json Directory.Build.props ./
COPY src/Ticketing.Contracts/Ticketing.Contracts.csproj src/Ticketing.Contracts/
COPY src/Ticketing.PaymentWorker/Ticketing.PaymentWorker.csproj src/Ticketing.PaymentWorker/
RUN dotnet restore src/Ticketing.PaymentWorker/Ticketing.PaymentWorker.csproj

COPY src/ src/
RUN dotnet publish src/Ticketing.PaymentWorker/Ticketing.PaymentWorker.csproj -c Release --no-restore -o /app

# The worker serves no HTTP, so it uses the smaller runtime image.
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Ticketing.PaymentWorker.dll"]
