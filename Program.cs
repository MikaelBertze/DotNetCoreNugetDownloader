using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace NuGet.Protocol.Samples
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            Console.WriteLine();
            Console.WriteLine("Search packages..");
            await SearchPackages();
        }

        public static async Task SearchPackages()
        {
            ILogger logger = NullLogger.Instance;
            CancellationToken cancellationToken = CancellationToken.None;
            SourceRepository repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            PackageSearchResource searchResource = await repository.GetResourceAsync<PackageSearchResource>();
            FindPackageByIdResource findPackageresource = await repository.GetResourceAsync<FindPackageByIdResource>();
            SearchFilter searchFilter = new SearchFilter(includePrerelease: false);

            var downloadTasks = new List<Task>();

            bool done = false;
            int stepSize = 10;
            int start = 0;
            int counter = 0;
            int maxPackages = 10000;

            while(!done)
            {
                IEnumerable<IPackageSearchMetadata> results = await searchResource.SearchAsync(
                    "owner:dotnetframework",
                    searchFilter,
                    skip: start,
                    take: stepSize,
                    logger,
                    cancellationToken);

                if (results.Any())
                {
                    foreach (IPackageSearchMetadata package in results)
                    {
                        var versions = await package.GetVersionsAsync();
                        Console.WriteLine("-----------------------------------------------");
                        Console.WriteLine($"Package {package.Identity.Id}");
                        Console.WriteLine($"Versions: { string.Join(", ", versions.Select(x => x.Version.ToNormalizedString())) }");

                        foreach(var version in versions)
                        {
                            var filename = $"{package.Identity.Id}.{version.Version.ToString()}.nupkg";
                            var cache = new SourceCacheContext();
                            
                            var copyTask = Task.Run(async () => {
                                using(var stream = File.Open(Path.Join("packages", filename), FileMode.Create)) {
                                    await findPackageresource.CopyNupkgToStreamAsync(package.Identity.Id, version.Version, stream, cache, logger, cancellationToken);
                                }
                            });
                            //var downloader = await findPackageresource.GetPackageDownloaderAsync(package.Identity, cache, logger, cancellationToken);
                            //downloadTasks.Add(downloader. CopyNupkgFileToAsync(Path.Join("packages", filename), cancellationToken));
                            downloadTasks.Add(copyTask);
                        }
                        counter++;
                        if (counter >= maxPackages)
                        {
                            done = true;
                            break;
                        }
                    }
                    start += stepSize;
                }
                else{
                    done = true;
                }
            }
            
            Console.WriteLine($"Done searching for packages. Packages found: {counter}");
            Console.WriteLine("Waiting for downloads to finish...");
            Task.WaitAll(downloadTasks.ToArray());
            Console.WriteLine("DONE!");
        }
    }
}
