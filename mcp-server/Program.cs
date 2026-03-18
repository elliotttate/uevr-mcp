using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddDebug();

try
{
    Console.Error.WriteLine($"Starting UEVR MCP Server with args: {string.Join(" ", args)}");
    var isSingleTool = args.Contains("--single-tool") || args.Contains("-single");

    var mcpBuilder = builder.Services
        .AddMcpServer()
        .WithStdioServerTransport();

    // Discover all types in the assembly
    var allTypes = Assembly.GetExecutingAssembly().GetTypes();

    // Identify tool types (those with [McpServerToolType])
    var toolTypes = allTypes
        .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
        .ToList();

    Console.Error.WriteLine($"Found {toolTypes.Count} classes with [McpServerToolType] attribute.");

    if (isSingleTool)
    {
        Console.Error.WriteLine("Mode: Single Tool (Consolidated View)");
        var dispatcherType = allTypes.FirstOrDefault(t => t.Name == "DispatcherTools");
        if (dispatcherType != null)
        {
            var uevrMethod = dispatcherType.GetMethod("Uevr");
            if (uevrMethod != null)
            {
                var tool = McpServerTool.Create(uevrMethod, _ => new UevrMcp.DispatcherTools());
                mcpBuilder.WithTools(new[] { tool });
            }
        }
        var metaType = allTypes.FirstOrDefault(t => t.Name == "MetaTools");
        if (metaType != null)
        {
            var listMethod = metaType.GetMethod("ListTools");
            if (listMethod != null)
            {
                var tool = McpServerTool.Create(listMethod);
                mcpBuilder.WithTools(new[] { tool });
            }
        }
    }
    else
    {
        Console.Error.WriteLine("Mode: Standard (Individual Tools View)");
        mcpBuilder.WithToolsFromAssembly();
    }

    var app = builder.Build();
    Console.Error.WriteLine("MCP Host built successfully.");
    await app.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal error during startup: {ex}");
    throw;
}
