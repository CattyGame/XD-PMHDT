FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/gateway/AURA.ApiGateway/AURA.ApiGateway.csproj src/gateway/AURA.ApiGateway/
RUN dotnet restore src/gateway/AURA.ApiGateway/AURA.ApiGateway.csproj

COPY src/gateway/AURA.ApiGateway/ src/gateway/AURA.ApiGateway/
RUN dotnet publish src/gateway/AURA.ApiGateway/AURA.ApiGateway.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "AURA.ApiGateway.dll"]