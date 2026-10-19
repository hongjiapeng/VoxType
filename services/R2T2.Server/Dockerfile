FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY services/R2T2.Server/ services/R2T2.Server/
COPY src/VoxType.R2T2.Core/ src/VoxType.R2T2.Core/
RUN dotnet publish services/R2T2.Server/R2T2.Server.csproj -c Release --no-self-contained -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "R2T2.Server.dll"]
