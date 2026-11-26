using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;

namespace RepairPlanner.Services;

/// <summary>
/// Data access for technicians, parts inventory and work orders.
/// The Cosmos SDK serializes with Newtonsoft.Json, so the [JsonProperty] attributes on the models apply.
/// </summary>
public sealed class CosmosDbService : IDisposable
{
    private readonly CosmosClient _client;
    private readonly Container _technicians;
    private readonly Container _parts;
    private readonly Container _workOrders;
    private readonly ILogger<CosmosDbService> _logger;

    public CosmosDbService(CosmosDbOptions options, ILogger<CosmosDbService> logger)
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace fails fast on missing config (like a Python assert)
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Endpoint, nameof(options.Endpoint));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Key, nameof(options.Key));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabaseName, nameof(options.DatabaseName));

        _logger = logger;

        // Gateway mode uses HTTPS (443) only, which works behind most container/proxy setups.
        _client = new CosmosClient(options.Endpoint, options.Key, new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
        });

        var database = _client.GetDatabase(options.DatabaseName);
        _technicians = database.GetContainer(options.TechniciansContainer);
        _parts = database.GetContainer(options.PartsInventoryContainer);
        _workOrders = database.GetContainer(options.WorkOrdersContainer);
    }

    /// <summary>
    /// Returns available technicians that have at least one of the required skills,
    /// ordered by how many of the required skills they have (best match first).
    /// </summary>
    public async Task<List<Technician>> GetAvailableTechniciansWithSkillsAsync(
        IReadOnlyList<string> requiredSkills, CancellationToken ct = default)
    {
        if (requiredSkills.Count == 0)
        {
            _logger.LogWarning("No required skills given; returning no technicians");
            return [];
        }

        // EXISTS + ARRAY_CONTAINS = "any of the technician's skills is in @skills"
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.available = true " +
                "AND EXISTS(SELECT VALUE s FROM s IN c.skills WHERE ARRAY_CONTAINS(@skills, s))")
            .WithParameter("@skills", requiredSkills);

        try
        {
            var technicians = await QueryAllAsync<Technician>(_technicians, query, ct);

            var ranked = technicians
                .OrderByDescending(t => t.Skills.Count(s => requiredSkills.Contains(s, StringComparer.OrdinalIgnoreCase)))
                .ThenBy(t => t.Id, StringComparer.Ordinal)
                .ToList();

            _logger.LogInformation("Found {Count} available technicians matching skills", ranked.Count);
            return ranked;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Failed to query technicians (status {StatusCode})", ex.StatusCode);
            throw;
        }
    }

    /// <summary>
    /// Returns inventory items whose partNumber is in the given list.
    /// </summary>
    public async Task<List<Part>> GetPartsByPartNumbersAsync(
        IReadOnlyList<string> partNumbers, CancellationToken ct = default)
    {
        if (partNumbers.Count == 0)
        {
            _logger.LogInformation("No parts required for this fault");
            return [];
        }

        var query = new QueryDefinition("SELECT * FROM c WHERE ARRAY_CONTAINS(@partNumbers, c.partNumber)")
            .WithParameter("@partNumbers", partNumbers);

        try
        {
            var parts = await QueryAllAsync<Part>(_parts, query, ct);

            var missing = partNumbers.Except(parts.Select(p => p.PartNumber), StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
            {
                _logger.LogWarning("Parts not found in inventory: {Missing}", string.Join(", ", missing));
            }

            _logger.LogInformation("Fetched {Count} parts", parts.Count);
            return parts;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Failed to query parts inventory (status {StatusCode})", ex.StatusCode);
            throw;
        }
    }

    /// <summary>
    /// Saves a new work order and returns its id.
    /// </summary>
    public async Task<string> CreateWorkOrderAsync(WorkOrder workOrder, CancellationToken ct = default)
    {
        // "status" is the partition key, so it must not be empty.
        // ??= means "assign if null" (like Python's: x = x or default)
        workOrder.Status ??= "new";
        if (string.IsNullOrWhiteSpace(workOrder.Id))
        {
            workOrder.Id = Guid.NewGuid().ToString();
        }

        try
        {
            var response = await _workOrders.CreateItemAsync(
                workOrder, new PartitionKey(workOrder.Status), cancellationToken: ct);

            _logger.LogInformation(
                "Created work order {WorkOrderNumber} (id={Id}, status={Status}, RU={RequestCharge:F1})",
                workOrder.WorkOrderNumber, workOrder.Id, workOrder.Status, response.RequestCharge);

            return response.Resource.Id;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Failed to create work order {WorkOrderNumber} (status {StatusCode})",
                workOrder.WorkOrderNumber, ex.StatusCode);
            throw;
        }
    }

    // Reads every page of a query result (Cosmos returns results in pages).
    private static async Task<List<T>> QueryAllAsync<T>(Container container, QueryDefinition query, CancellationToken ct)
    {
        var results = new List<T>();
        using var iterator = container.GetItemQueryIterator<T>(query);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    public void Dispose() => _client.Dispose();
}
