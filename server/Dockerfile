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
