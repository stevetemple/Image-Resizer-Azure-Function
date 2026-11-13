using ImageResizerFunction;
using ImageResizerFunction.ImageProcessors;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp.Web;
using System.Linq;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
	.AddApplicationInsightsTelemetryWorkerService()
	.ConfigureFunctionsApplicationInsights();

// The App Insights SDK adds a default filter that only captures Warning and above; remove it so
// the levels configured in host.json apply.
builder.Logging.Services.Configure<LoggerFilterOptions>(options =>
{
	var defaultRule = options.Rules.FirstOrDefault(rule =>
		rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
	if (defaultRule != null)
	{
		options.Rules.Remove(defaultRule);
	}
});

builder.Services.AddImageSharp();
builder.Services.AddSingleton<ImageContainers>();
builder.Services.AddSingleton<RasterImageProcessor>();
builder.Services.AddSingleton<PassThroughImageProcessor>();

builder.Build().Run();
