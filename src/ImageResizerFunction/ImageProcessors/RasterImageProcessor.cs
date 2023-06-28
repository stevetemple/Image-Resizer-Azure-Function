using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Web;
using SixLabors.ImageSharp.Web.Commands;
using SixLabors.ImageSharp.Web.Middleware;
using SixLabors.ImageSharp.Web.Processors;

namespace ImageResizerFunction.ImageProcessors
{
	public class RasterImageProcessor : IImageProcessor
	{
		private readonly HashSet<string> _rasterFormatFileExtensions = new(StringComparer.OrdinalIgnoreCase)
		{
			".bmp",
			".gif",
			".jpg",
			".jpeg",
			".pbm",
			".png",
			".tiff",
			".tga",
			".webp",
		};

		private readonly CommandParser _commandParser;

		private readonly IImageWebProcessor[] _imageWebProcessors;
		
		public RasterImageProcessor(CommandParser commandParser)
		{
			_commandParser = commandParser;
			_imageWebProcessors = new IImageWebProcessor[]
			{
				new ResizeWebProcessor(),
				new FormatWebProcessor(Options.Create(new ImageSharpMiddlewareOptions())),
				new QualityWebProcessor()
			};
		}

		public async Task<IActionResult> Process(
				string path,
				HttpContext httpContext,
				Stream blob,
				ILogger log)
		{
			using var image = Image.Load(blob, out var imageFormat);
			var parser = new QueryCollectionRequestParser();
			var commands = parser.ParseRequestCommands(httpContext);

			var formattedImage = new FormattedImage(image, imageFormat);

			foreach (var processor in _imageWebProcessors)
			{
				formattedImage = processor.Process(formattedImage, log, commands, _commandParser, System.Globalization.CultureInfo.CurrentCulture);
			}

			var outStream = new MemoryStream();
			await formattedImage.Image.SaveAsync(outStream, formattedImage.Encoder);

			return new FileContentResult(outStream.ToArray(), formattedImage.Format.DefaultMimeType);
		}

		public bool IsValidForPath(string path)
		{
			return _rasterFormatFileExtensions.Any(path.EndsWith);
		}
	}
}
