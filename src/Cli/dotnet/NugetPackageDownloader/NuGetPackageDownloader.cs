// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.ToolPackage;
using Microsoft.DotNet.Tools;
using Microsoft.Extensions.EnvironmentAbstractions;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Microsoft.DotNet.Cli.NuGetPackageDownloader
{
    internal class NuGetPackageDownloader : INuGetPackageDownloader
    {
        private readonly SourceCacheContext _cacheSettings = new SourceCacheContext
        {
            NoCache = true, DirectDownload = true
        };

        private readonly IFilePermissionSetter _filePermissionSetter;
        private readonly ILogger _logger;
        private readonly DirectoryPath _packageInstallDir;

        public NuGetPackageDownloader(DirectoryPath packageInstallDir, IFilePermissionSetter filePermissionSetter = null, ILogger logger = null)
        {
            _packageInstallDir = packageInstallDir;
            _logger = logger ?? new NuGetConsoleLogger();
            _filePermissionSetter = new FilePermissionSetter();
        }

        public async Task<string> DownloadPackageAsync(PackageId packageId,
            NuGetVersion packageVersion = null,
            PackageSourceLocation packageSourceLocation = null,
            bool includePreview = false,
            DirectoryPath? downloadFolder = null)
        {
            CancellationToken cancellationToken = CancellationToken.None;

            (var source, var resolvedPackageVersion) = await GetPackageSourceAndVerion(packageId, packageVersion, packageSourceLocation, includePreview);

            FindPackageByIdResource resource = null;
            SourceRepository repository = Repository.Factory.GetCoreV3(source);

            resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken)
                .ConfigureAwait(false);

            if (resource == null)
            {
                throw new NuGetPackageDownloaderException(
                    string.Format(LocalizableStrings.FailedToLoadNuGetSource, source.Source));
            }

            string nupkgPath = downloadFolder == null || !downloadFolder.HasValue ?
                Path.Combine(_packageInstallDir.Value, packageId.ToString(), resolvedPackageVersion.ToNormalizedString(), $"{packageId}.{resolvedPackageVersion.ToNormalizedString()}.nupkg") :
                Path.Combine(downloadFolder.Value.Value, $"{packageId}.{resolvedPackageVersion.ToNormalizedString()}.nupkg");
            Directory.CreateDirectory(Path.GetDirectoryName(nupkgPath));
            using FileStream destinationStream = File.Create(nupkgPath);
            bool success = await resource.CopyNupkgToStreamAsync(
                packageId.ToString(),
                resolvedPackageVersion,
                destinationStream,
                _cacheSettings,
                _logger,
                cancellationToken);

            if (!success)
            {
                throw new NuGetPackageDownloaderException(
                    $"Downloading {packageId} version {resolvedPackageVersion.ToNormalizedString()} failed");
            }

            return nupkgPath;
        }

        public async Task<string> GetPackageUrl(PackageId packageId,
            NuGetVersion packageVersion = null,
            PackageSourceLocation packageSourceLocation = null,
            bool includePreview = false)
        {
            (var source, var resolvedPackageVersion) = await GetPackageSourceAndVerion(packageId, packageVersion, packageSourceLocation, includePreview);

            SourceRepository repository = Repository.Factory.GetCoreV3(source);

            ServiceIndexResourceV3 serviceIndexResource = repository.GetResourceAsync<ServiceIndexResourceV3>().Result;
            IReadOnlyList<Uri> packageBaseAddress =
                serviceIndexResource?.GetServiceEntryUris(ServiceTypes.PackageBaseAddress);

            return GetNupkgUrl(packageBaseAddress.First().ToString(), packageId, resolvedPackageVersion);
        }

        public async Task<IEnumerable<string>> ExtractPackageAsync(string packagePath, DirectoryPath targetFolder)
        {
            await using FileStream packageStream = File.OpenRead(packagePath);
            PackageFolderReader packageReader = new PackageFolderReader(targetFolder.ToString());
            PackageExtractionContext packageExtractionContext = new PackageExtractionContext(
                PackageSaveMode.Defaultv3,
                XmlDocFileSaveMode.None,
                null,
                _logger);
            NuGetPackagePathResolver packagePathResolver = new NuGetPackagePathResolver(targetFolder.ToString());
            CancellationToken cancellationToken = CancellationToken.None;

            return await PackageExtractor.ExtractPackageAsync(
                targetFolder.ToString(),
                packageStream,
                packagePathResolver,
                packageExtractionContext,
                cancellationToken);
        }

        private async Task<(PackageSource, NuGetVersion)> GetPackageSourceAndVerion(PackageId packageId,
            NuGetVersion packageVersion = null,
            PackageSourceLocation packageSourceLocation = null,
            bool includePreview = false)
        {
            CancellationToken cancellationToken = CancellationToken.None;

            IPackageSearchMetadata packageMetadata;

            IEnumerable<PackageSource> packagesSources = LoadNuGetSources(packageSourceLocation);
            PackageSource source;

            if (packageVersion is null)
            {
                (source, packageMetadata) = await GetLatestVersionInternalAsync(packageId.ToString(), packagesSources,
                    includePreview, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                packageVersion = new NuGetVersion(packageVersion);
                (source, packageMetadata) =
                    await GetPackageMetadataAsync(packageId.ToString(), packageVersion, packagesSources,
                        cancellationToken).ConfigureAwait(false);
            }

            packageVersion = packageMetadata.Identity.Version;

            return (source, packageVersion);
        }

        private string GetNupkgUrl(string baseUri, PackageId id, NuGetVersion version) =>
            baseUri + id.ToString() + "/" + version.ToNormalizedString() + "/" + id.ToString() +
            "." + version.ToNormalizedString() + ".nupkg";

        private IEnumerable<PackageSource> LoadNuGetSources(PackageSourceLocation packageSourceLocation = null)
        {
            IEnumerable<PackageSource> defaultSources = new List<PackageSource>();
            string currentDirectory = Directory.GetCurrentDirectory();
            ISettings settings;
            if (packageSourceLocation?.NugetConfig != null)
            {
                string nugetConfigParentDirectory =
                    packageSourceLocation.NugetConfig.Value.GetDirectoryPath().Value;
                string nugetConfigFileName = Path.GetFileName(packageSourceLocation.NugetConfig.Value.Value);
                settings = Settings.LoadSpecificSettings(nugetConfigParentDirectory,
                    nugetConfigFileName);
            }
            else
            {
                settings = Settings.LoadDefaultSettings(
                    packageSourceLocation?.RootConfigDirectory?.Value ?? currentDirectory);
            }

            PackageSourceProvider packageSourceProvider = new PackageSourceProvider(settings);
            defaultSources = packageSourceProvider.LoadPackageSources().Where(source => source.IsEnabled);

            if (!packageSourceLocation?.SourceFeedOverrides.Any() ?? true)
            {
                if (!defaultSources.Any())
                {
                    throw new NuGetPackageDownloaderException("No NuGet sources are defined or enabled");
                }

                return defaultSources;
            }

            List<PackageSource> customSources = new List<PackageSource>();
            foreach (string source in packageSourceLocation?.SourceFeedOverrides)
            {
                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                PackageSource packageSource = new PackageSource(source);
                if (packageSource.TrySourceAsUri == null)
                {
                    _logger.LogWarning(string.Format(
                        LocalizableStrings.FailedToLoadNuGetSource,
                        source));
                    continue;
                }

                customSources.Add(packageSource);
            }

            IEnumerable<PackageSource> retrievedSources;
            if (packageSourceLocation != null && packageSourceLocation.SourceFeedOverrides.Any())
            {
                retrievedSources = customSources;
            }
            else
            {
                retrievedSources = defaultSources;
            }

            if (!retrievedSources.Any())
            {
                throw new NuGetPackageDownloaderException("No NuGet sources are defined or enabled");
            }

            return retrievedSources;
        }

        private async Task<(PackageSource, IPackageSearchMetadata)> GetLatestVersionInternalAsync(
            string packageIdentifier, IEnumerable<PackageSource> packageSources, bool includePreview,
            CancellationToken cancellationToken)
        {
            if (packageSources == null)
            {
                throw new ArgumentNullException(nameof(packageSources));
            }

            if (string.IsNullOrWhiteSpace(packageIdentifier))
            {
                throw new ArgumentException($"{nameof(packageIdentifier)} cannot be null or empty",
                    nameof(packageIdentifier));
            }

            (PackageSource source, IEnumerable<IPackageSearchMetadata> foundPackages)[] foundPackagesBySource =
                await Task.WhenAll(
                        packageSources.Select(source => GetPackageMetadataAsync(source, packageIdentifier,
                            true, cancellationToken)))
                    .ConfigureAwait(false);

            if (!foundPackagesBySource.Any())
            {
                throw new NuGetPackageDownloaderException(string.Format(LocalizableStrings.FailedToLoadNuGetSource,
                    string.Join(" ", packageSources.Select(s => s.Source))));
            }

            IEnumerable<(PackageSource source, IPackageSearchMetadata package)> accumulativeSearchResults =
                foundPackagesBySource
                    .SelectMany(result => result.foundPackages.Select(package => (result.source, package)));

            if (!accumulativeSearchResults.Any())
            {
                throw new NuGetPackageInstallerException(
                    string.Format(
                        LocalizableStrings.IsNotFoundInNuGetFeeds,
                        packageIdentifier,
                        string.Join(", ", packageSources.Select(source => source.Source))));
            }

            if (!includePreview)
            {
                var stableVersions = accumulativeSearchResults
                    .Where(r => !r.package.Identity.Version.IsPrerelease);

                if (stableVersions.Any())
                {
                    return stableVersions.MaxBy(r => r.package.Identity.Version);
                }
            }

            (PackageSource, IPackageSearchMetadata) latestVersion = accumulativeSearchResults
                .MaxBy(r => r.package.Identity.Version);
            return latestVersion;
        }

        private async Task<(PackageSource, IPackageSearchMetadata)> GetPackageMetadataAsync(string packageIdentifier,
            NuGetVersion packageVersion, IEnumerable<PackageSource> sources, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(packageIdentifier))
            {
                throw new ArgumentException($"{nameof(packageIdentifier)} cannot be null or empty",
                    nameof(packageIdentifier));
            }

            _ = packageVersion ?? throw new ArgumentNullException(nameof(packageVersion));
            _ = sources ?? throw new ArgumentNullException(nameof(sources));

            bool atLeastOneSourceValid = false;
            using CancellationTokenSource linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            List<Task<(PackageSource source, IEnumerable<IPackageSearchMetadata> foundPackages)>> tasks = sources
                .Select(source =>
                    GetPackageMetadataAsync(source, packageIdentifier, true, linkedCts.Token)).ToList();
            while (tasks.Any())
            {
                Task<(PackageSource source, IEnumerable<IPackageSearchMetadata> foundPackages)> finishedTask =
                    await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(finishedTask);
                (PackageSource source, IEnumerable<IPackageSearchMetadata> foundPackages) result =
                    await finishedTask.ConfigureAwait(false);
                if (result.foundPackages == null)
                {
                    continue;
                }

                atLeastOneSourceValid = true;
                IPackageSearchMetadata matchedVersion =
                    result.foundPackages.FirstOrDefault(package => package.Identity.Version == packageVersion);
                if (matchedVersion != null)
                {
                    linkedCts.Cancel();
                    return (result.source, matchedVersion);
                }
            }

            if (!atLeastOneSourceValid)
            {
                throw new NuGetPackageDownloaderException(string.Format(LocalizableStrings.FailedToLoadNuGetSource,
                    string.Join(";", sources.Select(s => s.Source))));
            }

            throw new NuGetPackageDownloaderException(string.Format(LocalizableStrings.IsNotFoundInNuGetFeeds,
                $"{packageIdentifier}::{packageVersion}", string.Join(";", sources.Select(s => s.Source))));
        }

        private async Task<(PackageSource source, IEnumerable<IPackageSearchMetadata> foundPackages)>
            GetPackageMetadataAsync(PackageSource source, string packageIdentifier, bool includePrerelease = false,
                CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(packageIdentifier))
            {
                throw new ArgumentException($"{nameof(packageIdentifier)} cannot be null or empty",
                    nameof(packageIdentifier));
            }

            _ = source ?? throw new ArgumentNullException(nameof(source));

            SourceRepository repository = Repository.Factory.GetCoreV3(source);
            PackageMetadataResource resource = await repository
                .GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);
            IEnumerable<IPackageSearchMetadata> foundPackages = await resource.GetMetadataAsync(
                packageIdentifier,
                includePrerelease,
                false,
                _cacheSettings,
                _logger,
                cancellationToken).ConfigureAwait(false);

            return (source, foundPackages);
        }
    }
}
