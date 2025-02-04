﻿using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using StrmAssistant.Provider;
using System.Linq;
using System.Reflection;
using System.Threading;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class EnhanceMissingEpisodes : PatchBase<EnhanceMissingEpisodes>
    {
        private static MethodInfo _getEnabledMetadataProviders;

        public static AsyncLocal<string> CurrentSeriesContainingFolderPath = new AsyncLocal<string>();

        public EnhanceMissingEpisodes()
        {
            Initialize();

            if (Plugin.Instance.ExperienceEnhanceStore.GetOptions().UIFunctionOptions.EnhanceMissingEpisodes)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var embyProviders = Assembly.Load("Emby.Providers");
            var providerManager = embyProviders.GetType("Emby.Providers.Manager.ProviderManager");
            _getEnabledMetadataProviders = providerManager.GetMethod("GetEnabledMetadataProviders",
                BindingFlags.Instance | BindingFlags.Public);
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _getEnabledMetadataProviders,
                postfix: nameof(GetEnabledMetadataProvidersPostfix));
        }

        [HarmonyPostfix]
        private static void GetEnabledMetadataProvidersPostfix(BaseItem item, LibraryOptions libraryOptions,
            ref IMetadataProvider[] __result)
        {
            if (item is Series && item.ProviderIds.ContainsKey(MetadataProviders.Tmdb.ToString()))
            {
                var movieDbSeriesProvider =
                    __result.FirstOrDefault(p => p.GetType().FullName == "MovieDb.MovieDbSeriesProvider");
                var newResult = __result.Where(p => p.GetType().FullName != typeof(MovieDbSeriesProvider).FullName)
                    .ToList();
                var provider = Plugin.MetadataApi.GetMovieDbSeriesProvider();

                if (movieDbSeriesProvider != null)
                {
                    var index = newResult.IndexOf(movieDbSeriesProvider);
                    newResult.Insert(index, provider);
                }
                else if (!newResult.Any(p => p is ISeriesMetadataProvider))
                {
                    newResult.Add(provider);
                }

                CurrentSeriesContainingFolderPath.Value = item.ContainingFolderPath;

                __result = newResult.ToArray();
            }
        }
    }
}
