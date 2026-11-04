FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/KnowledgeHub.Api/KnowledgeHub.Api.csproj -c Release -o /app

# One-shot migration runner (Doc 02 §7.4): `docker compose` service migrate-core builds this target.
# It applies EF Core migrations as core_migrator and exits. Pass the connection in CORE_MIGRATOR_CONNECTION.
FROM build AS migrate
RUN dotnet tool install --global dotnet-ef --version 10.0.*
ENV PATH="$PATH:/root/.dotnet/tools"
RUN dotnet ef migrations bundle --project src/KnowledgeHub.Infrastructure --output /efbundle --force
ENTRYPOINT ["/bin/sh", "-c", "/efbundle --connection \"$CORE_MIGRATOR_CONNECTION\""]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "KnowledgeHub.Api.dll"]
