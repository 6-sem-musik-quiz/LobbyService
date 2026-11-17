FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so this layer is cached until a csproj changes
COPY global.json Directory.Build.props ./
COPY src/LobbyService.Api/LobbyService.Api.csproj src/LobbyService.Api/
COPY src/LobbyService.Service/LobbyService.Service.csproj src/LobbyService.Service/
COPY src/LobbyService.Repository/LobbyService.Repository.csproj src/LobbyService.Repository/
COPY src/LobbyService.Domain/LobbyService.Domain.csproj src/LobbyService.Domain/
RUN dotnet restore src/LobbyService.Api/LobbyService.Api.csproj

COPY src/ src/
RUN dotnet publish src/LobbyService.Api/LobbyService.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "LobbyService.Api.dll"]
