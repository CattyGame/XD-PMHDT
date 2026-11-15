FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/common/AURA.Common.Cache/AURA.Common.Cache.csproj src/common/AURA.Common.Cache/
COPY src/services/Aura.Worker.Analysis/Aura.Worker.Analysis.csproj src/services/Aura.Worker.Analysis/
RUN dotnet restore src/services/Aura.Worker.Analysis/Aura.Worker.Analysis.csproj

COPY src/common/AURA.Common.Cache/ src/common/AURA.Common.Cache/
COPY src/services/Aura.Worker.Analysis/ src/services/Aura.Worker.Analysis/
RUN dotnet publish src/services/Aura.Worker.Analysis/Aura.Worker.Analysis.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Aura.Worker.Analysis.dll"]

