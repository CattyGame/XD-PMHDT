FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/common/AURA.Common.Audit/ src/common/AURA.Common.Audit/

COPY src/services/AURA.Notification.Api/AURA.Notification.Api.csproj \
     src/services/AURA.Notification.Api/

COPY src/services/AURA.Notification.Api/packages.lock.json \
     src/services/AURA.Notification.Api/

RUN dotnet restore \
    src/services/AURA.Notification.Api/AURA.Notification.Api.csproj \
    --locked-mode

COPY src/services/AURA.Notification.Api/ \
     src/services/AURA.Notification.Api/

RUN dotnet publish \
    src/services/AURA.Notification.Api/AURA.Notification.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080

COPY --from=build /app/publish .

USER $APP_UID

EXPOSE 8080

ENTRYPOINT ["dotnet", "AURA.Notification.Api.dll"]
