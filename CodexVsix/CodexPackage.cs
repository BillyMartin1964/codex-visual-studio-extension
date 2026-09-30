using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodexVsix.Services;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;

namespace CodexVsix;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("Codex Community Connector", "Tool window integration for Codex", ExtensionInfo.Version)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideToolWindow(typeof(CodexToolWindow))]
[ProvideToolWindow(typeof(CodexSettingsToolWindow))]
[Guid(GuidList.PackageString)]
public sealed class CodexPackage : AsyncPackage
{
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        CodexToolWindowManager.Initialize(this);
        await ShowCodexToolWindowCommand.InitializeAsync(this);
        await CodexIdeCommands.InitializeAsync(this);

        OpenMainToolWindowWhenShellIsIdleAsync(DisposalToken)
            .FileAndForget("CodexVsix/AutoOpenToolWindow");
    }

    private async Task OpenMainToolWindowWhenShellIsIdleAsync(CancellationToken cancellationToken)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        cancellationToken.ThrowIfCancellationRequested();

        if (new ExtensionSettingsStore().Load().OpenOnStartup)
        {
            await CodexToolWindowManager.ShowMainToolWindowAsync();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CodexViewModelHost.Dispose();
        }

        base.Dispose(disposing);
    }
}
