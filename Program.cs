using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace NuGet.Protocol.Samples
{
    public class CopyToFeedOptions
    {
        [Option('k', "api_key", Required=false, HelpText="Target feed API key")]
        public string API_KEY {get; set;}
        
        [Option('t', "target_url", Required=true, HelpText="Target nuget repository url (V3 API")]
        public string TargetNugetRepositoryUrl {get;set;}
        
        [Option('s', "source_url", Required=false, HelpText="Source nuget repository url (V3 API)", Default="https://api.nuget.org/v3/index.json")]
        public string SourceNugetRepositoryUrl {get;set;}
        
        [Option('q', "query", Required=true, HelpText="Search query")]
        public string SearchString {get;set;}

        [Option('p', "prereleases", Required=false, HelpText="Include prereleases", Default=false)]
        public bool Prerelease {get;set;}

        [Option('d', "dryrun", Required=false, HelpText="No upload to target feed", Default=false)]
        public bool Dryrun {get;set;}
    }

    public class Program
    {
        private static string _sourceRepositoryUrl;

        private static string _targetRepositoryUrl;

        private static string _targetApiKey;
        private static bool _preReleases;

        private static bool _dryRun;

        private static string _searchString;

        private static ConcurrentBag<(string, bool)> _bag;


        public static async Task Main(string[] args)
        {
            CopyToFeedOptions arguments = null;
            var result = Parser.Default.ParseArguments<CopyToFeedOptions>(args).WithParsed(x => arguments = x);
            if (result.Tag == ParserResultType.NotParsed)
            {
                return;
            }
            _bag = new ConcurrentBag<(string, bool)>();
            _targetRepositoryUrl = arguments.TargetNugetRepositoryUrl;
            _sourceRepositoryUrl = arguments.SourceNugetRepositoryUrl;
            _preReleases = arguments.Prerelease;
            _targetApiKey = arguments.API_KEY;
            _searchString = arguments.SearchString;
            _dryRun = arguments.Dryrun;

            Console.WriteLine();
            Console.WriteLine("Searching packages in source...");
            var sourcePackages = await SearchPackages(Repository.Factory.GetCoreV3(_sourceRepositoryUrl), _searchString, _preReleases);
            Console.WriteLine($"Found {sourcePackages.Count()} packages");
            Console.Write("Fetching all versions for all packages");
            
            // limiting fetching of metadata to 30 threads.
            List<IPackageSearchMetadata> versions = new List<IPackageSearchMetadata>();
            var skip = 0;
            var take = 30;
            while(true) {
                Console.Write(".");
                var tasks = sourcePackages.Skip(skip).Take(take).Select(p => Task.Run( async () => {
                    var metas = await GetPackageMetadatas(arguments.SourceNugetRepositoryUrl, p.Identity.Id, arguments.Prerelease);
                    return metas;
                }));

                if (tasks.Any())
                {
                    Task.WaitAll(tasks.ToArray());
                    foreach(var t in tasks)
                    {
                        if (!t.Result.Any())
                            Console.WriteLine("Error");
                        versions.AddRange(t.Result);
                    }
                    skip += take;
                }
                else
                    break;
            }
            Console.WriteLine();
            Console.WriteLine($"Total number of nugets: {versions.Count()}");
            Console.WriteLine($"Continue filtering towards target field? [Y/N]");
            var a = Console.ReadLine().ToLower();
            if (a == "n") {
                return;
            }

            var total = versions.Count();
            var counter = 0;
            var uploadTasks = new List<Task>();     
            foreach(var p in versions) {
                UploadToTargetIfNotExistInTarget(p).Wait();
                Console.WriteLine("Counter: "+ ++counter + "/" + total);
            }
            

            Console.WriteLine("-------- SUMMARY --------");
            Console.WriteLine("Uploaded versions");
            Console.WriteLine(string.Join(Environment.NewLine, _bag.Where(x => x.Item2).Select(x => x.Item1)));
            Console.WriteLine();
            Console.WriteLine("Skipped versions (already in target)");
            Console.WriteLine(string.Join(Environment.NewLine, _bag.Where(x => !x.Item2).Select(x => x.Item1)));
            Console.WriteLine();
            Console.WriteLine($"Nmber of skipped nugets: {_bag.Where(x => !x.Item2).Count()}");
            Console.WriteLine($"Nmber of downloaded nugets: {_bag.Where(x => x.Item2).Count()}");
        }

        private static async Task UploadToTargetIfNotExistInTarget(IPackageSearchMetadata package)
        {
            ILogger logger = NullLogger.Instance;
            CancellationToken cancellationToken = CancellationToken.None;
            SourceRepository sourceRepository = Repository.Factory.GetCoreV3(_sourceRepositoryUrl);
            FindPackageByIdResource findPackageresource = await sourceRepository.GetResourceAsync<FindPackageByIdResource>();
            var cache = new SourceCacheContext();
            if (!await PackageExistInTarget(package.Identity.Id, package.Identity.Version))
            {
                _bag.Add(($"{package.Identity.Id} {package.Identity.Version}", true));
                // Download from source
                //Console.WriteLine($"Downloading: {package.Identity.Id} | {package.Identity.Version}");
                Console.WriteLine($"Uploading: {package.Identity.Id} | {package.Identity.Version}");
                if (!_dryRun)
                {
                
                    var filename = Path.GetTempFileName();
                    using(var stream = File.Open(filename, FileMode.Create)) {
                        await findPackageresource.CopyNupkgToStreamAsync(package.Identity.Id, package.Identity.Version, stream, cache, logger, cancellationToken);
                    }
                    // upload to target
                    await UploadPackage(filename);
                    // remove temp file
                    File.Delete(filename);
                }
            }
            else {
                _bag.Add(($"{package.Identity.Id} {package.Identity.Version}", false));
            }
            
        }

        private static async Task<bool> PackageExistInTarget(string packageId, NuGetVersion version) 
        {
            Console.Write("Id: " + packageId + " | Version: " + version + " : ");
            
            var meta = await GetPackageMetadatas(_targetRepositoryUrl, packageId, _preReleases);

            foreach(var p in meta)
            {
                if (p.Identity.Version == version)
                {
                    Console.WriteLine("Exist");
                    return true;
                }
            }
            Console.WriteLine("Missing");
            return false;
        }

        private static async Task<IEnumerable<IPackageSearchMetadata>> GetPackageMetadatas(string nugetSource, string packageId, bool includePrereleases)
        {
            ILogger logger = NullLogger.Instance;

            List<Lazy<INuGetResourceProvider>> providers = new List<Lazy<INuGetResourceProvider>>();
            providers.AddRange(Repository.Provider.GetCoreV3());  // Add v3 API support
            CancellationToken cancellationToken = CancellationToken.None;
            SourceCacheContext cache = new SourceCacheContext();
            SourceRepository repository = Repository.Factory.GetCoreV3(nugetSource);
            PackageMetadataResource resource = await repository.GetResourceAsync<PackageMetadataResource>();

            var result = await resource.GetMetadataAsync(
                packageId,
                includePrereleases,
                false,
                logger,
                cancellationToken);

            return result;
        }

        private static async Task<IEnumerable<IPackageSearchMetadata>> SearchPackages(SourceRepository repository, string searchQuery, bool includePrereleases)
        {
            List<IPackageSearchMetadata> packages = new List<IPackageSearchMetadata>();
            ILogger logger = NullLogger.Instance;
            CancellationToken cancellationToken = CancellationToken.None;
            PackageSearchResource searchResource = await repository.GetResourceAsync<PackageSearchResource>();
            FindPackageByIdResource findPackageresource = await repository.GetResourceAsync<FindPackageByIdResource>();
            SearchFilter searchFilter = new SearchFilter(includePrerelease: includePrereleases);

            var downloadTasks = new List<Task>();

            bool done = false;
            int stepSize = 100;
            int start = 0;
            
            while(!done)
            {
                IEnumerable<IPackageSearchMetadata> results = await searchResource.SearchAsync(
                    searchQuery,
                    searchFilter,
                    skip: start,
                    take: stepSize,
                    logger,
                    cancellationToken);

                if (results.Any())
                {
                    packages.AddRange(results);
                    start += stepSize;
                }
                else{
                    done = true;
                }
            }
            return packages;
        }
        
        public async static Task UploadPackage(string packagePath)
        {
            SourceRepository repository = Repository.Factory.GetCoreV3(_targetRepositoryUrl);
            using (var sourceCacheContext = new SourceCacheContext())
            {
                PackageUpdateResource uploadResource = await repository.GetResourceAsync<PackageUpdateResource>();
                await uploadResource.Push(packagePath, null, 120, false, (param) => _targetApiKey, null, NullLogger.Instance);
            }
        }
    }
}
