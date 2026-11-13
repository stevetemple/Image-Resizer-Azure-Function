using System.IO;
using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;

namespace ImageResizerFunction.ImageProcessors
{
	public class PassThroughImageProcessor : IImageProcessor
	{
		private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

		public Task<IActionResult> Process(string path, HttpContext httpContext, Stream blob, ILogger log)
		{
			// Stream the source straight through to the client rather than buffering it in memory.
			// FileStreamResult disposes the stream once the response has been written.
			var contentType = GetMimeType(Path.GetFileName(path));
			return Task.FromResult<IActionResult>(new FileStreamResult(blob, contentType));
		}

		public bool IsValidForPath(string path)
		{
			return true;
		}

		public IActionResult Validate(string path, HttpContext httpContext) => null;

		// The output is identical to the source blob, so caching it would only duplicate storage.
		public string GetCacheIdentity(string path, HttpContext httpContext) => null;

		private static string GetMimeType(string fileName)
		{
			return ContentTypeProvider.TryGetContentType(fileName, out var contentType)
				? contentType
				: MediaTypeNames.Application.Octet;
		}
	}
}
