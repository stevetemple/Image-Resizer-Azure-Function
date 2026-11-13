using Azure;
using Azure.Storage.Blobs.Models;
using ImageResizerFunction.ImageProcessors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using SixLabors.ImageSharp.Web.Caching;
using SixLabors.ImageSharp.Web.Middleware;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ImageResizerFunction
{
	public class ResizeImage
	{
		// How long a cached entry is served before checking the source blob for changes. Entries whose
		// source is unchanged are kept, so this bounds how stale output can be rather than its lifetime.
		private const int CacheRevalidateMinutes = 60 * 24; // 24 hours

		// Cached blobs record the source blob's validators so every response for a URL carries the
		// same ETag/Last-Modified, whether it was served from the cache or freshly processed.
		private const string SourceETagMetadata = "sourceetag";
		private const string SourceLastModifiedMetadata = "sourcelastmodified";

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
			var processor = _processors.First(p => p.IsValidForPath(path));

			if (processor.Validate(path, req.HttpContext) is { } invalid)
			{
				return invalid;
			}

			string key = null;
			BlobDownloadStreamingResult source = null;

			if (processor.GetCacheIdentity(path, req.HttpContext) is { } cacheIdentity)
			{
				key = CacheHash.Create(cacheIdentity, 12);

				// Cache hit: a single request returns properties first, then streams the body.
				var cached = await TryDownloadAsync(_containers.Cache.GetBlobClient(key));
				if (cached != null)
				{
					var hasSourceValidators = TryGetSourceValidators(cached.Details, out var etag, out var lastModified);
					var current = cached.Details.LastModified > DateTimeOffset.UtcNow.AddMinutes(-CacheRevalidateMinutes);

					if (!current && hasSourceValidators)
					{
						// Due for revalidation: ask Storage whether the source has changed since this entry
						// was built. Unchanged costs no body; changed returns the new source, used below.
						var revalidation = await DownloadSourceAsync(path, new BlobRequestConditions { IfNoneMatch = etag });
						if (revalidation.Status == StatusCodes.Status304NotModified)
						{
							current = true;
							_ = LogCacheFailureAsync(RefreshCacheAsync(key, cached.Details), "refresh", key);
						}
						else if (revalidation.Status == StatusCodes.Status404NotFound)
						{
							cached.Dispose();
							return new NotFoundResult();
						}
						else
						{
							source = revalidation.Result;
						}
					}

					if (current)
					{
						SetHeaders(req, etag, lastModified);

						if (IsNotModified(req, etag, lastModified))
						{
							// Only the headers have been read; drop the body.
							cached.Dispose();
							return new StatusCodeResult(StatusCodes.Status304NotModified);
						}

						return new FileStreamResult(cached.Content, cached.Details.ContentType);
					}

					// Out of date: drop the connection without reading the body.
					cached.Dispose();
				}
			}

			if (source == null)
			{
				// Cache miss (or uncached type). Pass the client's validators to Storage so an unchanged
				// source comes back as 304 with no body and no processing.
				var download = await DownloadSourceAsync(path, GetSourceConditions(req));
				if (download.Status == StatusCodes.Status404NotFound)
				{
					return new NotFoundResult();
				}

				if (download.Status == StatusCodes.Status304NotModified)
				{
					if (download.NotModifiedETag is { } etag && download.NotModifiedLastModified is { } lastModified)
					{
						SetHeaders(req, etag, lastModified);
					}

					return new StatusCodeResult(StatusCodes.Status304NotModified);
				}

				source = download.Result;
			}

			IActionResult response = null;
			try
			{
				response = await processor.Process(path, req.HttpContext, source.Content, _log);
			}
			finally
			{
				// A pass-through result is still reading the source and disposes it once the response is written.
				if (!ReferenceEquals((response as FileStreamResult)?.FileStream, source.Content))
				{
					source.Dispose();
				}
			}

			if (key != null
				&& response is FileStreamResult { FileStream: MemoryStream output } fileResponse
				&& output.TryGetBuffer(out var bytes))
			{
				_ = LogCacheFailureAsync(UploadCache(key, bytes, fileResponse.ContentType, source.Details), "upload", key);
			}

			SetHeaders(req, source.Details.ETag, source.Details.LastModified);
			return response;
		}

		private static async Task<BlobDownloadStreamingResult> TryDownloadAsync(Azure.Storage.Blobs.BlobClient blob)
		{
			try
			{
				return (await blob.DownloadStreamingAsync()).Value;
			}
			catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
			{
				// Blob (or container) does not exist.
				return null;
			}
		}

		/// <summary>
		/// The source blob's validators recorded on a cached blob. Returns false, with the cached blob's
		/// own validators, for entries written before the metadata was added; those cannot be revalidated.
		/// </summary>
		private static bool TryGetSourceValidators(BlobDownloadDetails cached, out ETag etag, out DateTimeOffset lastModified)
		{
			if (cached.Metadata.TryGetValue(SourceETagMetadata, out var storedETag)
				&& cached.Metadata.TryGetValue(SourceLastModifiedMetadata, out var storedLastModified)
				&& DateTimeOffset.TryParseExact(storedLastModified, "R", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out lastModified))
			{
				etag = new ETag(storedETag);
				return true;
			}

			etag = cached.ETag;
			lastModified = cached.LastModified;
			return false;
		}

		private sealed record SourceDownload(
			int Status,
			BlobDownloadStreamingResult Result = null,
			ETag? NotModifiedETag = null,
			DateTimeOffset? NotModifiedLastModified = null);

		/// <summary>
		/// Downloads the source blob, reporting 404 and 304 (when <paramref name="conditions"/> matched)
		/// as a status rather than an exception. A 304 carries the source's current validators.
		/// </summary>
		private async Task<SourceDownload> DownloadSourceAsync(string path, BlobRequestConditions conditions)
		{
			try
			{
				var result = await _containers.Source.GetBlobClient(path).DownloadStreamingAsync(new BlobDownloadOptions
				{
					Conditions = conditions,
				});

				return new SourceDownload(StatusCodes.Status200OK, result.Value);
			}
			catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
			{
				return new SourceDownload(StatusCodes.Status404NotFound);
			}
			catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status304NotModified)
			{
				var headers = ex.GetRawResponse()?.Headers;
				if (headers?.ETag is { } etag && headers.Value.TryGetValue("Last-Modified", out var lastModifiedHeader)
					&& DateTimeOffset.TryParse(lastModifiedHeader, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lastModified))
				{
					return new SourceDownload(StatusCodes.Status304NotModified, NotModifiedETag: etag, NotModifiedLastModified: lastModified);
				}

				return new SourceDownload(StatusCodes.Status304NotModified);
			}
		}

		/// <summary>
		/// HTTP conditional GET: If-None-Match takes precedence; If-Modified-Since is only used without it.
		/// </summary>
		private static bool IsNotModified(HttpRequest req, ETag etag, DateTimeOffset lastModified)
		{
			var headers = req.GetTypedHeaders();

			if (headers.IfNoneMatch.Count > 0)
			{
				var current = EntityTagHeaderValue.Parse(etag.ToString("H"));
				return headers.IfNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(current, useStrongComparison: false));
			}

			// HTTP dates have one-second precision.
			var lastModifiedSeconds = lastModified.AddTicks(-(lastModified.Ticks % TimeSpan.TicksPerSecond));
			return headers.IfModifiedSince is { } since && lastModifiedSeconds <= since;
		}

		/// <summary>
		/// Translates the client's validators into a Storage condition. Storage accepts a single ETag,
		/// so requests listing several (rare) are not made conditional.
		/// </summary>
		private static BlobRequestConditions GetSourceConditions(HttpRequest req)
		{
			var headers = req.GetTypedHeaders();

			if (headers.IfNoneMatch.Count > 0)
			{
				if (headers.IfNoneMatch.Count == 1 && !headers.IfNoneMatch[0].Equals(EntityTagHeaderValue.Any))
				{
					// Weak comparison: a proxy may have weakened our strong ETag.
					return new BlobRequestConditions { IfNoneMatch = new ETag(headers.IfNoneMatch[0].Tag.ToString()) };
				}

				return null;
			}

			return headers.IfModifiedSince is { } since
				? new BlobRequestConditions { IfModifiedSince = since }
				: null;
		}

		private static void SetHeaders(HttpRequest req, ETag etag, DateTimeOffset lastModified)
		{
			req.HttpContext.Response.Headers.ETag = etag.ToString("H");
			req.HttpContext.Response.Headers.CacheControl = "public, max-age=31536000";
			req.HttpContext.Response.Headers.LastModified = lastModified.ToString("R");
		}

		private async Task UploadCache(string key, ArraySegment<byte> bytes, string contentType, BlobDownloadDetails source)
		{
			await _containers.EnsureCacheContainerAsync();

			// A separate read-only stream over the same buffer the response is being served from, so
			// the two never share a position and nothing is copied.
			using var content = new MemoryStream(bytes.Array, bytes.Offset, bytes.Count, writable: false);

			// Upload overwrites by default and sets the content type and metadata in the same request.
			await _containers.Cache.GetBlobClient(key).UploadAsync(content, new BlobUploadOptions
			{
				HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
				Metadata = new Dictionary<string, string>
				{
					[SourceETagMetadata] = source.ETag.ToString("H"),
					[SourceLastModifiedMetadata] = source.LastModified.ToString("R"),
				},
			});
		}

		/// <summary>
		/// Marks a cached entry as checked against an unchanged source. Rewriting its metadata updates
		/// its Last-Modified without re-uploading the image. Conditional on the entry's ETag so it can't
		/// clobber a newer upload that landed in the meantime.
		/// </summary>
		private async Task RefreshCacheAsync(string key, BlobDownloadDetails cached)
		{
			await _containers.Cache.GetBlobClient(key).SetMetadataAsync(
				cached.Metadata,
				new BlobRequestConditions { IfMatch = cached.ETag });
		}

		/// <summary>
		/// Awaits a background cache write and logs any failure. These run after the response has been
		/// returned, so failures don't affect the request, but they would otherwise go unnoticed.
		/// </summary>
		private async Task LogCacheFailureAsync(Task operation, string operationName, string key)
		{
			try
			{
				await operation;
			}
			catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status412PreconditionFailed)
			{
				// Another request replaced the entry first; nothing to do.
			}
			catch (Exception ex)
			{
				_log.LogWarning(ex, "Cache {Operation} failed for key {CacheKey}", operationName, key);
			}
		}
	}
}
