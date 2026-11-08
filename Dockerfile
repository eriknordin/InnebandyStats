FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

COPY *.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# Datalagret (SQLite) – montera en volym här så att datan överlever nya versioner
ENV Storage__Path=/data/innebandystats.db
VOLUME /data

ENTRYPOINT ["dotnet", "InnebandyStats.dll"]
