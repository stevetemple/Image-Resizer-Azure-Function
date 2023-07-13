

# Image Resizing Azure Function

So you're running a website that has a bunch of media etc that gets resized on the site. This might not be ideal, image processing can be very CPU intensive, it's a potential DDoS attack vector and it's just plain nicer to shift that off to somewhere else.

This will run an Azure Function that will do the Image Resizing for you without needing to run it on the website itself, it assumes that your media is on Azure Blob Storage and it'll take that resize it and cache the resized images back onto the blob storage.

The intended use is to then sit this behind a CDN or have media requests routed to this function for sites sitting behind something like Azure Front Door.

## Setup 

[![Deploy to Azure](/images/deploytoazure.png)](https://portal.azure.com/#create/Microsoft.Template/uri/https://azuredeploy.json)

You'll need to setup 3 app settings:

`ImageConnectionString`
The connection string to the blob containing the media to resize

`Container`
The name of the container which contains the images you want to resize

`CacheContainer`
The name of the container to use a cache of the resized images

The resizer uses the amazing ImageSharp to resize and will be listening on the path `/api/<pathtoimage>`

It supports the format, quality and resize commands from [ImageSharp.Web](https://docs.sixlabors.com/articles/imagesharp.web/processingcommands.html) passed in the querystring

## Usage

It's designed to sit behind a CDN that will do the caching. The function will resize on each request. 

Setup the CDN to cache each unique URL (including querystring). 

Also set the CDN to output cache headers to tell the client to cache for 365 days.
