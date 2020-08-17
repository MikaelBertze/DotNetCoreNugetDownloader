# DotNetCoreNugetDownloader
A tool for downloading .net Core nugets. Intended to be used for creating .net Core only nuget feeds.

1. Clone or download this repo
2. Create a folder named `packages` (in the repo root).
3. Run `dotnet run` from command line 

This will search for nugets with owner 'dotnetframework'. For each nuget, all versions that are not tagged as prerelease will be downloaded. Total download size is about 10GB.


