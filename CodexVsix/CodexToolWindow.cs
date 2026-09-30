using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexVsix.Services;
using CodexVsix.UI;
using CodexVsix.ViewModels;
using Microsoft.VisualStudio.Shell;

namespace CodexVsix;

public sealed class CodexToolWindow : ToolWindowPane
{
    private CodexRendererCoordinator? _rendererCoordinator;
    private CodexToolWindowViewModel? _viewModel;
    private CodexOfficialWebViewHost? _webViewHost;
    private CodexToolWindowControl? _classicControl;

    public CodexToolWindow() : base(null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Caption = "Codex Community Connector";

        try
        {
            _viewModel = CodexViewModelHost.GetOrCreate();
        }
        catch (Exception ex)
        {
            ActivityLog.TryLogError("CodexVsix", new LocalizationService().ToolWindowViewModelCreateLogMessage + Environment.NewLine + ex);
            Content = CreateErrorView(ex);
            return;
        }

        _rendererCoordinator = CodexRendererCoordinator.Shared;
        _rendererCoordinator.RendererSwitching += OnRendererSwitching;
        _rendererCoordinator.RendererChanged += OnRendererChanged;
        ShowRenderer(_rendererCoordinator.CurrentRenderer);
    }

    private void OnWebViewReady(object? sender, CodexOfficialWebViewReadyEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _rendererCoordinator?.ReportOfficialReady(e.ReadyDuration, e.HostingMode);
    }

    private void OnWebViewFallbackRequested(object? sender, CodexOfficialWebViewFallbackEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _rendererCoordinator?.ReportOfficialFailure(e.FailureKind, e.Reason);
    }

    private void OnRendererSwitching(object? sender, CodexRendererTransitionEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        DisposeActiveRenderer();
    }

    private void OnRendererChanged(object? sender, CodexRendererTransitionEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ShowRenderer(e.NextRenderer);
    }

    private void ShowRenderer(CodexRendererKind renderer)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            if (renderer == CodexRendererKind.ClassicWpf)
            {
                _classicControl = new CodexToolWindowControl(
                    () => _rendererCoordinator?.RetryOfficialRenderer("manual-main"));
                Content = _classicControl;
                return;
            }

            _webViewHost = new CodexOfficialWebViewHost(_viewModel);
            _webViewHost.Ready += OnWebViewReady;
            _webViewHost.FallbackRequested += OnWebViewFallbackRequested;
            Content = _webViewHost;
        }
        catch (Exception ex)
        {
            ActivityLog.TryLogError("CodexVsix", new LocalizationService().ToolWindowInitializeLogMessage + Environment.NewLine + ex);
            if (renderer == CodexRendererKind.OfficialWebView
                && _rendererCoordinator?.ReportOfficialFailure("construction", ex.Message) == true)
            {
                return;
            }

            Content = CreateErrorView(ex);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_rendererCoordinator is not null)
            {
                _rendererCoordinator.RendererSwitching -= OnRendererSwitching;
                _rendererCoordinator.RendererChanged -= OnRendererChanged;
                _rendererCoordinator = null;
            }

            DisposeActiveRenderer();
            _viewModel = null;
        }

        base.Dispose(disposing);
    }

    private void DisposeWebViewHost()
    {
        if (_webViewHost is null)
        {
            return;
        }

        _webViewHost.Ready -= OnWebViewReady;
        _webViewHost.FallbackRequested -= OnWebViewFallbackRequested;
        _webViewHost.Dispose();
        _webViewHost = null;
    }

    private void DisposeActiveRenderer()
    {
        DisposeWebViewHost();
        _classicControl?.Dispose();
        _classicControl = null;
        Content = null;
    }

    private static FrameworkElement CreateErrorView(Exception ex)
    {
        var localization = new LocalizationService();
        return new Border
        {
            Padding = new Thickness(16),
            Background = Brushes.Transparent,
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = localization.ToolWindowErrorMessage
                        + Environment.NewLine
                        + Environment.NewLine
                        + ex.Message,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
    }
}
