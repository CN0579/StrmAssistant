﻿using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using StrmAssistant.Web.Api;
using System;
using System.IO;
using System.Threading.Tasks;

namespace StrmAssistant.Web.Service
{
    [Authenticated]
    public class LibraryService : BaseApiService
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;

        public LibraryService(ILibraryManager libraryManager, IItemRepository itemRepository, IFileSystem fileSystem)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _fileSystem = fileSystem;
        }

        public void Any(DeleteVersion request)
        {
            var item = _libraryManager.GetItemById(request.Id);

            if (!(item is Video video) || !(video is Movie || video is Episode) || !video.IsFileProtocol ||
                video.GetAlternateVersionIds().Count == 0)
            {
                return;
            }
            
            var user = GetUserForRequest(null);
            var collectionFolders = _libraryManager.GetCollectionFolders(item);

            if (user is null)
            {
                if (!item.CanDelete())
                {
                    return;
                }
            }
            else if (!item.CanDelete(user, collectionFolders))
            {
                return;
            }

            var enableDeepDelete = Plugin.Instance.ExperienceEnhanceStore.GetOptions().EnableDeepDelete;
            var mountPaths = enableDeepDelete ? Plugin.LibraryApi.PrepareDeepDelete(item) : null;
            
            var proceedToDelete = true;
            var deletePaths = Plugin.LibraryApi.GetDeletePaths(item);

            foreach (var path in deletePaths)
            {
                try
                {
                    if (!path.IsDirectory)
                    {
                        _logger.Info("DeleteVersion - Attempting to delete file: " + path.FullName);
                        _fileSystem.DeleteFile(path.FullName, true);
                    }
                }
                catch (Exception e)
                {
                    if (e is IOException || e is UnauthorizedAccessException)
                    {
                        proceedToDelete = false;
                        _logger.Error("DeleteVersion - Failed to delete file: " + path.FullName);
                        _logger.Error(e.Message);
                        _logger.Debug(e.StackTrace);
                    }
                }
            }

            if (proceedToDelete)
            {
                _itemRepository.DeleteItems(new[] { item });

                try
                {
                    _fileSystem.DeleteDirectory(item.GetInternalMetadataPath(), true, true);
                }
                catch
                {
                    // ignored
                }

                if (mountPaths?.Count > 0)
                {
                    Task.Run(() => Plugin.LibraryApi.ExecuteDeepDelete(mountPaths)).ConfigureAwait(false);
                }
            }
        }
    }
}
