# Stage 1: Build Angular Frontend
FROM node:22-alpine AS node-build
WORKDIR /src/ClientApp
# Copy package files first to cache npm install
COPY ClientApp/package*.json ./
RUN npm ci --legacy-peer-deps
# Copy rest of the frontend source
COPY ClientApp/ ./
# Build Angular app
RUN npm run build -- --configuration production

# Stage 2: Build .NET Backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["AudioOrchestrator.csproj", "./"]
RUN dotnet restore "AudioOrchestrator.csproj"
COPY . .
# Copy compiled Angular assets into wwwroot
COPY --from=node-build /src/ClientApp/dist/audio-orchestrator/browser wwwroot/
RUN dotnet publish "AudioOrchestrator.csproj" -c Release -o /app/publish --no-restore

# Stage 3: Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg=7:6.1.1-3ubuntu5 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5000
ENTRYPOINT ["dotnet", "AudioOrchestrator.dll"]
