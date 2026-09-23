FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["NuGet.Config", "Directory.Build.props", "./"]
COPY ["src/Olga.Core.Worker/Olga.Core.Worker.csproj", "src/Olga.Core.Worker/"]
COPY ["src/Olga.Core.Infrastructure/Olga.Core.Infrastructure.csproj", "src/Olga.Core.Infrastructure/"]
COPY ["src/Olga.Core.Application/Olga.Core.Application.csproj", "src/Olga.Core.Application/"]
COPY ["src/Olga.Core.Contracts/Olga.Core.Contracts.csproj", "src/Olga.Core.Contracts/"]
COPY ["src/Olga.Core.Domain/Olga.Core.Domain.csproj", "src/Olga.Core.Domain/"]
RUN dotnet restore "src/Olga.Core.Worker/Olga.Core.Worker.csproj" --configfile NuGet.Config

COPY . .
RUN dotnet publish "src/Olga.Core.Worker/Olga.Core.Worker.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Olga.Core.Worker.dll"]
