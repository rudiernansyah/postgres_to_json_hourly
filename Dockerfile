# Gunakan .NET 8 SDK sebagai base image untuk build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Salin file proyek .csproj ke dalam container
COPY ./PostgresReaderApp.csproj ./

# Restore dependencies
RUN dotnet restore

# Salin semua kode proyek
COPY . .

# Build proyek
RUN dotnet publish -c Release -o /app/publish

# Gunakan .NET runtime image yang lebih ringan untuk menjalankan aplikasi
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Jalankan aplikasi
ENTRYPOINT ["dotnet", "PostgresReaderApp.dll"]


