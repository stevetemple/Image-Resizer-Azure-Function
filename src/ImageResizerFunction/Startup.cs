using ImageResizerFunction.ImageProcessors;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SixLabors.ImageSharp.Web.DependencyInjection;

[assembly: FunctionsStartup(typeof(ImageResizerFunction.Startup))]

namespace ImageResizerFunction
{
	public class Startup : FunctionsStartup
	{
		public override void Configure(IFunctionsHostBuilder builder)
		{
			builder.Services.AddImageSharp();
			builder.Services.TryAddTransient<RasterImageProcessor>();
			builder.Services.TryAddTransient<PassThroughImageProcessor>();
		}
	}
}
