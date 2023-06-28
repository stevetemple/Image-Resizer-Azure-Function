using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using ImageResizerFunction.ImageProcessors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp.Web.Caching;
using SixLabors.ImageSharp.Web.Middleware;
using System;
using System.IO;
using System.Threading.Tasks;
using Azure.Storage.Blobs.Models;

namespace ImageResizerFunction
{
	public class ResizeImage
	{
		private const int CacheMinutes = 60 * 24; // 24 hours

		private readonly IImageProcessor[] _processors;

		private readonly BlobContainerClient _container;
		private readonly BlobContainerClient _cacheContainer;

		private readonly ICacheHash _cacheHash;


		public ResizeImage(
			RasterImageProcessor rasterImageProcessor,
			PassThroughImageProcessor passThroughImageProcessor)
		{
			_processors = new IImageProcessor[]
			{
				rasterImageProcessor,
				passThroughImageProcessor,
			};

			var connectionString = Environment.GetEnvironmentVariable("ImageConnectionString");
			var containerName = Environment.GetEnvironmentVariable("Container").ToLower();
			var cacheContainerName = Environment.GetEnvironmentVariable("CacheContainer").ToLower();

			_cacheHash = new SHA256CacheHash(Options.Create(new ImageSharpMiddlewareOptions()));

			_cacheContainer = new BlobContainerClient(connectionString, cacheContainerName);
			_container = new BlobContainerClient(connectionString, containerName);
		}

		[FunctionName("Resize")]
		public async Task<IActionResult> Run(
				[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "{*path}")] HttpRequest req,
				string path,
				ILogger log)
		{
			var key = _cacheHash.Create(path + req.HttpContext.Request.QueryString, 12);
			
			if (!await _cacheContainer.ExistsAsync())
			{
				await _cacheContainer.CreateAsync();
			}
			
			var cachedBlob = _cacheContainer.GetBlockBlobClient(key);
			var cacheExists = await cachedBlob.ExistsAsync();
			if (cacheExists.Value)
			{
				var cachedProperties = await cachedBlob.GetPropertiesAsync();
				if (cachedProperties.Value.LastModified > DateTime.UtcNow.AddMinutes(-CacheMinutes))
				{
					SetHeaders(req, cachedProperties.Value);
					return new FileStreamResult(await cachedBlob.OpenReadAsync(), cachedProperties.Value.ContentType);
				}
			}
			
			var blob = _container.GetBlockBlobClient(path);
			var exists = await blob.ExistsAsync();
			if (!exists.Value)
			{
				return new NotFoundResult();
			}

			var result = await blob.DownloadStreamingAsync();

			IActionResult response = null;
			foreach (var processor in _processors)
			{
				if (processor.IsValidForPath(path))
				{
					response = await processor.Process(path, req.HttpContext, result.Value.Content, log);
					break;
				}
			}
			
			_ = UploadCache(key, response as FileContentResult).ContinueWith(CacheUploadFailed, TaskContinuationOptions.OnlyOnFaulted);

			var properties = await blob.GetPropertiesAsync();
			SetHeaders(req, properties.Value);
			return response;
		}

		private void SetHeaders(HttpRequest req, BlobProperties blobProperties)
		{
			var lastModified = blobProperties.LastModified;
			var etag = blobProperties.ETag;

			req.HttpContext.Response.Headers.ETag = etag.ToString();
			req.HttpContext.Response.Headers.CacheControl = "public, max-age=31536000";
			req.HttpContext.Response.Headers.LastModified = lastModified.ToString("R");
		}

		private async Task UploadCache(string key, FileContentResult response)
		{
			var blobClient = _cacheContainer.GetBlobClient(key);
			await blobClient.DeleteIfExistsAsync();
			using (var memoryStream = new MemoryStream(response.FileContents))
			{
				await blobClient.UploadAsync(memoryStream);

			}
			await blobClient.SetHttpHeadersAsync(new BlobHttpHeaders
			{
				ContentType = (response as FileContentResult).ContentType
			});
		}

		public static void CacheUploadFailed(Task task)
		{
			Exception ex = task.Exception;
			// Log this?
		}
	}
}
