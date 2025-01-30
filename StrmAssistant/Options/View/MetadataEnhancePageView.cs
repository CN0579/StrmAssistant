﻿using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using StrmAssistant.Options.Store;
using StrmAssistant.Options.UIBaseClasses.Views;
using System.Threading.Tasks;

namespace StrmAssistant.Options.View
{
    internal class MetadataEnhancePageView : PluginPageView
    {
        private readonly MetadataEnhanceOptionsStore _store;

        public MetadataEnhancePageView(PluginInfo pluginInfo, MetadataEnhanceOptionsStore store)
            : base(pluginInfo.Id)
        {
            _store = store;
            ContentData = store.GetOptions();
            MetadataEnhanceOptions.Initialize();
        }

        public MetadataEnhanceOptions MetadataEnhanceOptions => ContentData as MetadataEnhanceOptions;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            if (ContentData is MetadataEnhanceOptions options)
            {
                options.ValidateOrThrow();
            }

            _store.SetOptions(MetadataEnhanceOptions);
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
