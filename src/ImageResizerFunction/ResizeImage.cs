using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ImageResizerFunction.ImageProcessors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp.Web.Caching;
using SixLabors.ImageSharp.Web.Middleware;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ImageResizerFunction
{
	public class ResizeImage
	{
		private const int CacheMinutes = 60 * 24; // 24 hours

		private static readonly ICacheHash CacheHash = new SHA256CacheHash(Options.Create(new ImageSharpMiddlewareOptions()));

		private readonly IImageProcessor[] _processors;

		private readonly ImageContainers _containers;

		private readonly ILogger<ResizeImage> _log;

		public ResizeImage(
			ImageContainers containers,
			ILogger<ResizeImage> log,
			RasterImageProcessor rasterImageProcessor,
			PassThroughImageProcessor passThroughImageProcessor)
		{
			_containers = containers;
			_log = log;
			_processors = new IImageProcessor[]
			{
				rasterImageProcessor,
				passThroughImageProcessor,
			};
		}

		[Function("Resize")]
		public async Task<IActionResult> Run(
				[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "{*path}")] HttpRequest req,
				string path)
		{
			var key = CacheHash.Create(path + req.HttpContext.Request.QueryString, 12);

			// Cache hit: a single request returns content and properties together.
			var cached = await TryDownloadAsync(_containers.Cache.GetBlobClient(key));
			if (cached != null)
			{
				if (cached.Details.LastModified > DateTimeOffset.UtcNow.AddMinutes(-CacheMinutes))
				{
					SetHeaders(req, cached.Details);
					return new FileStreamResult(cached.Content, cached.Details.ContentType);
				}

				// Stale: drop the connection without reading the body.
				cached.Dispose();
			}

			// Cache miss: download the source, which also gives us the ETag/LastModified for the headers.
			using var source = await TryDownloadAsync(_containers.Source.GetBlobClient(path));
			if (source == null)
			{
				return new NotFoundResult();
			}

			IActionResult response = null;
			foreach (var processor in _processors)
			{
				if (processor.IsValidForPath(path))
				{
					response = await processor.Process(path, req.HttpContext, source.Content, _log);
					break;
				}
			}

			if (response is FileContentResult fileResponse)
			{
				_ = UploadCache(key, fileResponse).ContinueWith(CacheUploadFailed, TaskContinuationOptions.OnlyOnFaulted);
			}

			SetHeaders(req, source.Details);
			return response;
		}

		private static async Task<BlobDownloadStreamingResult> TryDownloadAsync(BlobClient blob)
		{
			try
			{
				return (await blob.DownloadStreamingAsync()).Value;
			}
			catch (RequestFailedException ex) when (ex.Status == 404)
			{
				// Blob (or container) does not exist.
				return null;
			}
		}

		private static void SetHeaders(HttpRequest req, BlobDownloadDetails details)
		{
			req.HttpContext.Response.Headers.ETag = details.ETag.ToString();
			req.HttpContext.Response.Headers.CacheControl = "public, max-age=31536000";
			req.HttpContext.Response.Headers.LastModified = details.LastModified.ToString("R");
		}

		private async Task UploadCache(string key, FileContentResult response)
		{
			await _containers.EnsureCacheContainerAsync();

			// Upload overwrites by default and sets the content type in the same request.
			using var content = new MemoryStream(response.FileContents, writable: false);
			await _containers.Cache.GetBlobClient(key).UploadAsync(content, new BlobUploadOptions
			{
				HttpHeaders = new BlobHttpHeaders { ContentType = response.ContentType },
			});
		}

		public static void CacheUploadFailed(Task task)
		{
			Exception ex = task.Exception;
			// Log this?
		}
	}
}
