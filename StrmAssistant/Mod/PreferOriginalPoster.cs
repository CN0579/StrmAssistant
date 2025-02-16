using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Common;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class PreferOriginalPoster : PatchBase<PreferOriginalPoster>
    {
        internal class ContextItem
        {
            public string TmdbId { get; set; }
            public string ImdbId { get; set; }
            public string TvdbId { get; set; }
            public string OriginalLanguage { get; set; }
        }

        private static Assembly _movieDbAssembly;
        private static MethodInfo _getMovieInfo;
        private static MethodInfo _ensureSeriesInfo;
        private static PropertyInfo _tmdbIdMovieDataTmdb;
        private static PropertyInfo _imdbIdMovieDataTmdb;
        private static PropertyInfo _originalLanguageMovieDataTmdb;
        private static PropertyInfo _tmdbIdSeriesDataTmdb;
        private static PropertyInfo _languagesSeriesDataTmdb;
        private static PropertyInfo _movieDataTmdbTaskResult;
        private static PropertyInfo _seriesDataTmdbTaskResult;

        private static Assembly _tvdbAssembly;
        private static MethodInfo _ensureMovieInfoTvdb;
        private static MethodInfo _ensureSeriesInfoTvdb;
        private static PropertyInfo _tvdbIdMovieDataTvdb;
        private static PropertyInfo _originalLanguageMovieDataTvdb;
        private static PropertyInfo _tvdbIdSeriesDataTvdb;
        private static PropertyInfo _originalLanguageSeriesDataTvdb;
        private static PropertyInfo _movieDataTvdbTaskResult;
        private static PropertyInfo _seriesDataTvdbTaskResult;

        private static MethodInfo _getBackdrops;
        private static PropertyInfo file_path;
        private static PropertyInfo iso_639_1;

        private static MethodInfo _getAvailableRemoteImages;

        private static readonly ConcurrentDictionary<string, ContextItem> CurrentItemsByTmdbId =
            new ConcurrentDictionary<string, ContextItem>();
        private static readonly ConcurrentDictionary<string, ContextItem> CurrentItemsByImdbId =
            new ConcurrentDictionary<string, ContextItem>();
        private static readonly ConcurrentDictionary<string, ContextItem> CurrentItemsByTvdbId =
            new ConcurrentDictionary<string, ContextItem>();
        private static readonly ConcurrentDictionary<string, string> BackdropByLanguage =
            new ConcurrentDictionary<string, string>();

        private static readonly AsyncLocal<ContextItem> CurrentLookupItem = new AsyncLocal<ContextItem>();

        private static readonly AsyncLocal<bool> WasCalledByImageProvider = new AsyncLocal<bool>();

        public PreferOriginalPoster()
        {
            Initialize();

            if (Plugin.Instance.MetadataEnhanceStore.GetOptions().PreferOriginalPoster)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            _movieDbAssembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "MovieDb");

            if (_movieDbAssembly != null)
            {
                var movieDbImageProvider = _movieDbAssembly.GetType("MovieDb.MovieDbImageProvider");
                _getMovieInfo = movieDbImageProvider.GetMethod("GetMovieInfo",
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(BaseItem), typeof(string), typeof(IJsonSerializer), typeof(CancellationToken) },
                    null);
                var completeMovieData = _movieDbAssembly.GetType("MovieDb.MovieDbProvider")
                    .GetNestedType("CompleteMovieData", BindingFlags.NonPublic);
                _tmdbIdMovieDataTmdb = completeMovieData.GetProperty("id");
                _imdbIdMovieDataTmdb = completeMovieData.GetProperty("imdb_id");
                _originalLanguageMovieDataTmdb = completeMovieData.GetProperty("original_language");

                var movieDbSeriesProvider = _movieDbAssembly.GetType("MovieDb.MovieDbSeriesProvider");
                _ensureSeriesInfo = movieDbSeriesProvider.GetMethod("EnsureSeriesInfo",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var seriesRootObject = movieDbSeriesProvider.GetNestedType("SeriesRootObject", BindingFlags.Public);
                _tmdbIdSeriesDataTmdb = seriesRootObject.GetProperty("id");
                _languagesSeriesDataTmdb = seriesRootObject.GetProperty("languages");

                var movieDbProviderBase = _movieDbAssembly.GetType("MovieDb.MovieDbProviderBase");
                _getBackdrops = movieDbProviderBase.GetMethod("GetBackdrops",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var tmdbImage = _movieDbAssembly.GetType("MovieDb.TmdbImage");
                file_path = tmdbImage.GetProperty("file_path");
                iso_639_1 = tmdbImage.GetProperty("iso_639_1");
            }
            else
            {
                Plugin.Instance.Logger.Warn("OriginalPoster - MovieDb plugin is not installed");
            }

            _tvdbAssembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Tvdb");

            if (_tvdbAssembly != null)
            {
                var tvdbMovieProvider = _tvdbAssembly.GetType("Tvdb.TvdbMovieProvider");
                _ensureMovieInfoTvdb = tvdbMovieProvider.GetMethod("EnsureMovieInfo",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var tvdbSeriesProvider = _tvdbAssembly.GetType("Tvdb.TvdbSeriesProvider");
                _ensureSeriesInfoTvdb = tvdbSeriesProvider.GetMethod("EnsureSeriesInfo",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var movieData = _tvdbAssembly.GetType("Tvdb.MovieData");
                _tvdbIdMovieDataTvdb = movieData.GetProperty("id");
                _originalLanguageMovieDataTvdb = movieData.GetProperty("originalLanguage");
                var seriesData = _tvdbAssembly.GetType("Tvdb.SeriesData");
                _tvdbIdSeriesDataTvdb = seriesData.GetProperty("id");
                _originalLanguageSeriesDataTvdb = seriesData.GetProperty("originalLanguage");
            }
            else
            {
                Plugin.Instance.Logger.Warn("OriginalPoster - Tvdb plugin is not installed");
            }

            if (_movieDbAssembly != null || _tvdbAssembly != null)
            {
                var embyProvidersAssembly = Assembly.Load("Emby.Providers");
                var providerManager = embyProvidersAssembly.GetType("Emby.Providers.Manager.ProviderManager");
                _getAvailableRemoteImages = providerManager.GetMethod("GetAvailableRemoteImages",
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new[]
                    {
                        typeof(BaseItem), typeof(LibraryOptions), typeof(RemoteImageQuery),
                        typeof(IDirectoryService), typeof(CancellationToken)
                    }, null);
            }
            else
            {
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                PatchTracker.IsSupported = false;
            }
        }

        protected override void Prepare(bool apply)
        {
            if (_movieDbAssembly != null)
            {
                PatchUnpatch(PatchTracker, apply, _getMovieInfo, postfix: nameof(GetMovieInfoTmdbPostfix));
                PatchUnpatch(PatchTracker, apply, _ensureSeriesInfo, postfix: nameof(EnsureSeriesInfoTmdbPostfix));
                PatchUnpatch(PatchTracker, apply, _getBackdrops, postfix: nameof(GetBackdropsPostfix));
            }

            if (_tvdbAssembly != null)
            {
                PatchUnpatch(PatchTracker, apply, _ensureMovieInfoTvdb, postfix: nameof(EnsureMovieInfoTvdbPostfix));
                PatchUnpatch(PatchTracker, apply, _ensureSeriesInfoTvdb, postfix: nameof(EnsureSeriesInfoTvdbPostfix));
            }

            PatchUnpatch(PatchTracker, apply, _getAvailableRemoteImages,
                prefix: nameof(GetAvailableRemoteImagesPrefix), postfix: nameof(GetAvailableRemoteImagesPostfix));
        }

        private static void AddContextItem(string tmdbId, string imdbId, string tvdbId)
        {
            if (tmdbId == null && imdbId == null && tvdbId == null) return;

            var item = new ContextItem { TmdbId = tmdbId, ImdbId = imdbId, TvdbId = tvdbId };

            if (tmdbId != null) CurrentItemsByTmdbId[tmdbId] = item;

            if (imdbId != null) CurrentItemsByImdbId[imdbId] = item;

            if (tvdbId != null) CurrentItemsByTvdbId[tvdbId] = item;

            CurrentLookupItem.Value = new ContextItem { TmdbId = tmdbId, ImdbId = imdbId, TvdbId = tvdbId };
        }

        private static void UpdateOriginalLanguage(string tmdbId, string imdbId, string tvdbId, string originalLanguage)
        {
            ContextItem itemToUpdate = null;

            if (tmdbId != null) CurrentItemsByTmdbId.TryGetValue(tmdbId, out itemToUpdate);

            if (itemToUpdate == null && imdbId != null) CurrentItemsByImdbId.TryGetValue(imdbId, out itemToUpdate);

            if (itemToUpdate == null && tvdbId != null) CurrentItemsByTvdbId.TryGetValue(tvdbId, out itemToUpdate);

            if (itemToUpdate != null) itemToUpdate.OriginalLanguage = originalLanguage;
        }

        private static ContextItem GetAndRemoveItem()
        {
            var lookupItem = CurrentLookupItem.Value;
            CurrentLookupItem.Value = null;

            if (lookupItem == null) return null;

            ContextItem foundItem = null;

            if (lookupItem.TmdbId != null)
            {
                CurrentItemsByTmdbId.TryRemove(lookupItem.TmdbId, out foundItem);
            }

            if (foundItem == null && lookupItem.ImdbId != null)
            {
                CurrentItemsByImdbId.TryRemove(lookupItem.ImdbId, out foundItem);
            }

            if (foundItem == null && lookupItem.TvdbId != null)
            {
                CurrentItemsByTvdbId.TryRemove(lookupItem.TvdbId, out foundItem);
            }

            return foundItem;
        }

        private static string GetOriginalLanguage(BaseItem item)
        {
            var itemLookup = GetAndRemoveItem();

            if (itemLookup != null && !string.IsNullOrEmpty(itemLookup.OriginalLanguage))
                return itemLookup.OriginalLanguage;

            var fallbackItem = item is Movie || item is Series ? item :
                item is Season season ? season.Series :
                item is Episode episode ? episode.Series : null;

            if (fallbackItem != null)
            {
                return LanguageUtility.GetLanguageByTitle(fallbackItem.OriginalTitle);
            }

            if (item is BoxSet collection)
            {
                return Plugin.MetadataApi.GetCollectionOriginalLanguage(collection);
            }

            return null;
        }

        [HarmonyPostfix]
        private static void GetMovieInfoTmdbPostfix(BaseItem item, string language, IJsonSerializer jsonSerializer,
            CancellationToken cancellationToken, Task __result)
        {
            if (_movieDataTmdbTaskResult is null)
                _movieDataTmdbTaskResult = __result.GetType().GetProperty("Result");

            var movieData = _movieDataTmdbTaskResult?.GetValue(__result);
            if (movieData != null && _tmdbIdMovieDataTmdb != null && _imdbIdMovieDataTmdb != null && _originalLanguageMovieDataTmdb != null)
            {
                var tmdbId = _tmdbIdMovieDataTmdb.GetValue(movieData)?.ToString();
                var imdbId = _imdbIdMovieDataTmdb.GetValue(movieData) as string;
                var originalLanguage = _originalLanguageMovieDataTmdb.GetValue(movieData) as string;
                if ((!string.IsNullOrEmpty(tmdbId) || !string.IsNullOrEmpty(imdbId)) &&
                    !string.IsNullOrEmpty(originalLanguage))
                {
                    UpdateOriginalLanguage(tmdbId, imdbId, null, originalLanguage);
                }
            }
        }

        [HarmonyPostfix]
        private static void EnsureSeriesInfoTmdbPostfix(string tmdbId, string language, CancellationToken cancellationToken,
            Task __result)
        {
            if (WasCalledByMethod(_movieDbAssembly, "FetchImages")) WasCalledByImageProvider.Value = true;

            __result.GetAwaiter().GetResult();

            if (!WasCalledByImageProvider.Value) return;

            if (_seriesDataTmdbTaskResult is null)
                _seriesDataTmdbTaskResult = __result.GetType().GetProperty("Result");

            var seriesInfo = _seriesDataTmdbTaskResult?.GetValue(__result);
            if (seriesInfo != null && _tmdbIdSeriesDataTmdb != null && _languagesSeriesDataTmdb != null)
            {
                var id = _tmdbIdSeriesDataTmdb.GetValue(seriesInfo)?.ToString();
                var originalLanguage = (_languagesSeriesDataTmdb.GetValue(seriesInfo) as List<string>)
                    ?.FirstOrDefault();
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(originalLanguage))
                {
                    UpdateOriginalLanguage(id, null, null, originalLanguage);
                }
            }
        }

        [HarmonyPostfix]
        private static void EnsureMovieInfoTvdbPostfix(string tvdbId, IDirectoryService directoryService,
            CancellationToken cancellationToken, Task __result)
        {
            if (WasCalledByMethod(_tvdbAssembly, "GetImages")) WasCalledByImageProvider.Value = true;

            __result.GetAwaiter().GetResult();

            if (!WasCalledByImageProvider.Value) return;

            if (_movieDataTvdbTaskResult is null)
                _movieDataTvdbTaskResult = __result.GetType().GetProperty("Result");

            var movieData = _movieDataTvdbTaskResult?.GetValue(__result);
            if (movieData != null && _tvdbIdMovieDataTvdb != null && _originalLanguageMovieDataTvdb != null)
            {
                var id = _tvdbIdMovieDataTvdb.GetValue(movieData)?.ToString();
                var originalLanguage = _originalLanguageMovieDataTvdb.GetValue(movieData) as string;
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(originalLanguage))
                {
                    var convertedLanguage = Plugin.MetadataApi.ConvertToServerLanguage(originalLanguage);
                    UpdateOriginalLanguage(null, null, id, convertedLanguage);
                }
            }
        }

        [HarmonyPostfix]
        private static void EnsureSeriesInfoTvdbPostfix(string tvdbId, IDirectoryService directoryService,
            CancellationToken cancellationToken, Task __result)
        {
            if (WasCalledByMethod(_tvdbAssembly, "GetImages")) WasCalledByImageProvider.Value = true;

            __result.GetAwaiter().GetResult();

            if (!WasCalledByImageProvider.Value) return;

            if (_seriesDataTvdbTaskResult is null)
                _seriesDataTvdbTaskResult = __result.GetType().GetProperty("Result");

            var seriesData = _seriesDataTvdbTaskResult?.GetValue(__result);
            if (seriesData != null && _tvdbIdSeriesDataTvdb != null &&
                _originalLanguageSeriesDataTvdb != null)
            {
                var id = _tvdbIdSeriesDataTvdb.GetValue(seriesData)?.ToString();
                var originalLanguage = _originalLanguageSeriesDataTvdb.GetValue(seriesData) as string;
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(originalLanguage))
                {
                    var convertedLanguage = Plugin.MetadataApi.ConvertToServerLanguage(originalLanguage);
                    UpdateOriginalLanguage(null, null, id, convertedLanguage);
                }
            }
        }

        [HarmonyPostfix]
        private static void GetBackdropsPostfix(IEnumerable __result)
        {
            if (__result != null && file_path != null && iso_639_1 != null)
            {
                var resultList = __result.Cast<object>().ToList();

                foreach (var item in resultList)
                {
                    var filePath = file_path.GetValue(item) as string;
                    var language = iso_639_1.GetValue(item) as string;

                    if (!string.IsNullOrEmpty(filePath) && !string.IsNullOrEmpty(language))
                    {
                        BackdropByLanguage[filePath] = language;
                        iso_639_1.SetValue(item, null);
                    }
                }
            }
        }

        [HarmonyPrefix]
        private static bool GetAvailableRemoteImagesPrefix(IHasProviderIds item, LibraryOptions libraryOptions,
            ref RemoteImageQuery query, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            query.IncludeAllLanguages = true;

            var tmdbId = item.GetProviderId(MetadataProviders.Tmdb);
            var imdbId = item.GetProviderId(MetadataProviders.Imdb);
            var tvdbId = item.GetProviderId(MetadataProviders.Tvdb);

            AddContextItem(tmdbId, imdbId, tvdbId);

            return true;
        }

        [HarmonyPostfix]
        private static Task<IEnumerable<RemoteImageInfo>> GetAvailableRemoteImagesPostfix(
            Task<IEnumerable<RemoteImageInfo>> __result, BaseItem item, LibraryOptions libraryOptions,
            RemoteImageQuery query, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            var result = __result.Result;

            if (result is null) return Task.FromResult(Enumerable.Empty<RemoteImageInfo>());

            var originalLanguage = GetOriginalLanguage(item);
            var libraryPreferredImageLanguage = libraryOptions.PreferredImageLanguage?.Split('-')[0];

            var remoteImages = result.ToList();

            foreach (var image in remoteImages.Where(image => image.Type == ImageType.Backdrop))
            {
                var match = BackdropByLanguage
                    .FirstOrDefault(kvp => image.Url.EndsWith(kvp.Key, StringComparison.Ordinal)).Key;

                if (match != null)
                {
                    image.Language = BackdropByLanguage[match];
                    BackdropByLanguage.TryRemove(match, out _);
                }
            }

            var reorderedImages = remoteImages.OrderBy(i =>
                !string.IsNullOrEmpty(libraryPreferredImageLanguage) && string.Equals(i.Language,
                    libraryPreferredImageLanguage, StringComparison.OrdinalIgnoreCase) ? 0 :
                !string.IsNullOrEmpty(originalLanguage) && string.Equals(i.Language, originalLanguage,
                    StringComparison.OrdinalIgnoreCase) ? 1 : 2);

            return Task.FromResult(reorderedImages.AsEnumerable());
        }
    }
}
