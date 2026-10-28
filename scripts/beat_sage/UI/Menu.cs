using System;
using System.Collections;
using System.Linq;
using BeatSaberMarkupLanguage;
using HMUI;
using LocalModUI;
using UnityEngine;

namespace BeatSage.UI
{
    /// <summary>Registers the Beat Sage entry point in the game's main menu.</summary>
    public static class Menu
    {
        private static IDisposable registration;
        private static BeatSageFlowCoordinator flowCoordinator;
        private static FlowCoordinator parentFlowCoordinator;
        private static RuntimeHost closeOwner;
        private static Coroutine closeTimeout;
        private static int closeGeneration;
        private static bool registered;
        private static bool closing;
        internal static bool Closing => closing;

        public static void Register()
        {
            if (registered)
            {
                return;
            }

            closeOwner = Resources.FindObjectsOfTypeAll<RuntimeHost>().FirstOrDefault(x => x != null && x.gameObject.activeInHierarchy);
            if (closeOwner == null) throw new InvalidOperationException("Beat Sage menu service is unavailable");
            registration = UiRegistry.Register(new UiEntry
            {
                Id = "beatsage", Title = "Beat Sage", Category = UiCategory.Library, Order = 10,
                SavePolicy = SavePolicy.Explicit,
                Open = parent => ShowFlow(parent, false),
                SecondaryTitle = "Generation settings",
                SecondaryOpen = parent => ShowFlow(parent, true)
            });
            registered = true;
        }

        public static void Dispose()
        {
            if (!registered)
            {
                return;
            }

            try
            {
                registration?.Dispose();
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn("Beat Sage menu cleanup failed: " + ex);
            }

            closeGeneration++;
            StopCloseWatchdog();
            closing = false;
            try
            {
                if (flowCoordinator != null && parentFlowCoordinator != null && (flowCoordinator.isActivated || flowCoordinator.isInTransition))
                    parentFlowCoordinator.DismissFlowCoordinator(flowCoordinator, null, ViewController.AnimationDirection.Horizontal, true);
            }
            catch (Exception ex) { Plugin.Log.Warn("Beat Sage flow cleanup failed: " + ex); }

            flowCoordinator = null;
            parentFlowCoordinator = null;
            closeOwner = null;
            registration = null;
            closing = false;
            registered = false;
        }

        /// <summary>Dismisses Beat Sage from the flow that originally presented it.</summary>
        public static void Close(Action completed) => Close(completed == null ? null : () => UiRegistry.LeaveCenter(completed), true);

        private static void Close(Action completed, bool immediately)
        {
            if (closing) return;
            var flow = flowCoordinator;
            var parent = parentFlowCoordinator;
            if (flow == null)
            {
                parentFlowCoordinator = null;
                ContinueAfterClose(completed);
                return;
            }

            if (!flow.isActivated && !flow.isInTransition)
            {
                parentFlowCoordinator = null;
                ContinueAfterClose(completed);
                return;
            }

            if (parent == null)
            {
                ReportCloseFailure("Could not close Beat Sage; try again", new InvalidOperationException("Flow parent is unavailable"));
                return;
            }

            closing = true;
            var generation = ++closeGeneration;
            Action onCompleted = () =>
            {
                if (!closing || generation != closeGeneration) return;
                closeGeneration++;
                StopCloseWatchdog();
                if (ReferenceEquals(flowCoordinator, flow))
                {
                    parentFlowCoordinator = null;
                }

                closing = false;
                ContinueAfterClose(completed);
            };

            try
            {
                if (closeOwner == null || !closeOwner.isActiveAndEnabled) throw new InvalidOperationException("Beat Sage menu service is unavailable");
                closeTimeout = closeOwner.StartCoroutine(WaitForClose(generation, flow));
                if (closeTimeout == null) throw new InvalidOperationException("Beat Sage close monitor could not start");
                var dismissImmediately = immediately || flow.isInTransition || !flow.isActivated;
                parent.DismissFlowCoordinator(flow, onCompleted, ViewController.AnimationDirection.Horizontal, dismissImmediately);
            }
            catch (Exception ex)
            {
                if (generation != closeGeneration || !closing) { Plugin.Log.Warn("Beat Sage close callback failed after completion: " + ex); return; }
                closeGeneration++;
                StopCloseWatchdog();
                closing = false;
                ReportCloseFailure("Could not close Beat Sage; try again", ex);
            }
        }

        private static IEnumerator WaitForClose(int generation, BeatSageFlowCoordinator flow)
        {
            float deadline = Time.realtimeSinceStartup + 10f;
            while (closing && generation == closeGeneration && Time.realtimeSinceStartup < deadline) yield return null;
            if (!closing || generation != closeGeneration) yield break;
            closeTimeout = null;
            closeGeneration++;
            closing = false;
            if (flow == null || (!flow.isActivated && !flow.isInTransition)) parentFlowCoordinator = null;
            ReportCloseFailure("Closing Beat Sage timed out; try again", null);
        }

        private static void StopCloseWatchdog()
        {
            var timeout = closeTimeout;
            closeTimeout = null;
            try { if (timeout != null && closeOwner != null) closeOwner.StopCoroutine(timeout); }
            catch (Exception ex) { Plugin.Log.Warn("Beat Sage close monitor cleanup failed: " + ex); }
        }

        private static void ReportCloseFailure(string message, Exception error)
        {
            if (error != null) Plugin.Log.Warn("Beat Sage close failed: " + error);
            else Plugin.Log.Warn(message);
            if (!BeatSageViewController.ReportNavigationError(message)) UiRegistry.ReportError(message, error);
        }

        private static void ContinueAfterClose(Action completed)
        {
            try { completed?.Invoke(); }
            catch (Exception ex) { ReportCloseFailure("Could not open the song; try again", ex); }
        }

        private static void ShowFlow(FlowCoordinator parent, bool settings)
        {
            try
            {
                if (parent == null || !parent.isActivated || parent.isInTransition)
                {
                    UiRegistry.ReportError("Could not open Beat Sage; try again");
                    return;
                }

                if (flowCoordinator == null)
                {
                    flowCoordinator = BeatSaberUI.CreateFlowCoordinator<BeatSageFlowCoordinator>();
                }

                if (closing || flowCoordinator.isActivated || flowCoordinator.isInTransition)
                {
                    return;
                }

                flowCoordinator.Prepare(settings);
                parentFlowCoordinator = parent;
                parent.PresentFlowCoordinator(flowCoordinator, animationDirection: ViewController.AnimationDirection.Horizontal);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("Could not open Beat Sage: " + ex);
                UiRegistry.ReportError("Could not open Beat Sage; try again", ex);
            }
        }
    }
}
