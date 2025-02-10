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
    internal class IntroSkipPageView : PluginPageView
    {
        private readonly IntroSkipOptionsStore _store;

        public IntroSkipPageView(PluginInfo pluginInfo, ILibraryManager libraryManager,
            IntroSkipOptionsStore store)
            : base(pluginInfo.Id)
        {
            _store = store;
            ContentData = store.GetOptions();
            IntroSkipOptions.Initialize(libraryManager);
        }

        public IntroSkipOptions IntroSkipOptions => ContentData as IntroSkipOptions;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            if (ContentData is IntroSkipOptions options)
            {
                options.ValidateOrThrow();
            }

            _store.SetOptions(IntroSkipOptions);
            return base.OnSaveCommand(itemId, commandId, data);
        }

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            switch (commandId)
            {
                case "ClearIntroCreditsMarkers":
                    Task.Run(HandleClearIntroButton).FireAndForget(Plugin.Instance.Logger);
                    return Task.FromResult<IPluginUIView>(this);
            }

            return base.RunCommand(itemId, commandId, data);
        }

        private async Task HandleClearIntroButton()
        {
            IntroSkipOptions.ClearIntroButton.IsEnabled = false;
            IntroSkipOptions.ClearIntroProgress.Clear();
            var progressItem = new GenericListItem
            {
                Icon = IconNames.work_outline,
                IconMode = ItemListIconMode.SmallRegular,
                Status = ItemStatus.InProgress,
                HasPercentage = true
            };
            IntroSkipOptions.ClearIntroProgress.Add(progressItem);
            RaiseUIViewInfoChanged();
            await Task.Delay(100.ms());

            var items = Plugin.ChapterApi.FetchClearTaskItems();
            progressItem.PercentComplete = 20;
            RaiseUIViewInfoChanged();
            await Task.Delay(100.ms());

            var total = items.Count;
            var current = 0;

            foreach (var item in items)
            {
                Plugin.ChapterApi.RemoveIntroCreditsMarkers(item);
                current++;
                var percentDone = current * 100 / total;
                var adjustedProgress = 20 + percentDone * 80 / 100;
                progressItem.PercentComplete = adjustedProgress;
                Plugin.Instance.Logger.Info("IntroSkip - Clear Task " + current + "/" + total + " - " + item.Path);
                RaiseUIViewInfoChanged();
                await Task.Delay(100.ms());
            }

            IntroSkipOptions.ClearIntroButton.IsEnabled = true;
            progressItem.HasPercentage = false;
            progressItem.SecondaryText = Resources.Operation_Success;
            progressItem.Icon = IconNames.info;
            progressItem.Status = ItemStatus.Succeeded;
            RaiseUIViewInfoChanged();
            await Task.Delay(2000);
            IntroSkipOptions.ClearIntroProgress.Clear();
            RaiseUIViewInfoChanged();
        }
    }
}
