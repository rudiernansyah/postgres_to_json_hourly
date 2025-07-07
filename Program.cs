using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using DotNetEnv;

class Program
{
    static async Task Main()
    {
        while (true)
        {
            try
            {
                Console.WriteLine($"[Main Start] {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                Env.Load();
                Console.WriteLine("Waiting 10 seconds before connecting database...");
                await Task.Delay(10000); // Menunggu 10 detik sebelum memuat .env

                await SetTimezone();
                await EnsureTableExists();
                //await DeleteOldRecords();
                await ExportDataToJson();

                while (true)
                {
                    await WaitForNextInterval();
                    //await DeleteOldRecords();
                    await ExportDataToJson();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] {DateTime.Now:yyyy-MM-dd HH:mm:ss} FATAL ERROR: {ex.Message}");
                Console.WriteLine("Restarting the application in 15 seconds...");
                await Task.Delay(15000); // Tunggu sebelum ulang agar tidak crash loop
            }
        }
    }

    static async Task SetTimezone()
    {
        string? connectionString = GetConnectionString();
        if (string.IsNullOrEmpty(connectionString))
        {
            Console.WriteLine("Database connection string is missing.");
            return;
        }

        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            // Set timezone to Asia/Jakarta di level database
            using (var cmd = new NpgsqlCommand("ALTER DATABASE postgres_reckit SET TIMEZONE = 'Asia/Jakarta';", conn))
            {
                await cmd.ExecuteNonQueryAsync();
                Console.WriteLine("The timezone for the database 'postgres_reckit' has been successfully set to 'Asia/Jakarta'.");
            }

            // Set timezone to Asia/Jakarta di level role
            using (var cmd = new NpgsqlCommand("ALTER ROLE postgres SET TIMEZONE = 'Asia/Jakarta';", conn))
            {
                await cmd.ExecuteNonQueryAsync();
                Console.WriteLine("The timezone for the role 'postgres' has been successfully set to 'Asia/Jakarta'.");
            }

            // Pastikan timezone yang diterapkan
            using (var cmd = new NpgsqlCommand("SHOW timezone;", conn))
            {
                var timezone = (string)await cmd.ExecuteScalarAsync();
                Console.WriteLine($"Applied timezone: {timezone}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error setting timezone: {ex.Message}");
        }
    }

    static async Task EnsureTableExists()
    {
        string? connectionString = GetConnectionString();
        if (string.IsNullOrEmpty(connectionString))
        {
            Console.WriteLine("Database connection string is missing.");
            return;
        }

        string createTableQuery = @"
        CREATE TABLE IF NOT EXISTS hourly_good_production (
            created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
            date_time TIMESTAMP NOT NULL,
            plant_name VARCHAR(20) NOT NULL,
            line_name VARCHAR(20) NOT NULL,
            output_1 INT NOT NULL CHECK (output_1 >= 0),
            output_2 INT NOT NULL CHECK (output_2 >= 0),
            total_output INT NOT NULL CHECK (total_output >= 0),
            record_status INT DEFAULT 0
        );

        -- Cek apakah tabel sudah menjadi hypertable
        DO $$ 
        DECLARE 
            is_hypertable BOOLEAN;
        BEGIN
            SELECT EXISTS (
                SELECT 1 FROM timescaledb_information.hypertables 
                WHERE hypertable_name = 'hourly_good_production'
            ) INTO is_hypertable;

            -- Jika belum hypertable, baru jalankan perintah create_hypertable
            IF NOT is_hypertable THEN
                PERFORM create_hypertable('hourly_good_production', 'date_time',
                                        chunk_time_interval => INTERVAL '3 months');
            END IF;
        END $$;
        ";

        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            await using var createTableCmd = new NpgsqlCommand(createTableQuery, conn);
            await createTableCmd.ExecuteNonQueryAsync();
            Console.WriteLine("Table hourly_good_production checked/created successfully with hypertable setup.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error ensuring table exists: {ex.Message}");
        }
    }


    static async Task WaitForNextInterval()
    {
        DateTime now = DateTime.Now;
        DateTime nextInterval = new DateTime(now.Year, now.Month, now.Day, now.Hour, 1, 0);
        if (now > nextInterval)
        {
            nextInterval = nextInterval.AddHours(1);
        }
        Console.WriteLine($"Waiting for {nextInterval} to create data.json");
        await Task.Delay(nextInterval - now);
    }

    static async Task DeleteOldRecords()
    {
        string? connectionString = GetConnectionString();
        if (string.IsNullOrEmpty(connectionString))
        {
            Console.WriteLine("Database connection string is missing.");
            return;
        }

        string deleteQuery = @"
        DELETE FROM hourly_good_production 
        WHERE created_at < NOW() - INTERVAL '2 years';";

        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(deleteQuery, conn);
            int rowsAffected = await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"{rowsAffected} old records deleted.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error deleting old records: {ex.Message}");
        }
    }


    static async Task ExportDataToJson()
    {
        string? connectionString = GetConnectionString();
        if (string.IsNullOrEmpty(connectionString))
        {
            Console.WriteLine("Database connection string is missing.");
            return;
        }

        string query = @"SELECT date_time, plant_name, line_name, output_1, output_2, total_output 
                          FROM hourly_good_production 
                          WHERE date_time = (SELECT MAX(date_time) FROM hourly_good_production)
                          ORDER BY created_at ASC, date_time ASC";

        string outputFolder = "reckit_json";
        string outputFile = Path.Combine(outputFolder, "data.json");

        List<ProductionData> data = new();

        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(query, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                data.Add(new ProductionData
                {
                    date_time = reader.GetDateTime(0),
                    plant_name = reader.GetString(1),
                    line_name = reader.GetString(2),
                    output_1 = reader.GetInt32(3),
                    output_2 = reader.GetInt32(4),
                    total_output = reader.GetInt32(5)
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return;
        }

        var formattedData = new
        {
            data = data
            .Select((d, index) => new { d, index }) // Menambahkan index ke setiap elemen
            .OrderBy(x => x.d.line_name == "Mespack BH" ? -1 : x.index) // Pindahkan "Mespack BH" ke depan
            .Select(x => new
            {
                date_time = x.d.date_time.ToString("yyyy-MM-dd HH:mm:ss"),
                x.d.plant_name,
                x.d.line_name,
                x.d.output_1,
                x.d.output_2,
                x.d.total_output
            })
            .ToList()
        };


        string json = JsonSerializer.Serialize(formattedData, new JsonSerializerOptions { WriteIndented = true });

        if (!Directory.Exists(outputFolder))
        {
            Directory.CreateDirectory(outputFolder);
        }

        await File.WriteAllTextAsync(outputFile, json);
        Console.WriteLine($"Data exported to {outputFile}");
    }

    static string? GetConnectionString()
    {
        string? host = Environment.GetEnvironmentVariable("DATABASE_HOST");
        string? port = Environment.GetEnvironmentVariable("DATABASE_PORT");
        string? username = Environment.GetEnvironmentVariable("DATABASE_USERNAME");
        string? password = Environment.GetEnvironmentVariable("DATABASE_PASSWORD");
        string? database = Environment.GetEnvironmentVariable("DATABASE_NAME");

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(port) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(database))
        {
            return null;
        }
        return $"Host={host};Port={port};Username={username};Password={password};Database={database}";
    }
}

class ProductionData
{
    public DateTime date_time { get; set; }
    public required string plant_name { get; set; }
    public required string line_name { get; set; }
    public int output_1 { get; set; }
    public int output_2 { get; set; }
    public int total_output { get; set; }
}
