FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY sql-mcp.csproj .
RUN dotnet restore sql-mcp.csproj
COPY . .
RUN dotnet publish sql-mcp.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_ENVIRONMENT=Production
ENV App__Urls=http://*:8080
EXPOSE 8080
# The base image's unprivileged "app" user; 8080 needs no root.
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=10s --start-period=10s --retries=3 \
    CMD ["dotnet", "sql-mcp.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "sql-mcp.dll"]
