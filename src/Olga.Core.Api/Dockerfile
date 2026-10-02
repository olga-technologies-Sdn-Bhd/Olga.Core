FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["NuGet.Config", "Directory.Build.props", "./"]
COPY ["src/Olga.Core.Api/Olga.Core.Api.csproj", "src/Olga.Core.Api/"]
COPY ["src/Olga.Core.Application/Olga.Core.Application.csproj", "src/Olga.Core.Application/"]
COPY ["src/Olga.Core.Contracts/Olga.Core.Contracts.csproj", "src/Olga.Core.Contracts/"]
COPY ["src/Olga.Core.Domain/Olga.Core.Domain.csproj", "src/Olga.Core.Domain/"]
COPY ["src/Olga.Core.Infrastructure/Olga.Core.Infrastructure.csproj", "src/Olga.Core.Infrastructure/"]
RUN dotnet restore "src/Olga.Core.Api/Olga.Core.Api.csproj" --configfile NuGet.Config

COPY . .
RUN dotnet publish "src/Olga.Core.Api/Olga.Core.Api.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN apt-get update \
    && apt-get upgrade -y \
    && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Olga.Core.Api.dll"]
