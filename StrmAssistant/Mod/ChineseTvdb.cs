using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.LanguageUtility;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class ChineseTvdb : PatchBase<ChineseTvdb>
    {
        private static Assembly _tvdbAssembly;
        private static MethodInfo _convertToTvdbLanguages;
        private static MethodInfo _getTranslation;
        private static MethodInfo _addMovieInfo;
        private static MethodInfo _addSeriesInfo;
        private static MethodInfo _getTvdbSeason;
        private static MethodInfo _findEpisode;
        private static MethodInfo _getEpisodeData;

        private static readonly ThreadLocal<bool?> ConsiderJapanese = new ThreadLocal<bool?>();

        public ChineseTvdb()
        {
            Initialize();

            if (Plugin.Instance.MetadataEnhanceStore.GetOptions().ChineseTvdb)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            _tvdbAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Tvdb");

            if (_tvdbAssembly != null)
            {
                var entryPoint = _tvdbAssembly.GetType("Tvdb.EntryPoint");
                _convertToTvdbLanguages = entryPoint.GetMethod("ConvertToTvdbLanguages",
                    BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(ItemLookupInfo) }, null);
                var translations = _tvdbAssembly.GetType("Tvdb.Translations");
                _getTranslation =
                    translations.GetMethod("GetTranslation", BindingFlags.Instance | BindingFlags.NonPublic);
                var tvdbMovieProvider = _tvdbAssembly.GetType("Tvdb.TvdbMovieProvider");
                _addMovieInfo = tvdbMovieProvider.GetMethod("AddMovieInfo",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var tvdbSeriesProvider = _tvdbAssembly.GetType("Tvdb.TvdbSeriesProvider");
                _addSeriesInfo = tvdbSeriesProvider.GetMethod("AddSeriesInfo",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var tvdbSeasonProvider= _tvdbAssembly.GetType("Tvdb.TvdbSeasonProvider");
                _getTvdbSeason =
                    tvdbSeasonProvider.GetMethod("GetTvdbSeason", BindingFlags.Instance | BindingFlags.Public);
                var tvdbEpisodeProvider = _tvdbAssembly.GetType("Tvdb.TvdbEpisodeProvider");
                _findEpisode = tvdbEpisodeProvider.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "FindEpisode" && m.GetParameters().Length == 3);
                _getEpisodeData =
                    tvdbEpisodeProvider.GetMethod("GetEpisodeData", BindingFlags.Instance | BindingFlags.Public);
            }
            else
            {
                Plugin.Instance.Logger.Warn("ChineseTvdb - Tvdb plugin is not installed");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                PatchTracker.IsSupported = false;
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _convertToTvdbLanguages,
                postfix: nameof(ConvertToTvdbLanguagesPostfix));
            PatchUnpatch(PatchTracker, apply, _getTranslation, prefix: nameof(GetTranslationPrefix),
                postfix: nameof(GetTranslationPostfix));
            PatchUnpatch(PatchTracker, apply, _addMovieInfo, postfix: nameof(AddInfoPostfix));
            PatchUnpatch(PatchTracker, apply, _addSeriesInfo, postfix: nameof(AddInfoPostfix));
            PatchUnpatch(PatchTracker, apply, _getTvdbSeason, postfix: nameof(GetTvdbSeasonPostfix));
            PatchUnpatch(PatchTracker, apply, _findEpisode, postfix: nameof(FindEpisodePostfix));
            PatchUnpatch(PatchTracker, apply, _getEpisodeData, postfix: nameof(GetEpisodeDataPostfix));
        }

        [HarmonyPostfix]
        private static void ConvertToTvdbLanguagesPostfix(ItemLookupInfo lookupInfo, ref string[] __result)
        {
            if (lookupInfo.MetadataLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                var list = __result.ToList();
                var index = list.FindIndex(l => string.Equals(l, "eng", StringComparison.OrdinalIgnoreCase));

                var currentFallbackLanguages =
                    GetTvdbFallbackLanguages().Where(l => (ConsiderJapanese.Value ?? true) || l != "jpn");

                foreach (var fallbackLanguage in currentFallbackLanguages)
                {
                    if (!list.Contains(fallbackLanguage, StringComparer.OrdinalIgnoreCase))
                    {
                        if (index >= 0)
                        {
                            list.Insert(index, fallbackLanguage);
                            index++;
                        }
                        else
                        {
                            list.Add(fallbackLanguage);
                        }
                    }
                }

                __result = list.ToArray();
            }
        }
        
        [HarmonyPrefix]
        private static bool GetTranslationPrefix(ref List<object> translations, ref string[] tvdbLanguages, int field,
            ref bool defaultToFirst)
        {
            if (translations != null && translations.Count > 0)
            {
                if (field == 0)
                {
                    translations.RemoveAll(t =>
                    {
                        bool.TryParse(Traverse.Create(t).Property("isAlias").ToString(), out var isAlias);

                        return isAlias;
                    });
                }

                if (HasTvdbJapaneseFallback())
                {
                    var considerJapanese = translations.Any(t =>
                    {
                        var tran = Traverse.Create(t);
                        var language = tran.Property("language").ToString();
                        var isPrimary = tran.Property("IsPrimary").GetValue() as bool?;

                        return language == "jpn" && isPrimary is true;
                    });

                    tvdbLanguages = tvdbLanguages.Where(l => considerJapanese || l != "jpn").ToArray();
                }

                if (field == 0)
                {
                    var cnLanguages = new HashSet<string> { "zho", "zhtw", "yue" };
                    var trans = translations;
                    Array.Sort(tvdbLanguages, (lang1, lang2) =>
                    {
                        var translation1 =
                            trans.FirstOrDefault(t => Traverse.Create(t).Property("language").ToString() == lang1);
                        var translation2 =
                            trans.FirstOrDefault(t => Traverse.Create(t).Property("language").ToString() == lang2);

                        var name1 = Traverse.Create(translation1).Property("name").ToString();
                        var name2 = Traverse.Create(translation2).Property("name").ToString();

                        var cn1 = cnLanguages.Contains(lang1);
                        var cn2 = cnLanguages.Contains(lang2);

                        if (cn1 && cn2)
                        {
                            if (IsChinese(name1) && !IsChinese(name2)) return -1;
                            if (!IsChinese(name1) && IsChinese(name2)) return 1;
                            return 0;
                        }

                        if (cn1) return -1;
                        if (cn2) return 1;

                        return 0;
                    });
                }

                var languageOrder = tvdbLanguages.Select((l, index) => (l, index))
                    .ToDictionary(x => x.l, x => x.index);

                translations.Sort((t1, t2) =>
                {
                    var language1 = Traverse.Create(t1).Property("language").ToString();
                    var language2 = Traverse.Create(t2).Property("language").ToString();

                    var index1 = languageOrder.GetValueOrDefault(language1, int.MaxValue);
                    var index2 = languageOrder.GetValueOrDefault(language2, int.MaxValue);

                    return index1.CompareTo(index2);
                });
            }

            if (translations?.Count == 0) translations = null;

            return true;
        }

        [HarmonyPostfix]
        private static void AddInfoPostfix(MetadataResult<BaseItem> metadataResult)
        {
            var instance = metadataResult.Item;

            if (IsChinese(instance.Name))
            {
                instance.Name = ConvertTraditionalToSimplified(instance.Name);
            }

            if (IsChinese(instance.Overview))
            {
                instance.Overview = ConvertTraditionalToSimplified(instance.Overview);
            }
            else if (BlockTvdbNonFallbackLanguage(instance.Overview))
            {
                instance.Overview = null;
            }
        }

        [HarmonyPostfix]
        private static void GetTranslationPostfix(List<object> translations, string[] tvdbLanguages, int field,
            bool defaultToFirst, ref object __result)
        {
            if (__result != null && !defaultToFirst)
            {
                var traverseResult = Traverse.Create(__result);
                var nameProperty = traverseResult.Property("name");
                var overviewProperty = traverseResult.Property("overview");

                if (nameProperty != null && overviewProperty != null)
                {
                    var name = nameProperty.ToString();

                    switch (field)
                    {
                        case 0:
                        {
                            if (IsChinese(name))
                            {
                                nameProperty.SetValue(ConvertTraditionalToSimplified(name));
                            }
                            else if (BlockTvdbNonFallbackLanguage(name))
                            {
                                nameProperty.SetValue(null);
                            }

                            break;
                        }
                        case 1:
                        {
                            var overview = overviewProperty.ToString();

                            if (IsChinese(overview))
                            {
                                overview = ConvertTraditionalToSimplified(overview);
                                overviewProperty.SetValue(overview);
                            }
                            else if (BlockTvdbNonFallbackLanguage(overview))
                            {
                                overview = null;
                                overviewProperty.SetValue(null);
                            }

                            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(overview))
                            {
                                nameProperty.SetValue(overview);
                            }

                            break;
                        }
                    }
                }
            }
        }

        [HarmonyPostfix]
        private static void GetTvdbSeasonPostfix(SeasonInfo id, IDirectoryService directoryService,
            CancellationToken cancellationToken, Task __result)
        {
            var tvdbSeason = Traverse.Create(__result).Property("Result").GetValue();

            if (tvdbSeason != null)
            {
                var nameProperty = Traverse.Create(tvdbSeason).Property("name");

                if (nameProperty != null)
                {
                    var name = nameProperty.ToString();

                    if (IsChinese(name))
                    {
                        nameProperty.SetValue(ConvertTraditionalToSimplified(name));
                    }
                    else if (id.IndexNumber.HasValue &&
                             (string.IsNullOrEmpty(name) || BlockTvdbNonFallbackLanguage(name)))
                    {
                        nameProperty.SetValue($"第 {id.IndexNumber} 季");
                    }
                }
            }
        }

        [HarmonyPostfix]
        private static void FindEpisodePostfix(object data, EpisodeInfo searchInfo, int? seasonNumber,
            ref object __result)
        {
            if (__result != null)
            {
                var traverseResult = Traverse.Create(__result);
                var nameProperty = traverseResult.Property("name");
                var overviewProperty = traverseResult.Property("overview");

                if (nameProperty != null && overviewProperty != null)
                {
                    var name = nameProperty.ToString();
                    var overview = overviewProperty.ToString();

                    var considerJapanese = HasTvdbJapaneseFallback() && (IsJapanese(name) || IsJapanese(overview));
                    ConsiderJapanese.Value = considerJapanese;

                    if (!considerJapanese)
                    {
                        if (!IsChinese(name)) nameProperty.SetValue(null);
                        if (!IsChinese(overview)) overviewProperty.SetValue(null);
                    }
                    else
                    {
                        if (!IsChineseJapanese(name)) nameProperty.SetValue(null);
                        if (!IsChineseJapanese(overview)) overviewProperty.SetValue(null);
                    }
                }
            }
        }

        [HarmonyPostfix]
        private static void GetEpisodeDataPostfix(EpisodeInfo searchInfo, bool fillExtendedInfo,
            IDirectoryService directoryService, CancellationToken cancellationToken, Task __result)
        {
            var taskResult = Traverse.Create(__result).Property("Result").GetValue();
            var tvdbEpisode = Traverse.Create(taskResult).Property("Item1").GetValue();

            if (tvdbEpisode != null)
            {
                var traverseTvdbEpisode = Traverse.Create(tvdbEpisode);
                var nameProperty = traverseTvdbEpisode.Property("name");
                var overviewProperty = traverseTvdbEpisode.Property("overview");

                if (nameProperty != null && overviewProperty != null)
                {
                    var name = nameProperty.ToString();
                    var overview = overviewProperty.ToString();

                    if (IsChinese(name))
                    {
                        nameProperty.SetValue(ConvertTraditionalToSimplified(name));
                    }
                    else if (searchInfo.IndexNumber.HasValue &&
                             (string.IsNullOrEmpty(name) || BlockTvdbNonFallbackLanguage(name)))
                    {
                        nameProperty.SetValue($"第 {searchInfo.IndexNumber} 集");
                    }

                    if (IsChinese(overview))
                    {
                        overviewProperty.SetValue(ConvertTraditionalToSimplified(overview));
                    }
                    else if (BlockTvdbNonFallbackLanguage(overview))
                    {
                        overviewProperty.SetValue(null);
                    }
                }
            }
        }
    }
}
