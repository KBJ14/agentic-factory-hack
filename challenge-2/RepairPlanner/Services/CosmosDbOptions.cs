namespace RepairPlanner.Services;

/// <summary>
/// Connection settings for Cosmos DB.
/// Values come from COSMOS_ENDPOINT, COSMOS_KEY and COSMOS_DATABASE_NAME.
/// </summary>
public sealed class CosmosDbOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = "FactoryOpsDB";

    // Container names (partition keys in comments)
    public string TechniciansContainer { get; set; } = "Technicians";       // /department
    public string PartsInventoryContainer { get; set; } = "PartsInventory"; // /category
    public string WorkOrdersContainer { get; set; } = "WorkOrders";         // /status
}
