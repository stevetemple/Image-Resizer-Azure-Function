# Image Resizing Azure Function
An Azure Function that will do the Image Resizing without needing to run it on the website itself

## Setup 
You'll need to setup 3 app settings:

`ImageConnectionString`
The connection string to the blob containing the media to resize

`Container`
The name of the container which contains the images you want to resize

`CacheContainer`
The name of the container to use a cache of the resized images

The resizer uses ImageSharp to resize and will be listening on the path `/api/<pathtoimage>`

It supports the format, quality and resize commands from [ImageSharp.Web](https://docs.sixlabors.com/articles/imagesharp.web/processingcommands.html) passed in the querystring

## Usage

It's designed to sit behind a CDN that will do the caching. The function will resize on each request. 

Setup the CDN to cache each unique URL (including querystring). 

Also set the CDN to output cache headers to tell the client to cache for 365 days.
