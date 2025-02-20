using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.CommonUtility;
using static StrmAssistant.Options.ExperienceEnhanceOptions;

namespace StrmAssistant.ScheduledTask
{
    public class TriggerMergeMovieTask : ILibraryPostScanTask
    {
        private readonly MergeMultiVersionTask _task;
        
        public TriggerMergeMovieTask(MergeMultiVersionTask task) => _task = task;

        public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            return Plugin.Instance.ExperienceEnhanceStore.GetOptions().MergeMultiVersion
                ? _task.Execute(cancellationToken, progress)
                : Task.CompletedTask;
        }
    }

    public class MergeMultiVersionTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;

        public static readonly AsyncLocal<CollectionFolder> PerLibrary = new AsyncLocal<CollectionFolder>();

        public MergeMultiVersionTask(ILibraryManager libraryManager)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
        }

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.Info("MergeMultiVersion - Scheduled Task Execute");

            var globalScope = Plugin.Instance.ExperienceEnhanceStore.GetOptions()
                .MergeMoviesPreference == MergeScopeOption.GlobalScope;
            _logger.Info("MergeMultiVersion - Across Libraries: " + globalScope);

            long[][] libraryGroups;

            if (!globalScope && PerLibrary.Value != null)
            {
                libraryGroups = new[] { new[] { PerLibrary.Value.InternalId } };
                _logger.Info("MergeMultiVersion - Libraries: " + PerLibrary.Value.Name);
                PerLibrary.Value = null;
            }
            else
            {
                var libraries = Plugin.LibraryApi.GetMovieLibraries();

                if (!libraries.Any())
                {
                    progress.Report(100);
                    _logger.Info("MergeMultiVersion - Scheduled Task Aborted");
                    return Task.CompletedTask;
                }

                _logger.Info("MergeMultiVersion - Libraries: " + string.Join(", ", libraries.Select(l => l.Name)));

                var libraryIds = libraries.Select(l => l.InternalId).ToArray();
                libraryGroups = globalScope
                    ? new[] { libraryIds }
                    : libraryIds.Select(library => new[] { library }).ToArray();
            }
            
            var totalGroups = libraryGroups.Length;
            var groupProgressWeight = 100.0 / totalGroups;
            double cumulativeProgress = 0;

            foreach (var group in libraryGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var groupProgress = new Progress<double>(p =>
                {
                    cumulativeProgress += p * groupProgressWeight / 100;
                    progress.Report(cumulativeProgress);
                });

                MergeMovies(group, groupProgress);
            }

            progress.Report(100);
            _logger.Info("MergeMultiVersion - Scheduled Task Complete");

            return Task.CompletedTask;
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "MergeMultiVersionTask";

        public string Description => Resources.ResourceManager.GetString(
            "MergeMovieTask_Description_Merge_movies_per_library_or_across_libraries_per_preference",
            Plugin.Instance.DefaultUICulture);

        public string Name => "Merge Multi Versions";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public bool IsHidden => false;

        public bool IsEnabled => true;
        
        public bool IsLogged => true;

        private void MergeMovies(long[] parents, IProgress<double> groupProgress = null)
        {
            var movieQuery = new InternalItemsQuery
            {
                Recursive = true,
                ParentIds = parents,
                IncludeItemTypes = new[] { nameof(Movie) }
            };

            var allMovies = _libraryManager.GetItemList(movieQuery).Cast<Movie>().ToList();
            var checkKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tmdb", "imdb", "tvdb" };
            var dupMovies = allMovies.Where(item => item.ProviderIds != null)
                .SelectMany(item => item.ProviderIds
                    .Where(kvp => checkKeys.Contains(kvp.Key))
                    .Select(kvp => new { kvp.Key, kvp.Value, item }))
                .GroupBy(kvp => new { kvp.Key, kvp.Value })
                .Where(g =>
                {
                    var groupItems = g.Select(kvp => kvp.item).ToList();

                    var altVersionCount = g.Sum(kvp =>
                        kvp.item.GetAlternateVersionIds().Count(id => groupItems.Any(i => i.InternalId == id)));

                    return g.Count() != 1 + altVersionCount / g.Count();
                })
                .ToList();
            allMovies.Clear();
            allMovies.TrimExcess();

            if (dupMovies.Count > 0)
            {
                var parentMap = new Dictionary<long, long>(dupMovies.Count);

                foreach (var group in dupMovies)
                {
                    long rootId = -1;

                    foreach (var kvp in group)
                    {
                        var movie = kvp.item;

                        if (!parentMap.ContainsKey(movie.InternalId))
                        {
                            parentMap[movie.InternalId] = movie.InternalId;
                        }

                        if (rootId == -1)
                            rootId = movie.InternalId;
                        else
                            Union(rootId, movie.InternalId, parentMap);
                    }
                }

                var rootIdGroups = parentMap.Values.GroupBy(id => Find(id, parentMap)).ToList();

                var movieLookup = dupMovies.SelectMany(g => g).GroupBy(kvp => Find(kvp.item.InternalId, parentMap))
                    .ToDictionary(g => g.Key, g => g.Select(kvp => kvp.item).Distinct().ToList());

                var total = rootIdGroups.Count;
                var current = 0;

                foreach (var group in rootIdGroups)
                {
                    var movies = group
                        .SelectMany(
                            rootId => movieLookup.TryGetValue(rootId, out var m) ? m : Enumerable.Empty<Movie>())
                        .Distinct()
                        .OfType<BaseItem>()
                        .ToArray();

                    _libraryManager.MergeItems(movies);

                    foreach (var item in movies)
                    {
                        _logger.Info($"MergeMultiVersion - Item merged: {item.Name} - {item.Path}");
                    }

                    current++;
                    _logger.Info($"MergeMultiVersion - Merged group {current} of {total} with {movies.Length} items");

                    var progress = (double)current / total * 100;
                    groupProgress?.Report(progress);
                }

                groupProgress?.Report(100);
            }
        }
    }
}
