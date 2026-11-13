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

		/// <summary>
		/// Checks the request before any storage access. Returns null if it can be processed,
		/// otherwise the error result to send to the client.
		/// </summary>
		public IActionResult Validate(string path, HttpContext httpContext);

		/// <summary>
		/// A normalised string identifying this request's output, from which the cache key is derived.
		/// Requests that would produce the same output must return the same value. Returns null when
		/// the output should not be cached, e.g. when it is the source blob unchanged.
		/// </summary>
		public string GetCacheIdentity(string path, HttpContext httpContext);
	}
}
