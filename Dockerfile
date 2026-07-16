# Networthy — the self-contained product image: Plenipo platform (vendored packages) +
# finance module + Plaid connector + the embedded branded web UI. Build context is the
# repository root:
#   docker build -t networthy .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore from the vendored feed first, projects-only, for layer caching.
COPY global.json nuget.config ./
COPY .packages/ .packages/
COPY src/Networthy.Finance/Networthy.Finance.csproj                 src/Networthy.Finance/
COPY src/Networthy.Connectors.Plaid/Networthy.Connectors.Plaid.csproj src/Networthy.Connectors.Plaid/
COPY src/Networthy.Host/Networthy.Host.csproj                       src/Networthy.Host/
RUN dotnet restore src/Networthy.Host/Networthy.Host.csproj

# Copy the sources and publish; wwwroot (the embedded branded UI) rides along as content.
COPY src/ src/
RUN dotnet publish src/Networthy.Host/Networthy.Host.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
# Pull in security updates that may land between Microsoft base-image refreshes. No package
# manager metadata remains in the shipped layer.
RUN apt-get update \
    && apt-get upgrade -y \
    && rm -rf /var/lib/apt/lists/*
# Run as the built-in non-root user provided by the aspnet image.
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Networthy.Host.dll"]
