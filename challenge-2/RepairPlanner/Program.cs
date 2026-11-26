using System.Text.Json;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepairPlanner;
using RepairPlanner.Models;
using RepairPlanner.Services;

// ---------------------------------------------------------------------------
// Configuration from environment variables (load the repo-root .env first)
// ---------------------------------------------------------------------------
string[] required = ["AZURE_AI_PROJECT_ENDPOINT", "MODEL_DEPLOYMENT_NAME", "COSMOS_ENDPOINT", "COSMOS_KEY", "COSMOS_DATABASE_NAME"];
var missing = required.Where(n => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(n))).ToList();
if (missing.Count > 0)
{
    Console.Error.WriteLine($"Missing environment variables: {string.Join(", ", missing)}");
    Console.Error.WriteLine("Load them first, e.g.: set -a && . ../../.env && set +a");
    return 1;
}

var projectEndpoint = Environment.GetEnvironmentVariable("AZURE_AI_PROJECT_ENDPOINT")!;
var modelDeploymentName = Environment.GetEnvironmentVariable("MODEL_DEPLOYMENT_NAME")!;

// ---------------------------------------------------------------------------
// Dependency injection (a container that builds and wires up our services)
// ---------------------------------------------------------------------------
var services = new ServiceCollection();

services.AddLogging(builder => builder
    .AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    })
    .SetMinimumLevel(LogLevel.Information)
    // Hide verbose HTTP/Azure SDK logs
    .AddFilter("System.Net.Http", LogLevel.Warning)
    .AddFilter("Azure", LogLevel.Warning));

services.AddSingleton(new CosmosDbOptions
{
    Endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")!,
    Key = Environment.GetEnvironmentVariable("COSMOS_KEY")!,
    DatabaseName = Environment.GetEnvironmentVariable("COSMOS_DATABASE_NAME")!,
});
services.AddSingleton<CosmosDbService>();
services.AddSingleton<IFaultMappingService, FaultMappingService>();

// DefaultAzureCredential picks up the `az login` session inside the dev container
services.AddSingleton(_ => new AIProjectClient(new Uri(projectEndpoint), new DefaultAzureCredential()));

services.AddSingleton(sp => new RepairPlannerAgent(
    sp.GetRequiredService<AIProjectClient>(),
    sp.GetRequiredService<CosmosDbService>(),
    sp.GetRequiredService<IFaultMappingService>(),
    modelDeploymentName,
    sp.GetRequiredService<ILogger<RepairPlannerAgent>>()));

// await using - disposes the provider (and CosmosClient) at the end, like Python's "async with"
await using var provider = services.BuildServiceProvider();
var logger = provider.GetRequiredService<ILogger<Program>>();

// ---------------------------------------------------------------------------
// Sample fault (same shape as the Fault Diagnosis Agent output in Challenge 1)
// ---------------------------------------------------------------------------
var fault = new DiagnosedFault
{
    MachineId = "machine-001",
    FaultType = "curing_temperature_excessive",
    RootCause = "Heating element malfunction",
    Severity = "High",
    DetectedAt = DateTimeOffset.UtcNow,
    Metadata = new()
    {
        ["observedCuringTemperature"] = 179.2,
        ["warningThreshold"] = 178,
        ["machineType"] = "tire_curing_press",
    },
};

try
{
    var agent = provider.GetRequiredService<RepairPlannerAgent>();
    await agent.EnsureAgentVersionAsync();

    var workOrder = await agent.PlanAndCreateWorkOrderAsync(fault);

    logger.LogInformation(
        "Saved work order {WorkOrderNumber} (id={Id}, status={Status}, assignedTo={AssignedTo})",
        workOrder.WorkOrderNumber, workOrder.Id, workOrder.Status, workOrder.AssignedTo ?? "unassigned");

    Console.WriteLine();
    Console.WriteLine(JsonSerializer.Serialize(workOrder, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "Repair planning failed");
    return 1;
}
