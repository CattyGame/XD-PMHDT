FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/common/AURA.Common.Audit/ src/common/AURA.Common.Audit/

COPY src/services/AURA.Profile.Api/AURA.Profile.Api.csproj \
     src/services/AURA.Profile.Api/

RUN dotnet restore \
    src/services/AURA.Profile.Api/AURA.Profile.Api.csproj

COPY src/services/AURA.Profile.Api/ \
     src/services/AURA.Profile.Api/

RUN dotnet publish \
    src/services/AURA.Profile.Api/AURA.Profile.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Development
ENV ASPNETCORE_HTTP_PORTS=8080

COPY --from=build /app/publish .

USER $APP_UID

EXPOSE 8080

ENTRYPOINT ["dotnet", "AURA.Profile.Api.dll"]
