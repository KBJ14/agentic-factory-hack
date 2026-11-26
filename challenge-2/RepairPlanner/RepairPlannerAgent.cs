using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;
using RepairPlanner.Services;

namespace RepairPlanner;

/// <summary>
/// Registers the RepairPlannerAgent in Foundry and turns a diagnosed fault into a saved work order.
/// Data lookups are done in code (deterministic); the LLM only drafts the plan.
/// </summary>
// Primary constructor: the parameters become private fields of the class (like Python's __init__ args).
public sealed class RepairPlannerAgent(
    AIProjectClient projectClient,
    CosmosDbService cosmosDb,
    IFaultMappingService faultMapping,
    string modelDeploymentName,
    ILogger<RepairPlannerAgent> logger)
{
    private const string AgentName = "RepairPlannerAgent";

    private const string AgentInstructions = """
        You are a Repair Planner Agent for tire manufacturing equipment.
        Generate a repair plan with tasks, timeline, and resource allocation.
        Return the response as valid JSON matching the WorkOrder schema.

        Output JSON with these fields:
        - workOrderNumber, machineId, title, description
        - type: "corrective" | "preventive" | "emergency"
        - priority: "critical" | "high" | "medium" | "low"
        - status, assignedTo (technician id or null), notes
        - estimatedDuration: integer (minutes, e.g. 60 not "60 minutes")
        - partsUsed: [{ partId, partNumber, quantity }]
        - tasks: [{ sequence, title, description, estimatedDurationMinutes (integer), requiredSkills, safetyNotes }]

        IMPORTANT: All duration fields must be integers representing minutes (e.g. 90), not strings.

        Rules:
        - Assign the most qualified available technician
        - Include only relevant parts; empty array if none needed
        - Tasks must be ordered and actionable
        - Only use technician ids and part numbers that appear in the provided lists
        - Return only the JSON object, with no Markdown fences or extra text
        """;

    // LLMs sometimes return numbers as strings ("60"); allow that when parsing.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly JsonSerializerOptions PromptJsonOptions = new() { WriteIndented = true };

    private ProjectsAgentVersion? _agentVersion;

    /// <summary>
    /// Creates (or adds a new version of) the prompt agent in Foundry.
    /// </summary>
    public async Task EnsureAgentVersionAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Creating agent '{AgentName}' with model '{Model}'", AgentName, modelDeploymentName);

        var definition = new DeclarativeAgentDefinition(modelDeploymentName)
        {
            Instructions = AgentInstructions,
        };

        var result = await projectClient.AgentAdministrationClient.CreateAgentVersionAsync(
            AgentName,
            new ProjectsAgentVersionCreationOptions(definition),
            foundryFeatures: null,
            cancellationToken: ct);

        _agentVersion = result.Value;
        logger.LogInformation("Agent version: {Version} (id={Id})", _agentVersion.Version, _agentVersion.Id);
    }

    /// <summary>
    /// Full workflow: mapping → Cosmos lookups → agent call → parse/validate → save.
    /// </summary>
    public async Task<WorkOrder> PlanAndCreateWorkOrderAsync(DiagnosedFault fault, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fault);
        if (_agentVersion is null)
        {
            await EnsureAgentVersionAsync(ct);
        }

        logger.LogInformation("Planning repair for {MachineId}, fault={FaultType}", fault.MachineId, fault.FaultType);

        // 1. Required skills and parts from the static mapping
        var requiredSkills = faultMapping.GetRequiredSkills(fault.FaultType);
        var requiredParts = faultMapping.GetRequiredParts(fault.FaultType);

        // 2. Candidate technicians and parts from Cosmos DB
        var technicians = await cosmosDb.GetAvailableTechniciansWithSkillsAsync(requiredSkills, ct);
        var parts = await cosmosDb.GetPartsByPartNumbersAsync(requiredParts, ct);

        // 3. Build the prompt and invoke the agent
        var prompt = BuildPrompt(fault, requiredSkills, technicians, parts);
        logger.LogInformation("Invoking agent '{AgentName}'", AgentName);

        // ! tells the compiler "_agentVersion is not null here" (it was set above)
        AIAgent agent = projectClient.AsAIAgent(_agentVersion!);
        var response = await agent.RunAsync(prompt, cancellationToken: ct);
        var text = response.Text ?? string.Empty;

        // 4. Parse and apply defaults / validation
        var workOrder = ParseWorkOrder(text);
        ApplyDefaults(workOrder, fault, requiredSkills, technicians, parts);

        // 5. Save to Cosmos DB
        await cosmosDb.CreateWorkOrderAsync(workOrder, ct);
        return workOrder;
    }

    private static string BuildPrompt(
        DiagnosedFault fault,
        IReadOnlyList<string> requiredSkills,
        List<Technician> technicians,
        List<Part> parts)
    {
        // Send only the fields the planner needs (keeps the prompt small)
        var techSummary = technicians.Select(t => new
        {
            t.Id,
            t.Name,
            t.Role,
            t.Skills,
            matchingSkills = t.Skills.Where(s => requiredSkills.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList(),
            t.ShiftSchedule,
        });
        var partSummary = parts.Select(p => new { p.Id, p.PartNumber, p.Name, p.QuantityInStock, p.LeadTimeDays });

        var sb = new StringBuilder();
        sb.AppendLine("Create a repair work order for the following diagnosed fault.");
        sb.AppendLine();
        sb.AppendLine("## Diagnosed fault");
        sb.AppendLine(JsonSerializer.Serialize(fault, PromptJsonOptions));
        sb.AppendLine();
        sb.AppendLine("## Required skills");
        sb.AppendLine(string.Join(", ", requiredSkills));
        sb.AppendLine();
        sb.AppendLine("## Available technicians (best skill match first)");
        sb.AppendLine(technicians.Count > 0 ? JsonSerializer.Serialize(techSummary, PromptJsonOptions) : "None available - set assignedTo to null.");
        sb.AppendLine();
        sb.AppendLine("## Parts in inventory for this fault");
        sb.AppendLine(parts.Count > 0 ? JsonSerializer.Serialize(partSummary, PromptJsonOptions) : "No parts required - use an empty partsUsed array.");
        sb.AppendLine();
        sb.AppendLine("Return only the WorkOrder JSON object.");
        return sb.ToString();
    }

    private WorkOrder ParseWorkOrder(string text)
    {
        // Models sometimes wrap JSON in ```json fences or add prose; keep the outermost {...}
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            logger.LogError("Agent response did not contain a JSON object: {Response}", Truncate(text));
            throw new InvalidOperationException("Agent response did not contain a JSON object.");
        }

        try
        {
            return JsonSerializer.Deserialize<WorkOrder>(text[start..(end + 1)], JsonOptions)
                   ?? throw new InvalidOperationException("Agent returned an empty work order.");
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse agent response as WorkOrder: {Response}", Truncate(text));
            throw;
        }
    }

    private void ApplyDefaults(
        WorkOrder wo,
        DiagnosedFault fault,
        IReadOnlyList<string> requiredSkills,
        List<Technician> technicians,
        List<Part> parts)
    {
        var now = DateTimeOffset.UtcNow;

        // Values we know better than the model always come from the input
        wo.Id = Guid.NewGuid().ToString();
        wo.MachineId = fault.MachineId;
        wo.FaultType = fault.FaultType;
        wo.CreatedDate = now;

        // A new work order always starts as "new" (also the Cosmos partition key)
        wo.Status = "new";

        if (string.IsNullOrWhiteSpace(wo.WorkOrderNumber))
        {
            wo.WorkOrderNumber = $"WO-{now:yyyy}-{now:MMddHHmmss}";
        }

        // ??= means "assign if null" (like Python's: x = x or default)
        wo.Type ??= "corrective";
        wo.Priority ??= fault.Severity.ToLowerInvariant() switch
        {
            "critical" => "critical",
            "high" => "high",
            "low" => "low",
            _ => "medium",
        };

        // Only accept a technician from the candidate list; otherwise pick the best match
        if (wo.AssignedTo is not null && technicians.All(t => t.Id != wo.AssignedTo))
        {
            logger.LogWarning("Agent assigned unknown technician '{Tech}'; using best match instead", wo.AssignedTo);
            wo.AssignedTo = null;
        }
        // ?. returns null instead of throwing when the list is empty (FirstOrDefault() is null)
        wo.AssignedTo ??= technicians.FirstOrDefault()?.Id;

        // Keep only parts that exist in inventory and fill in their ids
        var partsByNumber = parts.ToDictionary(p => p.PartNumber, StringComparer.OrdinalIgnoreCase);
        wo.PartsUsed = wo.PartsUsed
            .Where(u => partsByNumber.ContainsKey(u.PartNumber))
            .Select(u => new WorkOrderPartUsage
            {
                PartId = partsByNumber[u.PartNumber].Id,
                PartNumber = partsByNumber[u.PartNumber].PartNumber,
                Quantity = u.Quantity > 0 ? u.Quantity : 1,
            })
            .ToList();

        // Order tasks and make sure each has required skills
        wo.Tasks = wo.Tasks.OrderBy(t => t.Sequence).ToList();
        foreach (var task in wo.Tasks)
        {
            if (task.RequiredSkills.Count == 0)
            {
                task.RequiredSkills = requiredSkills.ToList();
            }
        }

        if (wo.EstimatedDuration <= 0)
        {
            wo.EstimatedDuration = wo.Tasks.Sum(t => t.EstimatedDurationMinutes);
        }

        if (string.IsNullOrWhiteSpace(wo.Title))
        {
            wo.Title = $"Repair {fault.FaultType} on {fault.MachineId}";
        }
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "...";
}
