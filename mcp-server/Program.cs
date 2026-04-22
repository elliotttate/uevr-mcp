using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

// Dev/test CLI entry points. When the first arg matches a known command, we
// invoke the static tool and print its JSON result, bypassing the MCP stdio
// transport. Lets shell scripts drive the emitter + compile-check without
// spinning up a full MCP client.
if (args.Length > 0)
{
    switch (args[0])
    {
        case "emit-from-usmap":
        {
            // args: emit-from-usmap <usmap> <outDir> <projectName> <moduleName> <engineAssoc> [gobjPath]
            if (args.Length < 6)
            {
                Console.Error.WriteLine("usage: emit-from-usmap <usmap> <outDir> <proj> <module> <engine> [gobj]");
                return 2;
            }
            var gobj = args.Length > 6 ? args[6] : null;
            Console.WriteLine(UevrMcp.UsmapSdkTools.DumpUhtFromUsmap(args[1], args[2], args[3], args[4], args[5], gobj));
            return 0;
        }
        case "compile-check":
        {
            // args: compile-check <emittedDir> <hostUproject> [module] [maxHeaders] [headerFilter]
            if (args.Length < 3)
            {
                Console.Error.WriteLine("usage: compile-check <emittedDir> <hostUproject> [module] [maxHeaders] [headerFilter]");
                return 2;
            }
            var moduleName = args.Length > 3 && args[3] != "-" ? args[3] : null;
            int maxHeaders = args.Length > 4 && int.TryParse(args[4], out var mh) ? mh : 0;
            var headerFilter = args.Length > 5 && args[5] != "-" ? args[5] : null;
            var task = UevrMcp.CompileCheckTools.CompileCheck(
                args[1], args[2], moduleName, skipUprojectPatch: false,
                timeoutSec: 1200, maxHeaders: maxHeaders, headerFilter: headerFilter);
            Console.WriteLine(task.GetAwaiter().GetResult());
            return 0;
        }
        case "plugin-info":
        {
            if (args.Length < 2) { Console.Error.WriteLine("usage: plugin-info <gameExe>"); return 2; }
            var task = UevrMcp.PluginReloadTools.PluginInfo(args[1]);
            Console.WriteLine(task.GetAwaiter().GetResult());
            return 0;
        }
        case "plugin-rebuild":
        {
            var task = UevrMcp.PluginReloadTools.PluginRebuild();
            Console.WriteLine(task.GetAwaiter().GetResult());
            return 0;
        }
        case "setup-game":
        {
            if (args.Length < 2) { Console.Error.WriteLine("usage: setup-game <gameExe>"); return 2; }
            Console.WriteLine(UevrMcp.SetupTools.SetupGame(args[1]).GetAwaiter().GetResult());
            return 0;
        }
        case "attach":
        {
            // attach-only — game must already be running. Use when the game
            // has a Steam / Denuvo / EAC launcher-stub that doesn't survive
            // direct exe invocation (Hogwarts Legacy etc.).
            if (args.Length < 2) { Console.Error.WriteLine("usage: attach <gameExe>"); return 2; }
            Console.WriteLine(UevrMcp.SetupTools.SetupGame(args[1], launchIfMissing: false).GetAwaiter().GetResult());
            return 0;
        }
        case "stop-game":
        {
            if (args.Length < 2) { Console.Error.WriteLine("usage: stop-game <gameExe>"); return 2; }
            Console.WriteLine(UevrMcp.ReadinessTools.StopGame(args[1]).GetAwaiter().GetResult());
            return 0;
        }
        case "wait-plugin":
        {
            int timeout = args.Length > 1 && int.TryParse(args[1], out var t) ? t : 30000;
            Console.WriteLine(UevrMcp.ReadinessTools.WaitForPlugin(timeout).GetAwaiter().GetResult());
            return 0;
        }
        case "dump-ue-project":
        {
            // args: dump-ue-project <outDir> [projectName] [modules] [engineAssoc] [methods:0|1] [gameContent:0|1]
            if (args.Length < 2) { Console.Error.WriteLine("usage: dump-ue-project <outDir> [projectName] [modules] [engineAssoc] [methods:0|1] [gameContent:0|1]"); return 2; }
            var outDir = args[1];
            var projectName = args.Length > 2 && args[2] != "-" ? args[2] : null;
            var modules = args.Length > 3 && args[3] != "-" ? args[3] : null;
            var engineAssoc = args.Length > 4 ? args[4] : "4.26";
            bool methods = args.Length > 5 && (args[5] == "1" || args[5].Equals("true", StringComparison.OrdinalIgnoreCase));
            bool gameContent = args.Length > 6 && (args[6] == "1" || args[6].Equals("true", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine(UevrMcp.UhtSdkTools.DumpUeProject(outDir, projectName, modules, engineAssoc, skipEngineModules: true, methods: methods, includeGameContent: gameContent).GetAwaiter().GetResult());
            return 0;
        }
        case "dump-usmap":
        {
            // args: dump-usmap <outPath> [filter] [compression]
            if (args.Length < 2) { Console.Error.WriteLine("usage: dump-usmap <outPath> [filter] [compression]"); return 2; }
            var outPath = args[1];
            var filter = args.Length > 2 && args[2] != "-" ? args[2] : null;
            var compression = args.Length > 3 ? args[3] : "none";
            Console.WriteLine(UevrMcp.DumpTools.DumpUsmap(outPath, filter, compression).GetAwaiter().GetResult());
            return 0;
        }
        case "dump-bn-bundle":
        {
            // args: dump-bn-bundle <outDir> [filter]
            if (args.Length < 2) { Console.Error.WriteLine("usage: dump-bn-bundle <outDir> [filter]"); return 2; }
            var outDir = args[1];
            var filter = args.Length > 2 && args[2] != "-" ? args[2] : null;
            Console.WriteLine(UevrMcp.ReverseEngineeringTools.DumpBinaryNinjaIdaBundle(outDir, filter, pretty: true).GetAwaiter().GetResult());
            return 0;
        }
        case "ps-resolve":
        {
            if (args.Length < 2) { Console.Error.WriteLine("usage: ps-resolve <exePath> [resolvers]"); return 2; }
            var resolvers = args.Length > 2 ? args[2] : null;
            Console.WriteLine(UevrMcp.PatternsleuthTools.ResolveOffsets(args[1], resolvers).GetAwaiter().GetResult());
            return 0;
        }
        case "ps-xref":
        {
            if (args.Length < 3) { Console.Error.WriteLine("usage: ps-xref <exePath> <address>"); return 2; }
            Console.WriteLine(UevrMcp.PatternsleuthTools.XrefScan(args[1], args[2]).GetAwaiter().GetResult());
            return 0;
        }
        case "ps-diff":
        {
            if (args.Length < 2) { Console.Error.WriteLine("usage: ps-diff <exePath> [baselineJson] [outputJson]"); return 2; }
            var baseline = args.Length > 2 && args[2] != "-" ? args[2] : null;
            var outPath = args.Length > 3 && args[3] != "-" ? args[3] : null;
            Console.WriteLine(UevrMcp.PatternsleuthTools.ResolverDiff(args[1], baseline, outPath).GetAwaiter().GetResult());
            return 0;
        }
        case "ps-disasm":
        {
            if (args.Length < 3) { Console.Error.WriteLine("usage: ps-disasm <exePath> <resolverOrRange>"); return 2; }
            Console.WriteLine(UevrMcp.PatternsleuthTools.DisassembleFunction(args[1], args[2]).GetAwaiter().GetResult());
            return 0;
        }
        case "ps-symbols":
        {
            if (args.Length < 3) { Console.Error.WriteLine("usage: ps-symbols <exePath> <regex>"); return 2; }
            Console.WriteLine(UevrMcp.PatternsleuthTools.PdbSymbols(args[1], args[2]).GetAwaiter().GetResult());
            return 0;
        }
    }
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddDebug();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
await builder.Build().RunAsync();
return 0;
