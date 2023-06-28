using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ImageResizerFunction.ImageProcessors
{
	public interface IImageProcessor
	{
		public Task<IActionResult> Process(
			string path,
			HttpContext httpContext,
			Stream blob,
			ILogger log);

		public bool IsValidForPath(string path);
	}
}
