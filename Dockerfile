FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/OpenNet.Server/OpenNet.Server/OpenNet.Server.csproj OpenNet.Server/
RUN dotnet restore OpenNet.Server/OpenNet.Server.csproj

COPY src/OpenNet.Server/OpenNet.Server/ OpenNet.Server/
RUN dotnet publish OpenNet.Server/OpenNet.Server.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "OpenNet.Server.dll"]
