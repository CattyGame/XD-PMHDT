FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/services/AURA.Billing.Api/AURA.Billing.Api.csproj \
     src/services/AURA.Billing.Api/

COPY src/services/AURA.Billing.Api/packages.lock.json \
     src/services/AURA.Billing.Api/

RUN dotnet restore \
    src/services/AURA.Billing.Api/AURA.Billing.Api.csproj \
    --locked-mode

COPY src/services/AURA.Billing.Api/ \
     src/services/AURA.Billing.Api/

RUN dotnet publish \
    src/services/AURA.Billing.Api/AURA.Billing.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080

COPY --from=build /app/publish .

USER $APP_UID

EXPOSE 8080

ENTRYPOINT ["dotnet", "AURA.Billing.Api.dll"]