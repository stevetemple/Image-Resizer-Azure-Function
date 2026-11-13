using System;
using System.Collections.Generic;
using System.Globalization;
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
		private const int DefaultMaxDimension = 4000;

		private static readonly HashSet<string> RasterFormatFileExtensions = new(StringComparer.OrdinalIgnoreCase)
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

		private static readonly QueryCollectionRequestParser RequestParser = new();

		// Query values are parsed the same way whatever culture the host runs under.
		private static readonly CultureInfo ParsingCulture = CultureInfo.InvariantCulture;

		private readonly CommandParser _commandParser;

		private readonly IImageWebProcessor[] _imageWebProcessors;

		// Every query parameter the processors above act on; anything else has no effect on the output.
		private readonly HashSet<string> _supportedCommands;

		private readonly int _maxWidth;

		private readonly int _maxHeight;

		public RasterImageProcessor(CommandParser commandParser)
		{
			_commandParser = commandParser;
			_imageWebProcessors = new IImageWebProcessor[]
			{
				new ResizeWebProcessor(),
				new FormatWebProcessor(Options.Create(new ImageSharpMiddlewareOptions())),
				new QualityWebProcessor()
			};

			_supportedCommands = new HashSet<string>(
				_imageWebProcessors.SelectMany(processor => processor.Commands),
				StringComparer.OrdinalIgnoreCase);

			_maxWidth = ReadLimit("MaxWidth");
			_maxHeight = ReadLimit("MaxHeight");
		}

		public async Task<IActionResult> Process(
				string path,
				HttpContext httpContext,
				Stream blob,
				ILogger log)
		{
			using var image = await Image.LoadAsync(blob);
			var imageFormat = image.Metadata.DecodedImageFormat;
			var commands = RequestParser.ParseRequestCommands(httpContext);

			var formattedImage = new FormattedImage(image, imageFormat);

			foreach (var processor in _imageWebProcessors)
			{
				formattedImage = processor.Process(formattedImage, log, commands, _commandParser, ParsingCulture);
			}

			var outStream = new MemoryStream();
			await formattedImage.Image.SaveAsync(outStream, formattedImage.Encoder);

			// Serve the encoder's buffer as-is instead of copying it with ToArray(). The stream is
			// read-only and its buffer is exposed so the cache upload can read the same bytes.
			var buffer = outStream.GetBuffer();
			var output = new MemoryStream(buffer, 0, (int)outStream.Length, writable: false, publiclyVisible: true);

			return new FileStreamResult(output, formattedImage.Format.DefaultMimeType);
		}

		public IActionResult Validate(string path, HttpContext httpContext)
		{
			// Reject oversized output before anything is downloaded or decoded: a large width or height
			// costs memory and CPU proportional to its area, and each distinct value is a new cache entry.
			var commands = RequestParser.ParseRequestCommands(httpContext);

			var error = CheckDimension(commands, ResizeWebProcessor.Width, _maxWidth)
				?? CheckDimension(commands, ResizeWebProcessor.Height, _maxHeight);

			return error == null ? null : new BadRequestObjectResult(error);
		}

		public string GetCacheIdentity(string path, HttpContext httpContext)
		{
			// Only parameters the processors act on, with lower-cased names in a fixed order, so
			// ?height=50&width=100, ?Width=100&height=50 and ?width=100&height=50&x=1 share one entry.
			var commands = RequestParser.ParseRequestCommands(httpContext)
				.Where(command => _supportedCommands.Contains(command.Key))
				.Select(command => KeyValuePair.Create(command.Key.ToLowerInvariant(), command.Value))
				.OrderBy(command => command.Key, StringComparer.Ordinal);

			return path + QueryString.Create(commands);
		}

		public bool IsValidForPath(string path)
		{
			// A single case-insensitive hash lookup instead of a culture-sensitive EndsWith per extension.
			return RasterFormatFileExtensions.Contains(Path.GetExtension(path));
		}

		private string CheckDimension(CommandCollection commands, string command, int max)
		{
			if (!commands.TryGetValue(command, out var value))
			{
				return null;
			}

			int size;
			try
			{
				size = _commandParser.ParseValue<int>(value, ParsingCulture);
			}
			catch (FormatException)
			{
				// Would otherwise fail later during processing as a 500.
				return $"'{command}' must be a number.";
			}

			return size > max ? $"'{command}' must be at most {max} pixels." : null;
		}

		private static int ReadLimit(string setting)
		{
			return int.TryParse(Environment.GetEnvironmentVariable(setting), NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit > 0
				? limit
				: DefaultMaxDimension;
		}
	}
}
