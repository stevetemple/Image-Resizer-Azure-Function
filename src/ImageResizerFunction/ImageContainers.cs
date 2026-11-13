using Azure.Identity;
using Azure.Storage.Blobs;
using System;
using System.Threading.Tasks;

namespace ImageResizerFunction
{
	/// <summary>
	/// Holds the source and cache container clients. Registered as a singleton so the
	/// credential (and its access token) and HTTP pipeline are reused across invocations.
	/// </summary>
	public class ImageContainers
	{
		private readonly object _ensureLock = new();
		private Task _ensureCacheContainer;

		public ImageContainers()
		{
			var storageUrl = Environment.GetEnvironmentVariable("ImageConnectionString");
			var containerName = Environment.GetEnvironmentVariable("Container").ToLower();
			var cacheContainerName = Environment.GetEnvironmentVariable("CacheContainer").ToLower();

			if (!storageUrl.EndsWith("/"))
			{
				storageUrl += "/";
			}

			var credential = new DefaultAzureCredential(true);

			Source = new BlobContainerClient(new Uri($"{storageUrl}{containerName}"), credential);
			Cache = new BlobContainerClient(new Uri($"{storageUrl}{cacheContainerName}"), credential);
		}

		public BlobContainerClient Source { get; }

		public BlobContainerClient Cache { get; }

		/// <summary>
		/// Creates the cache container on first use only. A failed attempt is retried on the next call.
		/// </summary>
		public Task EnsureCacheContainerAsync()
		{
			lock (_ensureLock)
			{
				if (_ensureCacheContainer == null || _ensureCacheContainer.IsFaulted || _ensureCacheContainer.IsCanceled)
				{
					_ensureCacheContainer = Cache.CreateIfNotExistsAsync();
				}

				return _ensureCacheContainer;
			}
		}
	}
}
