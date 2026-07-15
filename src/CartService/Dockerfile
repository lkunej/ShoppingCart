# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files for restore
COPY src/CartService/CartService.csproj src/CartService/
COPY src/Shared/Shared.csproj src/Shared/

# Restore dependencies
RUN dotnet restore src/CartService/CartService.csproj

# Copy source code
COPY src/CartService/ src/CartService/
COPY src/Shared/ src/Shared/

# Publish in Release configuration
RUN dotnet publish src/CartService/CartService.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "CartService.dll"]
