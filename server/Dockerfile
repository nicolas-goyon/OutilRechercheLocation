# Image du serveur local. Commande : docker compose up (voir docker-compose.yml à la racine).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/RechercheLogement.Core/RechercheLogement.Core.csproj src/RechercheLogement.Core/
COPY src/RechercheLogement.Server/RechercheLogement.Server.csproj src/RechercheLogement.Server/
RUN dotnet restore src/RechercheLogement.Server/RechercheLogement.Server.csproj
COPY src/ src/
ARG VERSION=0.0.0-local
RUN dotnet publish src/RechercheLogement.Server/RechercheLogement.Server.csproj -c Release -o /app --no-restore -p:Version=${VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=0.0.0-local
# source : relie l'image GHCR au dépôt GitHub. title : sert au nettoyage des anciennes images (deploy/update.*).
LABEL org.opencontainers.image.title="recherche-logement-server" \
      org.opencontainers.image.description="Serveur local de suivi de recherche logement (API du plugin + pages web)" \
      org.opencontainers.image.source="https://github.com/nicolas-goyon/OutilRechercheLocation" \
      org.opencontainers.image.version="${VERSION}"
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    RechercheLogement__DatabasePath=/data/recherche-logement.db \
    TZ=Europe/Paris
RUN mkdir -p /data && chown app:app /data
VOLUME /data
COPY --from=build /app .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "RechercheLogement.Server.dll"]
