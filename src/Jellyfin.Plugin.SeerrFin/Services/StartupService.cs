using System.Runtime.Loader;
using Jellyfin.Plugin.SeerrFin.Helpers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.SeerrFin.Services;

public class StartupService : IScheduledTask
{
    private static readonly Guid IndexHtmlTransformationId = Guid.Parse("b7c1e2f3-4a5b-6c7d-8e9f-0a1b2c3d4e5f");
    private static readonly Guid WebConfigTransformationId = Guid.Parse("64d34a37-eec7-4c86-95b4-21ad6c6be292");

    private readonly ILogger<SeerrFinPlugin> _logger;

    public StartupService(ILogger<SeerrFinPlugin> logger)
    {
        _logger = logger;
    }

    public string Name => "SeerrFin Startup";

    public string Key => "Jellyfin.Plugin.SeerrFin.Startup";

    public string Description => "Registers file transformations for SeerrFin";

    public string Category => "Startup Services";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("SeerrFin • registering file transformations");

        // Resolve File Transformation assembly at runtime via reflection
        var fileTransformationAssembly = AssemblyLoadContext.All
            .SelectMany(x => x.Assemblies)
            .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);

        if (fileTransformationAssembly == null)
        {
            _logger.LogWarning("SeerrFin • File Transformation plugin not found. UI injection won't work");
            return Task.CompletedTask;
        }

        Type? pluginInterfaceType = fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        if (pluginInterfaceType == null)
        {
            _logger.LogWarning("SeerrFin • File Transformation PluginInterface type not found");
            return Task.CompletedTask;
        }

        var registerTransformation = pluginInterfaceType.GetMethod("RegisterTransformation");
        if (registerTransformation == null)
        {
            _logger.LogWarning("SeerrFin • File Transformation RegisterTransformation method not found");
            return Task.CompletedTask;
        }

        void Register(Guid id, string fileName, string callback)
        {
            var payload = new JObject
            {
                ["id"] = id,
                ["fileNamePattern"] = fileName,
                ["callbackAssembly"] = GetType().Assembly.FullName,
                ["callbackClass"] = typeof(TransformationPatches).FullName,
                ["callbackMethod"] = callback
            };
            registerTransformation.Invoke(null, new object?[] { payload });
        }

        Register(IndexHtmlTransformationId, "index.html", nameof(TransformationPatches.IndexHtml));
        Register(WebConfigTransformationId, "config.json", nameof(TransformationPatches.WebConfig));
        _logger.LogInformation("SeerrFin • registered index.html and config.json transformations");
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }
}
