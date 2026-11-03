# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:254fe017495996d3abcc487ffacdcce2b73affb380d3af87af12c8dff41fe0b0 AS base
WORKDIR /app
RUN apk add --upgrade --no-cache tzdata
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:cc02b814237a76c4735fb6eeee87181597b5e56f2ce458a435e527674d91f4d7 AS build
ARG BUILD_CONFIGURATION=Release
ARG NUGET_URL=https://nuget.pkg.github.com/Revolutionized-IoT2/index.json
WORKDIR /src
COPY ["RIoT2.Connector.HomeAssistant.csproj", "Directory.Build.props", "Directory.Packages.props", "./"]
# The feed token is the BuildKit secret "nuget_token" and reaches NuGet only through an environment
# variable of this RUN, so no layer stores it:
#   docker build --secret id=nuget_token,env=NUGET_AUTH_TOKEN .
RUN --mount=type=secret,id=nuget_token,required=true \
    dotnet nuget add source "$NUGET_URL" -n riot2 \
    && NuGetPackageSourceCredentials_riot2="Username=riot2;Password=$(cat /run/secrets/nuget_token)" \
       dotnet restore "./RIoT2.Connector.HomeAssistant.csproj"
COPY . .
WORKDIR "/src/."
# The release's version (docker-publish.yml), which the connector announces in its presence (design 7.3).
ARG VERSION=0.0.0-local
RUN dotnet build "./RIoT2.Connector.HomeAssistant.csproj" -c $BUILD_CONFIGURATION --no-restore -o /app/build -p:Version=$VERSION

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
ARG VERSION=0.0.0-local
RUN dotnet publish "./RIoT2.Connector.HomeAssistant.csproj" -c $BUILD_CONFIGURATION --no-restore -o /app/publish /p:UseAppHost=false -p:Version=$VERSION

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
RUN mkdir -p /app/Logs && chown -R app:app /app
USER app
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD wget -q -O- http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["dotnet", "RIoT2.Connector.HomeAssistant.dll"]

ENV TZ=Europe/Helsinki