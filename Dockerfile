# B_VIP Tool backend — Linux container for Render (or any Docker host).
#
# The desktop client is Windows-only, but the backend is plain ASP.NET Core and runs on Linux.
# Only the backend project and its four dependencies are restored/published; the WPF projects are
# never touched by this build.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore before copying sources so the NuGet layer is reused until a project file changes.
COPY Directory.Build.props ./
COPY src/B_VIP.Core/B_VIP.Core.csproj src/B_VIP.Core/
COPY src/B_VIP.Contracts/B_VIP.Contracts.csproj src/B_VIP.Contracts/
COPY src/B_VIP.Application/B_VIP.Application.csproj src/B_VIP.Application/
COPY src/B_VIP.Infrastructure/B_VIP.Infrastructure.csproj src/B_VIP.Infrastructure/
COPY src/B_VIP.Backend/B_VIP.Backend.csproj src/B_VIP.Backend/
RUN dotnet restore src/B_VIP.Backend/B_VIP.Backend.csproj

COPY src/ src/
RUN dotnet publish src/B_VIP.Backend/B_VIP.Backend.csproj \
        --configuration Release \
        --output /app/publish \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

# Render assigns the public port at run time through PORT and expects a 0.0.0.0 listener.
ENTRYPOINT ["sh", "-c", "export ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080}; exec dotnet B_VIP.Backend.dll"]
