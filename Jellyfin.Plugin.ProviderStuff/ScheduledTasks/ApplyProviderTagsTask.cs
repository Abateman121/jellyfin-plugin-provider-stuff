using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ProviderStuff.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ProviderStuff.ScheduledTasks;

/// <summary>
/// Scheduled task to apply provider tags.
/// Ported from original ProviderStuff by kamilkosek.
/// Fixed for Jellyfin 10.11: ILibraryManager.GetItemList() was removed.
/// Replaced with GetItemListResult(query).Items (returns BaseItem[]).
/// </summary>
public class ApplyProviderTagsTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ApplyProviderTagsTask> _logger;
    private readonly ProviderService _providerService;
    private readonly IConfigurationManager _config;
    private readonly ICollectionManager _collectionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplyProviderTagsTask"/> class.
    /// </summary>
    public ApplyProviderTagsTask(
        ILibraryManager libraryManager,
        ILogger<ApplyProviderTagsTask> logger,
        ProviderService providerService,
        IConfigurationManager config,
        ICollectionManager collectionManager)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _providerService = providerService;
        _config = config;
        _collectionManager = collectionManager;
        _logger.LogInformation("Got config: {Config}", _config);
    }

    /// <inheritdoc />
    public string Name => "ProviderStuff: Apply provider tags";

    /// <inheritdoc />
    public string Description => "Fetch providers from TMDB and apply provider:<n> tags to items with TMDB IDs.";

    /// <inheritdoc />
    public string Category => "Metadata";

    /// <inheritdoc />
    public string Key => "providerstuff.applyprovidertags";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.TmdbApiKey) || cfg.Providers is null || cfg.Providers.Length == 0)
        {
            _logger.LogWarning("Plugin not configured. Aborting run.");
            return;
        }

        // Pre-create and cache collections for providers that enable it
        var providersNeedingCollections = cfg.Providers.Where(p => p.CreateCollection).ToArray();
        var collectionIdsByProvider = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var pendingAddsByCollection = new Dictionary<Guid, HashSet<Guid>>();

        if (providersNeedingCollections.Length > 0)
        {
            _logger.LogInformation("Preparing collections for {Count} providers", providersNeedingCollections.Length);
            foreach (var provider in providersNeedingCollections)
            {
                var collectionName = provider.Name;

                // ── 10.11 FIX ──────────────────────────────────────────────────────────
                // GetItemList(InternalItemsQuery) was REMOVED in Jellyfin 10.10/10.11.
                // Replacement: GetItemListResult(query).Items returns BaseItem[]
                // ───────────────────────────────────────────────────────────────────────
                var collections = _libraryManager.GetItemListResult(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                    Name = collectionName,
                    Recursive = true
                }).Items;

                if (collections.Length > 0 && collections[0] is BoxSet existing)
                {
                    collectionIdsByProvider[provider.Name] = existing.Id;
                    pendingAddsByCollection[existing.Id] = new HashSet<Guid>();

                    try
                    {
                        await EnsureCollectionImageAsync(existing, provider, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to ensure image for existing collection '{Collection}'", collectionName);
                    }
                }
                else
                {
                    var boxSet = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions { Name = collectionName }).ConfigureAwait(false);
                    collectionIdsByProvider[provider.Name] = boxSet.Id;
                    pendingAddsByCollection[boxSet.Id] = new HashSet<Guid>();
                    _logger.LogInformation("Created collection '{Collection}'", collectionName);
                    boxSet.PremiereDate = DateTime.UtcNow;
                    await _libraryManager.UpdateItemAsync(boxSet, boxSet, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

                    try
                    {
                        await EnsureCollectionImageAsync(boxSet, provider, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to set image for new collection '{Collection}'", collectionName);
                    }
                }
            }
        }

        // ── 10.11 FIX ──────────────────────────────────────────────────────────────
        // Same fix: GetItemList → GetItemListResult(...).Items
        // Items is BaseItem[] so use .Length instead of .Count
        // ───────────────────────────────────────────────────────────────────────────
        var items = _libraryManager.GetItemListResult(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode },
            Recursive = true
        }).Items;

        var total = items.Length;
        var done = 0;
        _logger.LogInformation("Starting provider tag application for {Total} items", total);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ProcessItemAsync(item, cfg, collectionIdsByProvider, pendingAddsByCollection, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing item {Name}", item.Name);
            }

            done++;
            progress.Report(100.0 * done / total);

            if (done % 100 == 0)
            {
                _logger.LogInformation("Processed {Done}/{Total} items", done, total);
            }
        }

        // Batch add accumulated items to their collections
        foreach (var kvp in pendingAddsByCollection)
        {
            var collectionId = kvp.Key;
            var itemIds = kvp.Value;
            if (itemIds.Count == 0)
            {
                continue;
            }

            try
            {
                await _collectionManager.AddToCollectionAsync(collectionId, itemIds).ConfigureAwait(false);
                _logger.LogInformation("Added {Count} items to collection {CollectionId}", itemIds.Count, collectionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to batch add items to collection {CollectionId}", collectionId);
            }
        }
    }

    private async Task EnsureCollectionImageAsync(BoxSet collection, Provider provider, CancellationToken ct)
    {
        if (collection is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(provider?.ProviderLogoUrl))
        {
            return;
        }

        if (collection.HasImage(ImageType.Primary, 0))
        {
            return;
        }

        var url = provider.ProviderLogoUrl.Trim();
        _logger.LogInformation("Setting primary image for collection '{Name}' from {Url}", collection.Name, url);

        var remoteImage = new ItemImageInfo
        {
            Path = url,
            Type = ImageType.Primary,
            DateModified = DateTime.UtcNow
        };

        collection.AddImage(remoteImage);
        await _libraryManager.UpdateItemAsync(collection, collection, ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);

        try
        {
            var index = collection.GetImageIndex(remoteImage);
            await _libraryManager.ConvertImageToLocal(collection, remoteImage, index, removeOnFailure: true).ConfigureAwait(false);
            await _libraryManager.UpdateImagesAsync(collection, forceUpdate: true).ConfigureAwait(false);
            _logger.LogInformation("Primary image set for collection '{Name}'", collection.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to convert image to local for collection '{Name}'", collection.Name);
        }
    }

    private async Task ProcessItemAsync(
        BaseItem item,
        PluginConfiguration cfg,
        Dictionary<string, Guid> collectionIdsByProvider,
        Dictionary<Guid, HashSet<Guid>> pendingAddsByCollection,
        CancellationToken ct)
    {
        string? tmdbId = null;
        if (item.ProviderIds is not null)
        {
            if (!item.ProviderIds.TryGetValue("Tmdb", out tmdbId))
            {
                tmdbId = item.ProviderIds
                    .FirstOrDefault(kv => string.Equals(kv.Key, "Tmdb", StringComparison.OrdinalIgnoreCase))
                    .Value;
            }
        }

        if (string.IsNullOrWhiteSpace(tmdbId))
        {
            return;
        }

        var contentType = item switch
        {
            Movie => "movie",
            Series => "tv",
            Episode => "tv",
            _ => "movie"
        };

        var providerIds = await _providerService.GetProvidersForAsync(tmdbId, contentType, cfg, ct).ConfigureAwait(false);
        if (providerIds.Count == 0)
        {
            return;
        }

        var matched = new List<string>();
        foreach (var p in cfg.Providers)
        {
            if (p.ProviderIds?.Length > 0 && providerIds.Intersect(p.ProviderIds).Any())
            {
                matched.Add(p.Name);
                if (p.CreateCollection && collectionIdsByProvider.TryGetValue(p.Name, out var collectionId))
                {
                    if (pendingAddsByCollection.TryGetValue(collectionId, out var set))
                    {
                        set.Add(item.Id);
                    }
                }
            }
        }

        if (matched.Count == 0)
        {
            return;
        }

        var tags = item.Tags?.ToList() ?? new List<string>();
        var addedAny = false;
        foreach (var name in matched)
        {
            var tag = $"provider:{name}";
            if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(tag);
                addedAny = true;
            }
        }

        if (addedAny)
        {
            item.Tags = tags.ToArray();
            await _libraryManager.UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            _logger.LogInformation("Applied provider tags to {Name}: {Tags}", item.Name, string.Join(", ", matched));
        }
    }
}
