FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/services/AURA.Analysis.Api/AURA.Analysis.Api.csproj src/services/AURA.Analysis.Api/
RUN dotnet restore src/services/AURA.Analysis.Api/AURA.Analysis.Api.csproj

COPY src/services/AURA.Analysis.Api/ src/services/AURA.Analysis.Api/
RUN dotnet publish src/services/AURA.Analysis.Api/AURA.Analysis.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "AURA.Analysis.Api.dll"]