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
		public async Task<IActionResult> Process(string path, HttpContext httpContext, Stream blob, ILogger log)
		{
			var contentType = GetMimeType(Path.GetFileName(path));
			var stream = new MemoryStream();
			await blob.CopyToAsync(stream);

			return new FileContentResult(stream.ToArray(), contentType);
		}

		public bool IsValidForPath(string path)
		{
			return true;
		}

		private string GetMimeType(string fileName)
		{
			return new FileExtensionContentTypeProvider().TryGetContentType(fileName, out var contentType)
				? contentType
				: MediaTypeNames.Application.Octet;
		}
	}
}
