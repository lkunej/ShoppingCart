# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files for restore
COPY src/AuthService/AuthService.csproj src/AuthService/
COPY src/Shared/Shared.csproj src/Shared/

# Restore dependencies
RUN dotnet restore src/AuthService/AuthService.csproj

# Copy source code
COPY src/AuthService/ src/AuthService/
COPY src/Shared/ src/Shared/

# Publish in Release configuration
RUN dotnet publish src/AuthService/AuthService.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "AuthService.dll"]
