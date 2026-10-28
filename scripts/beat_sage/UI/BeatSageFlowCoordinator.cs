using System;
using HMUI;

namespace BeatSage.UI
{
    internal sealed class BeatSageFlowCoordinator : FlowCoordinator
    {
        private BeatSageViewController view;
        private bool openSettings;

        internal void Prepare(bool settings)
        {
            openSettings = settings;
            if (view != null) view.Prepare(settings);
        }

        public override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            if (firstActivation)
            {
                showBackButton = true;
                SetTitle("Beat Sage");
                view = BeatSaberMarkupLanguage.BeatSaberUI.CreateViewController<BeatSageViewController>();
                view.Prepare(openSettings);
                ProvideInitialViewControllers(view);
            }
        }

        public override void BackButtonWasPressed(ViewController topViewController)
        {
            if (view == null) Menu.Close(null);
            else view.RequestClose(() => Menu.Close(null));
        }

        private void OnDestroy()
        {
            if (view != null)
            {
                view.Dispose();
                view = null;
            }
        }
    }
}
