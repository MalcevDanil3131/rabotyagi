FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["rabotyagi/rabotyagi.csproj", "rabotyagi/"]
RUN dotnet restore "rabotyagi/rabotyagi.csproj"
COPY . .
WORKDIR "/src/rabotyagi"
RUN dotnet build "./rabotyagi.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./rabotyagi.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
USER root
RUN mkdir -p /app/uploads /app/keys && chown -R $APP_UID:$APP_UID /app/uploads /app/keys
USER $APP_UID
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "rabotyagi.dll"]
