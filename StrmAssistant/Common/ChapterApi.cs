using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Mod;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.LibraryApi;
using static StrmAssistant.Options.IntroSkipOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Common
{
    public class ChapterApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IJsonSerializer _jsonSerializer;

        private const string MarkerSuffix = "#SA";

        public ChapterApi(ILibraryManager libraryManager, IItemRepository itemRepository, IJsonSerializer jsonSerializer)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _jsonSerializer = jsonSerializer;
        }

        public bool HasIntro(BaseItem item)
        {
            return _itemRepository.GetChapters(item.InternalId, new[] { MarkerType.IntroStart }).Any();
        }

        public long? GetIntroStart(BaseItem item)
        {
            var introStart = _itemRepository.GetChapters(item.InternalId, new[] { MarkerType.IntroStart })
                .FirstOrDefault();

            return introStart?.StartPositionTicks;
        }

        public long? GetIntroEnd(BaseItem item)
        {
            var introEnd = _itemRepository.GetChapters(item.InternalId, new[] { MarkerType.IntroEnd }).FirstOrDefault();

            return introEnd?.StartPositionTicks;
        }

        public bool HasCredits(BaseItem item)
        {
            return _itemRepository.GetChapters(item.InternalId, new[] { MarkerType.CreditsStart }).Any();
        }

        public long? GetCreditsStart(BaseItem item)
        {
            var creditsStart = _itemRepository.GetChapters(item.InternalId, new[] { MarkerType.CreditsStart })
                .FirstOrDefault();

            return creditsStart?.StartPositionTicks;
        }

        public void UpdateIntro(Episode item, SessionInfo session, long introStartPositionTicks,
            long introEndPositionTicks)
        {
            if (introStartPositionTicks > introEndPositionTicks) return;

            var resultEpisodes = FetchEpisodes(item, MarkerType.IntroEnd);

            foreach (var episode in resultEpisodes)
            {
                var chapters = _itemRepository.GetChapters(episode);

                chapters.RemoveAll(c => c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd);

                var introStart = new ChapterInfo
                {
                    Name = MarkerType.IntroStart + MarkerSuffix,
                    MarkerType = MarkerType.IntroStart,
                    StartPositionTicks = introStartPositionTicks
                };
                chapters.Add(introStart);
                var introEnd = new ChapterInfo
                {
                    Name = MarkerType.IntroEnd + MarkerSuffix,
                    MarkerType = MarkerType.IntroEnd,
                    StartPositionTicks = introEndPositionTicks
                };
                chapters.Add(introEnd);

                chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));

                _itemRepository.SaveChapters(episode.InternalId, chapters);
            }

            _logger.Info("Intro marker updated by " + session.UserName + " for " +
                         item.FindSeriesName() + " - " + item.FindSeasonName() + " - " + item.Season.Path);
            var introStartTime = new TimeSpan(introStartPositionTicks).ToString(@"hh\:mm\:ss\.fff");
            _logger.Info("Intro start time: " + introStartTime);
            var introEndTime = new TimeSpan(introEndPositionTicks).ToString(@"hh\:mm\:ss\.fff");
            _logger.Info("Intro end time: " + introEndTime);
            Plugin.NotificationApi.IntroUpdateSendNotification(item, session, introStartTime, introEndTime);
        }

        public void UpdateCredits(Episode item, SessionInfo session, long creditsDurationTicks)
        {
            var resultEpisodes = FetchEpisodes(item, MarkerType.CreditsStart);

            foreach (var episode in resultEpisodes)
            {
                if (episode.RunTimeTicks.HasValue)
                {
                    if (episode.RunTimeTicks.Value - creditsDurationTicks > 0)
                    {
                        var chapters = _itemRepository.GetChapters(episode);
                        chapters.RemoveAll(c => c.MarkerType == MarkerType.CreditsStart);

                        var creditsStart = new ChapterInfo
                        {
                            Name = MarkerType.CreditsStart + MarkerSuffix,
                            MarkerType = MarkerType.CreditsStart,
                            StartPositionTicks = episode.RunTimeTicks.Value - creditsDurationTicks
                        };
                        chapters.Add(creditsStart);

                        chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));

                        _itemRepository.SaveChapters(episode.InternalId, chapters);
                    }
                }
            }

            _logger.Info("Credits marker updated by " + session.UserName + " for " +
                         item.FindSeriesName() + " - " + item.FindSeasonName() + " - " + item.Season.Path);
            var creditsDuration = new TimeSpan(creditsDurationTicks).ToString(@"hh\:mm\:ss\.fff");
            _logger.Info("Credits duration: " + new TimeSpan(creditsDurationTicks).ToString(@"hh\:mm\:ss\.fff"));
            Plugin.NotificationApi.CreditsUpdateSendNotification(item, session, creditsDuration);
        }

        private static bool IsMarkerAddedByIntroSkip(ChapterInfo chapter)
        {
            return chapter.Name.EndsWith(MarkerSuffix);
        }

        private static bool AllowOverwrite(ChapterInfo chapter)
        {
            return IsMarkerAddedByIntroSkip(chapter) ||
                   IsIntroSkipPreferenceSelected(IntroSkipPreference.ResetAndOverwrite);
        }

        public List<BaseItem> FetchEpisodes(BaseItem item, MarkerType markerType)
        {
            var episodesInSeasonQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                HasPath = true,
                MediaTypes = new[] { MediaType.Video },
                ParentIds = new[] { item.ParentId },
                OrderBy = new (string, SortOrder)[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) }
            };
            var episodesInSeason =
                _libraryManager.GetItemList(episodesInSeasonQuery).ToList();

            var priorEpisodesWithoutMarkers = episodesInSeason.Where(e => e.IndexNumber < item.IndexNumber)
                .Where(e =>
                {
                    if (!Plugin.LibraryApi.HasMediaInfo(e))
                    {
                        QueueManager.MediaInfoExtractItemQueue.Enqueue(e);
                        return false;
                    }

                    var chapters = _itemRepository.GetChapters(e);
                    switch (markerType)
                    {
                        case MarkerType.IntroEnd:
                            {
                                var hasIntroStart = chapters.Any(c => c.MarkerType == MarkerType.IntroStart);
                                var hasIntroEnd = chapters.Any(c => c.MarkerType == MarkerType.IntroEnd);
                                return !hasIntroStart || !hasIntroEnd;
                            }
                        case MarkerType.CreditsStart:
                            var hasCredits = chapters.Any(c => c.MarkerType == MarkerType.CreditsStart);
                            return !hasCredits;
                    }

                    return false;
                });

            var followingEpisodes = episodesInSeason.Where(e => e.IndexNumber > item.IndexNumber)
                .Where(e =>
                {
                    if (!Plugin.LibraryApi.HasMediaInfo(e))
                    {
                        QueueManager.MediaInfoExtractItemQueue.Enqueue(e);
                        return false;
                    }

                    var chapters = _itemRepository.GetChapters(e);
                    switch (markerType)
                    {
                        case MarkerType.IntroEnd:
                            {
                                var hasIntroStart = chapters.Any(c =>
                                    c.MarkerType == MarkerType.IntroStart && !AllowOverwrite(c));
                                var hasIntroEnd = chapters.Any(c =>
                                    c.MarkerType == MarkerType.IntroEnd && !AllowOverwrite(c));
                                return !hasIntroStart || !hasIntroEnd;
                            }
                        case MarkerType.CreditsStart:
                            var hasCredits = chapters.Any(c =>
                                c.MarkerType == MarkerType.CreditsStart && !AllowOverwrite(c));
                            return !hasCredits;
                    }

                    return false;
                });

            var result = priorEpisodesWithoutMarkers.Concat(new[] { item }).Concat(followingEpisodes).ToList();
            return result;
        }

        public void RemoveSeasonIntroCreditsMarkers(Episode item, SessionInfo session)
        {
            var episodesInSeasonQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                HasPath = true,
                MediaTypes = new[] { MediaType.Video },
                ParentIds = new[] { item.ParentId },
                MinIndexNumber = item.IndexNumber,
                OrderBy = new (string, SortOrder)[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) }
            };
            var episodesInSeason = _libraryManager.GetItemList(episodesInSeasonQuery);

            foreach (var episode in episodesInSeason)
            {
                RemoveIntroCreditsMarkers(episode);
            }

            _logger.Info("Intro and Credits markers are cleared by " + session.UserName + " since " + item.Name +
                         " in " + item.FindSeriesName() + " - " + item.FindSeasonName() + " - " + item.Season.Path);
        }

        public void RemoveIntroCreditsMarkers(BaseItem item)
        {
            var chapters = _itemRepository.GetChapters(item);
            chapters.RemoveAll(c =>
                (c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd ||
                 c.MarkerType == MarkerType.CreditsStart) && IsMarkerAddedByIntroSkip(c));
            _itemRepository.SaveChapters(item.InternalId, chapters);
        }

        public List<BaseItem> FetchClearTaskItems()
        {
            var libraryIds = Plugin.Instance.IntroSkipStore.GetOptions().LibraryScope
                ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => libraryIds != null && libraryIds.Any()
                    ? libraryIds.Contains(f.Id)
                    : f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null).ToList();

            _logger.Info("IntroSkip - LibraryScope: " +
                         (libraryIds != null && libraryIds.Any()
                             ? string.Join(", ", libraries.Select(l => l.Name))
                             : "ALL"));

            var itemsIntroSkipQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                HasPath = true,
                MediaTypes = new[] { MediaType.Video },
                PathStartsWithAny = libraries.SelectMany(l => l.Locations).Select(ls =>
                    ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                        ? ls
                        : ls + Path.DirectorySeparatorChar).ToArray()
            };

            var results = _libraryManager.GetItemList(itemsIntroSkipQuery);

            var items = new List<BaseItem>();
            foreach (var item in results)
            {
                var chapters = _itemRepository.GetChapters(item);
                if (chapters != null && chapters.Any())
                {
                    var hasMarkers = chapters.Any(c =>
                        (c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd ||
                         c.MarkerType == MarkerType.CreditsStart) && IsMarkerAddedByIntroSkip(c));
                    if (hasMarkers)
                    {
                        items.Add(item);
                    }
                }
            }
            _logger.Info("IntroSkip - Number of items: " + items.Count);

            return items;
        }

        public bool SeasonHasIntroCredits(Episode item)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                HasPath = true,
                MediaTypes = new[] { MediaType.Video },
                ParentIds = new[] { item.ParentId }
            };

            var allEpisodesInSeason = _libraryManager.GetItemList(query);

            var result = allEpisodesInSeason.Any(e =>
            {
                var chapters = _itemRepository.GetChapters(e);
                var hasIntroMarkers =
                    chapters.Any(c => c.MarkerType == MarkerType.IntroStart && IsMarkerAddedByIntroSkip(c)) &&
                    chapters.Any(c => c.MarkerType == MarkerType.IntroEnd && IsMarkerAddedByIntroSkip(c));
                var hasCreditsStart =
                    chapters.Any(c => c.MarkerType == MarkerType.CreditsStart && IsMarkerAddedByIntroSkip(c));

                return hasIntroMarkers || hasCreditsStart;
            });

            return result;
        }

        public List<Episode> SeasonHasIntroCredits(List<Episode> episodes)
        {
            var episodesInScope = episodes
                .Where(e => Plugin.PlaySessionMonitor.IsLibraryInScope(e)).ToList();

            var seasonIds = episodesInScope.Select(e => e.ParentId).Distinct().ToArray();

            var episodesQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                HasPath = true,
                MediaTypes = new[] { MediaType.Video },
                ParentIds = seasonIds
            };

            var groupedBySeason = _libraryManager.GetItemList(episodesQuery)
                .OfType<Episode>()
                .Where(ep => !episodesInScope.Select(e => e.InternalId).Contains(ep.InternalId))
                .GroupBy(ep => ep.ParentId);

            var resultEpisodes = new List<Episode>();

            foreach (var season in groupedBySeason)
            {
                var hasMarkers = season.Any(e =>
                {
                    var chapters = _itemRepository.GetChapters(e);

                    if (chapters != null && chapters.Any())
                    {
                        var hasIntroMarkers =
                            chapters.Any(c => c.MarkerType == MarkerType.IntroStart && IsMarkerAddedByIntroSkip(c)) &&
                            chapters.Any(c => c.MarkerType == MarkerType.IntroEnd && IsMarkerAddedByIntroSkip(c));
                        var hasCreditsMarker = chapters.Any(c =>
                            c.MarkerType == MarkerType.CreditsStart && IsMarkerAddedByIntroSkip(c));
                        return hasIntroMarkers || hasCreditsMarker;
                    }

                    return false;
                });

                if (hasMarkers)
                {
                    var episodesCanMarkers = episodesInScope.Where(e => e.ParentId == season.Key).ToList();
                    resultEpisodes.AddRange(episodesCanMarkers);
                }
            }

            return resultEpisodes;
        }

        public void PopulateIntroCredits(List<Episode> incomingEpisodes)
        {
            var episodesLatestDataQuery = new InternalItemsQuery
            {
                ItemIds = incomingEpisodes.Select(e => e.InternalId).ToArray()
            };
            var episodes = _libraryManager.GetItemList(episodesLatestDataQuery);

            var seasonIds = episodes.Select(e => e.ParentId).Distinct().ToArray();

            var episodesQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                HasPath = true,
                MediaTypes = new[] { MediaType.Video },
                ParentIds = seasonIds
            };

            var groupedBySeason = _libraryManager.GetItemList(episodesQuery)
                .Where(ep => !episodes.Select(e => e.InternalId).Contains(ep.InternalId))
                .OfType<Episode>()
                .GroupBy(ep => ep.ParentId);

            foreach (var season in groupedBySeason)
            {
                Episode lastIntroEpisode = null;
                Episode lastCreditsEpisode = null;

                foreach (var episode in season.Reverse())
                {
                    var chapters = _itemRepository.GetChapters(episode);

                    var hasIntroMarkers =
                        chapters.Any(c => c.MarkerType == MarkerType.IntroStart && IsMarkerAddedByIntroSkip(c)) &&
                        chapters.Any(c => c.MarkerType == MarkerType.IntroEnd && IsMarkerAddedByIntroSkip(c));

                    if (hasIntroMarkers && lastIntroEpisode == null)
                    {
                        lastIntroEpisode = episode;
                    }

                    var hasCreditsMarker =
                        chapters.Any(c => c.MarkerType == MarkerType.CreditsStart && IsMarkerAddedByIntroSkip(c));

                    if (hasCreditsMarker && lastCreditsEpisode == null && episode.RunTimeTicks.HasValue)
                    {
                        lastCreditsEpisode = episode;
                    }

                    if (lastIntroEpisode != null && lastCreditsEpisode != null)
                    {
                        break;
                    }
                }

                if (lastIntroEpisode != null)
                {
                    var introChapters = _itemRepository.GetChapters(lastIntroEpisode);
                    var introStart = introChapters.FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart);
                    var introEnd = introChapters.FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd);

                    if (introStart != null && introEnd != null)
                    {
                        foreach (var episode in episodes.Where(e => e.ParentId == season.Key))
                        {
                            var chapters = _itemRepository.GetChapters(episode);
                            chapters.Add(introStart);
                            chapters.Add(introEnd);
                            chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));
                            _itemRepository.SaveChapters(episode.InternalId, chapters);
                            _logger.Info("Intro marker updated for " + episode.Path);
                        }
                    }
                }

                if (lastCreditsEpisode != null && lastCreditsEpisode.RunTimeTicks.HasValue)
                {
                    var lastEpisodeChapters = _itemRepository.GetChapters(lastCreditsEpisode);
                    var lastEpisodeCreditsStart = lastEpisodeChapters.FirstOrDefault(c => c.MarkerType == MarkerType.CreditsStart);

                    if (lastEpisodeCreditsStart != null)
                    {
                        var creditsDurationTicks = lastCreditsEpisode.RunTimeTicks.Value - lastEpisodeCreditsStart.StartPositionTicks;
                        if (creditsDurationTicks > 0)
                        {
                            foreach (var episode in episodes.Where(e => e.ParentId == season.Key))
                            {
                                if (episode.RunTimeTicks.HasValue)
                                {
                                    var creditsStartTicks = episode.RunTimeTicks.Value - creditsDurationTicks;
                                    if (creditsStartTicks > 0)
                                    {
                                        var chapters = _itemRepository.GetChapters(episode);
                                        var creditsStart = new ChapterInfo
                                        {
                                            Name = MarkerType.CreditsStart + MarkerSuffix,
                                            MarkerType = MarkerType.CreditsStart,
                                            StartPositionTicks = creditsStartTicks
                                        };
                                        chapters.Add(creditsStart);
                                        chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));
                                        _itemRepository.SaveChapters(episode.InternalId, chapters);
                                        _logger.Info("Credits marker updated for " + episode.Path);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        public async Task<bool> DeserializeChapterInfo(Episode item, IDirectoryService directoryService, string source,
            CancellationToken cancellationToken)
        {
            var mediaInfoJsonPath = GetMediaInfoJsonPath(item);
            var file = directoryService.GetFile(mediaInfoJsonPath);

            if (file?.Exists == true)
            {
                try
                {
                    var mediaSourceWithChapters =
                        (await _jsonSerializer
                            .DeserializeFromFileAsync<List<MediaSourceWithChapters>>(mediaInfoJsonPath)
                            .ConfigureAwait(false)).ToArray()[0];

                    var introStart = mediaSourceWithChapters.Chapters
                        .FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart);

                    var introEnd = mediaSourceWithChapters.Chapters
                        .FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd);

                    if (introStart != null && introEnd != null && introEnd.StartPositionTicks > introStart.StartPositionTicks)
                    {
                        var chapters = _itemRepository.GetChapters(item);
                        chapters.RemoveAll(c =>
                            c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd);
                        chapters.Add(introStart);
                        chapters.Add(introEnd);
                        chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));

                        ChapterChangeTracker.BypassInstance(item);
                        _itemRepository.SaveChapters(item.InternalId, chapters);

                        _logger.Info("ChapterInfoPersist - Deserialization Success (" + source + "): " + mediaInfoJsonPath);

                        return true;
                    }
                }
                catch (Exception e)
                {
                    _logger.Error("ChapterInfoPersist - Deserialization Failed (" + source + "): " + mediaInfoJsonPath);
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            return false;
        }
    }
}
