using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StrmAssistant.Options
{
    public class ExperienceEnhanceOptions : EditableOptionsBase
    {
        [DisplayNameL("ExperienceEnhanceOptions_EditorTitle_Experience_Enhance", typeof(Resources))]
        public override string EditorTitle => Resources.ExperienceEnhanceOptions_EditorTitle_Experience_Enhance;
        
        [DisplayNameL("GeneralOptions_MergeMultiVersion_Merge_Multiple_Versions", typeof(Resources))]
        [DescriptionL("GeneralOptions_MergeMultiVersion_Auto_merge_multiple_versions_if_in_the_same_folder_", typeof(Resources))]
        [Required]
        public bool MergeMultiVersion { get; set; } = false;

        public enum MergeMultiVersionOption
        {
            [DescriptionL("MergeMultiVersionOption_LibraryScope_LibraryScope", typeof(Resources))]
            LibraryScope,
            [DescriptionL("MergeMultiVersionOption_GlobalScope_GlobalScope", typeof(Resources))]
            GlobalScope
        }
        
        [DisplayName("")]
        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public MergeMultiVersionOption MergeMultiVersionPreferences { get; set; } =
            MergeMultiVersionOption.LibraryScope;

        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public ButtonItem SplitMovieButton =>
            new ButtonItem(
                Resources.ExperienceEnhanceOptions_SplitMovieButton_Split_multi_version_movies_in_all_libraries)
            {
                Icon = IconNames.clear_all, Data1 = "SplitMovies"
            };

        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public GenericItemList SplitMovieProgress { get; set; } = new GenericItemList();
        
        [DisplayNameL("ExperienceEnhanceOptions_EnhanceNotification_Enhance_Notification", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_EnhanceNotification_Show_episode_details_in_series_notification__Default_is_OFF_", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool EnhanceNotificationSystem { get; set; } = false;

        [DisplayNameL("UIFunctionOptions_EditorTitle_UI_Functions", typeof(Resources))]
        public UIFunctionOptions UIFunctionOptions { get; set; } = new UIFunctionOptions();

        [Browsable(false)]
        public bool IsModSupported => RuntimeInformation.ProcessArchitecture == Architecture.X64;
    }
}
