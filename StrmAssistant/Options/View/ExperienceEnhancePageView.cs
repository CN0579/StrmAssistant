﻿using Emby.Media.Common.Extensions;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Tasks;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using StrmAssistant.Options.Store;
using StrmAssistant.Options.UIBaseClasses.Views;
using StrmAssistant.Properties;
using System.Threading.Tasks;

namespace StrmAssistant.Options.View
{
    internal class ExperienceEnhancePageView : PluginPageView
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ExperienceEnhanceOptionsStore _store;

        public ExperienceEnhancePageView(PluginInfo pluginInfo, ILibraryManager libraryManager,
            ExperienceEnhanceOptionsStore store) : base(pluginInfo.Id)
        {
            _libraryManager = libraryManager;
            _store = store;
            ContentData = store.GetOptions();
            ExperienceEnhanceOptions.UIFunctionOptions.Initialize();
        }

        public ExperienceEnhanceOptions ExperienceEnhanceOptions => ContentData as ExperienceEnhanceOptions;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            _store.SetOptions(ExperienceEnhanceOptions);
            return base.OnSaveCommand(itemId, commandId, data);
        }

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            switch (commandId)
            {
                case "SplitMovies":
                    Task.Run(HandleSplitMovieButton).FireAndForget(Plugin.Instance.Logger);
                    return Task.FromResult<IPluginUIView>(this);
            }

            return base.RunCommand(itemId, commandId, data);
        }

        private async Task HandleSplitMovieButton()
        {
            ExperienceEnhanceOptions.SplitMovieButton.IsEnabled = false;
            ExperienceEnhanceOptions.SplitMovieProgress.Clear();
            var progressItem = new GenericListItem
            {
                Icon = IconNames.work_outline,
                IconMode = ItemListIconMode.SmallRegular,
                Status = ItemStatus.InProgress,
                HasPercentage = true
            };
            ExperienceEnhanceOptions.SplitMovieProgress.Add(progressItem);
            RaiseUIViewInfoChanged();
            await Task.Delay(100.ms());

            var movies = Plugin.LibraryApi.FetchSplitMovieItems();
            progressItem.PercentComplete = 20;
            RaiseUIViewInfoChanged();
            await Task.Delay(100.ms());

            var total = movies.Count;
            var current = 0;

            foreach (var item in movies)
            {
                _libraryManager.SplitItems(item);
                current++;
                var percentDone = current * 100 / total;
                var adjustedProgress = 20 + percentDone * 80 / 100;
                progressItem.PercentComplete = adjustedProgress;
                Plugin.Instance.Logger.Info("MergeMovie - Split group " + current + "/" + total + " - " + item.Path);
                RaiseUIViewInfoChanged();
                await Task.Delay(100.ms());
            }

            ExperienceEnhanceOptions.SplitMovieButton.IsEnabled = true;
            progressItem.HasPercentage = false;
            progressItem.SecondaryText = Resources.Operation_Success;
            progressItem.Icon = IconNames.info;
            progressItem.Status = ItemStatus.Succeeded;
            RaiseUIViewInfoChanged();
            await Task.Delay(2000);
            ExperienceEnhanceOptions.SplitMovieProgress.Clear();
            RaiseUIViewInfoChanged();
        }
    }
}
