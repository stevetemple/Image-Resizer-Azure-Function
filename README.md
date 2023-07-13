

# Image Resizing Azure Function

If you're running a website that has a a lot of images/media etc that gets resized on demand as part of the site. You may find this might not be ideal, image processing can be very CPU intensive and so i've found it nicer to shift that off to somewhere else and let Azure worry about scaling for it.

This will run an Azure Function that will do the image resizing and formatting for you without needing to run it on the website itself, it assumes that your media is on Azure Blob Storage and it'll take that resize it and cache the resized images back onto the blob storage.

The intended use is to then sit this behind a CDN or have media requests routed to this function for sites sitting behind something like Azure Front Door.

## Setup 

Add the new resources at Azure:

[![Deploy to Azure](/images/deploytoazure.png)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fstevetemple%2FImage-Resizer-Azure-Function%2Fmain%2Fazuredeploy.json)

You'll need to setup 3 settings ready:

`ImageConnectionString`
The connection string to the blob containing the media to resize

`Container`
The name of the container which contains the images you want to resize

`CacheContainer`
The name of the container to use a cache of the resized images

The resizer uses the amazing ImageSharp to resize and will be listening on the path `/api/<pathtoimage>` so if in your container the image is at `media/1234/image.jpg` you'll want to pass `/api/media/1234/image.jpg?width=100`

It supports the format, quality and resize commands from [ImageSharp.Web](https://docs.sixlabors.com/articles/imagesharp.web/processingcommands.html) passed in the querystring

## Usage

It's designed to sit behind a CDN that will do the caching. The function will resize on each request. 

Make sure you setup the CDN to cache each unique URL (including querystring). 

This library sends headers to the CDN to tell it to cache for up to 365 days.

## License

Copyright © [Steve Temple](https://github.com/stevetemple).

All source code is licensed under the Mozilla Public License.

## Acknowledgements

### Developers

Steve Temple - ([GitHub](https://github.com/stevetemple), [Mastodon](https://umbracocommunity.social/@steve_gibe), [Twitter](https://twitter.com/Steve_Gibe))

### Dependencies

This project is built entirely around and would not be possible without [ImageSharp](https://docs.sixlabors.com/articles/imagesharp/index.html) please check that out and [support them](https://github.com/SixLabors/ImageSharp#support-six-labors)