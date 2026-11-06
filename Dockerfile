FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/SlaTracker.Api/SlaTracker.Api.csproj src/SlaTracker.Api/
RUN dotnet restore src/SlaTracker.Api/SlaTracker.Api.csproj
COPY src/ src/
RUN dotnet publish src/SlaTracker.Api/SlaTracker.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SlaTracker.Api.dll"]
