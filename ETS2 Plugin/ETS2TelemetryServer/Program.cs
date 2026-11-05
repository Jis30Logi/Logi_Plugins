using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SCSSdkClient;
using SCSSdkClient.Object;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors();

var app = builder.Build();

// Enable CORS
app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

// Initialize SCS SDK Client
var telemetryClient = new SCSSdkTelemetry();
SCSTelemetry? latestData = null;

telemetryClient.Data += (data, newTimestamp) =>
{
    if (newTimestamp)
    {
        latestData = data;
    }
};

// API endpoint
app.MapGet("/api/ets2/telemetry", () =>
{
    if (latestData == null)
    {
        return Results.Json(new { error = "No telemetry data available" }, statusCode: 503);
    }

    var response = new
    {
        game = new
        {
            connected = true,
            gameName = latestData.ScsValues.Game.ToString(),
            paused = latestData.ScsValues.Paused,
            time = latestData.CommonValues.GameTime.Value,
            nextRestStopTime = latestData.CommonValues.NextRestStop.Value
        },
        truck = new
        {
            speed = latestData.TruckValues.CurrentValues.DashboardValues.Speed.Kph,
            fuel = latestData.TruckValues.CurrentValues.DashboardValues.FuelValue.Amount,
            fuelCapacity = latestData.TruckValues.ConstantsValues.CapacityValues.Fuel,
            parkBrake = latestData.TruckValues.CurrentValues.MotorValues.BrakeValues.ParkingBrake,
            wearEngine = latestData.TruckValues.CurrentValues.DamageValues.Engine,
            wearTransmission = latestData.TruckValues.CurrentValues.DamageValues.Transmission,
            wearCabin = latestData.TruckValues.CurrentValues.DamageValues.Cabin,
            wearChassis = latestData.TruckValues.CurrentValues.DamageValues.Chassis,
            wearWheels = latestData.TruckValues.CurrentValues.DamageValues.WheelsAvg
        }
    };

    return Results.Json(response);
});

// Root endpoint
app.MapGet("/", () => Results.Content(@"
<!DOCTYPE html>
<html>
<head>
    <title>ETS2 Telemetry Server</title>
    <style>
        body { font-family: Arial; margin: 40px; background: #1e1e1e; color: #fff; }
        h1 { color: #4CAF50; }
        .status { padding: 20px; background: #2d2d2d; border-radius: 8px; margin: 20px 0; }
        .data { font-family: monospace; background: #000; padding: 15px; border-radius: 4px; overflow-x: auto; }
        .green { color: #4CAF50; }
        .red { color: #f44336; }
    </style>
</head>
<body>
    <h1>🚛 ETS2 Telemetry Server</h1>
    <div class='status'>
        <h2>Status: <span class='green'>Running</span></h2>
        <p>Server is listening on <strong>http://localhost:25555</strong></p>
        <p>API Endpoint: <a href='/api/ets2/telemetry' style='color: #4CAF50;'>/api/ets2/telemetry</a></p>
    </div>
    <div class='status'>
        <h3>Instructions:</h3>
        <ol>
            <li>Launch Euro Truck Simulator 2</li>
            <li>Start driving (telemetry only works in-game, not in menu)</li>
            <li>Your Logitech MX Creative Console indicators will update automatically</li>
        </ol>
    </div>
    <div class='status'>
        <h3>Test the API:</h3>
        <p>Click the API endpoint link above to see live telemetry data in JSON format</p>
    </div>
</body>
</html>
", "text/html"));

Console.WriteLine("========================================");
Console.WriteLine("  ETS2 Telemetry HTTP Server");
Console.WriteLine("========================================");
Console.WriteLine();
Console.WriteLine("✅ Server started successfully!");
Console.WriteLine($"🌐 Listening on: http://localhost:25555");
Console.WriteLine($"📡 API Endpoint: http://localhost:25555/api/ets2/telemetry");
Console.WriteLine();
Console.WriteLine("📝 Instructions:");
Console.WriteLine("   1. Launch Euro Truck Simulator 2");
Console.WriteLine("   2. Start driving (telemetry only works in-game)");
Console.WriteLine("   3. Your Logitech indicators will update automatically");
Console.WriteLine();
Console.WriteLine("Press Ctrl+C to stop the server");
Console.WriteLine();

app.Run("http://localhost:25555");
