// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using Microsoft.DotNet.ApiCompatibility.Logging;
using Microsoft.DotNet.ApiCompatibility.Runner;
using NuGet.Client;
using NuGet.ContentModel;
using NuGet.Frameworks;

namespace Microsoft.DotNet.PackageValidation.Validators
{
    /// <summary>
    /// Validates the api surface between compatible frameworks.
    /// </summary>
    public class CompatibleFrameworkInPackageValidator(ISuppressibleLog log,
        IApiCompatRunner apiCompatRunner) : IPackageValidator
    {
        /// <summary>
        /// Validates that the compatible frameworks have compatible surface area.
        /// </summary>
        /// <param name="options"><see cref="PackageValidatorOption"/> to configure the compatible framework in package validation.</param>
        public void Validate(PackageValidatorOption options)
        {
            ApiCompatRunnerOptions apiCompatOptions = new(options.EnableStrictMode);

            // The runtime graph doesn't need to be passed in as compile time assets can't be rid specific.
            ManagedCodeConventions conventions = new(null);
            PatternSet patternSet = options.Package.RefAssets.Any() ?
               conventions.Patterns.CompileRefAssemblies :
               conventions.Patterns.CompileLibAssemblies;

            // If the package doesn't contain at least two frameworks, then there's nothing to compare.
            if (options.Package.FrameworksInPackage.Count < 2)
            {
                return;
            }

            Queue<(NuGetFramework, IReadOnlyList<ContentItem>)> compileAssetsQueue = new();
            foreach (NuGetFramework framework in options.Package.FrameworksInPackage.OrderByDescending(f => f.Version))
            {
                IReadOnlyList<ContentItem>? compileTimeAsset = options.Package.FindBestCompileAssetForFramework(framework);
                if (compileTimeAsset != null && !compileTimeAsset.IsPlaceholderFile())
                {
                    compileAssetsQueue.Enqueue((framework, compileTimeAsset));
                }
            }

            // Iterate as long as assets are available for comparison.
            while (compileAssetsQueue.Count > 1)
            {
                (NuGetFramework framework, IReadOnlyList<ContentItem> compileTimeAsset) = compileAssetsQueue.Dequeue();

                SelectionCriteria managedCriteria = conventions.Criteria.ForFramework(framework);
                ContentItemCollection contentItemCollection = new();
                // The collection won't contain the current compile time asset as it is already dequeued.
                contentItemCollection.Load(compileAssetsQueue.SelectMany(a => a.Item2).Select(a => a.Path));

                // Search for a compatible compile time asset and compare it.
                IList<ContentItem>? compatibleCompileTimeAsset = contentItemCollection.FindBestItemGroup(managedCriteria, patternSet)?.Items;
                if (compatibleCompileTimeAsset != null)
                {
                    apiCompatRunner.QueueApiCompatFromContentItem(log,
                        new ReadOnlyCollection<ContentItem>(compatibleCompileTimeAsset),
                        compileTimeAsset,
                        apiCompatOptions,
                        options.Package);
                }
            }

            if (options.ExecuteApiCompatWorkItems)
            {
                apiCompatRunner.ExecuteWorkItems();
            }
        }
    }
}
