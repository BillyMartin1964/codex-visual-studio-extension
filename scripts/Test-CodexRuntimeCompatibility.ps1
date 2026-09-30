[CmdletBinding()]
param(
    [string]$AssemblyPath = '',
    [string]$CodexExecutablePath = 'codex.cmd'
)

# Run in Windows PowerShell 5.1, matching the extension's .NET Framework runtime.
# This probe only reads the account and model catalog; it never starts an inference turn.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    throw 'Run this probe with powershell.exe (.NET Framework), not pwsh.exe.'
}

$probeRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($AssemblyPath)) {
    $AssemblyPath = Join-Path $probeRoot '..\CodexVsix\bin\Release\net472\CodexVsix.dll'
}
$resolvedAssembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudioPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
public static class CodexProbeAssemblyResolver
{
    public static void Register(string[] directories)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            var filename = new AssemblyName(args.Name).Name + ".dll";
            foreach (var directory in directories)
            {
                var path = Path.Combine(directory, filename);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
    }
}
'@
[CodexProbeAssemblyResolver]::Register([string[]]@(
    (Split-Path -Parent $resolvedAssembly),
    (Join-Path $visualStudioPath 'Common7\IDE\PublicAssemblies'),
    (Join-Path $visualStudioPath 'Common7\IDE\PrivateAssemblies')
))
[void][Reflection.Assembly]::LoadFrom($resolvedAssembly)
Add-Type -ReferencedAssemblies @($resolvedAssembly, (Join-Path (Split-Path -Parent $resolvedAssembly) 'Newtonsoft.Json.dll')) -TypeDefinition @'
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Services;
using CodexVsix.Models;
using Newtonsoft.Json.Linq;
public static class CodexAccountProbe
{
    public static JToken Read(CodexProcessService service, CodexExtensionSettings settings, CancellationToken token)
    {
        var invoke = typeof(CodexProcessService).GetMethod("InvokeAppServerRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        var task = (Task<JToken>)invoke.Invoke(service, new object[] { settings, "account/read", JObject.Parse("{\"refreshToken\":false}"), token });
        return task.GetAwaiter().GetResult();
    }
}
'@
$settings = New-Object CodexVsix.Models.CodexExtensionSettings
$settings.CodexExecutablePath = $CodexExecutablePath
$settings.WorkingDirectory = (Resolve-Path -LiteralPath (Join-Path $probeRoot '..')).Path
$service = New-Object CodexVsix.Services.CodexProcessService
$timeout = New-Object Threading.CancellationTokenSource
$timeout.CancelAfter(30000)
try {
    $models = $service.ListModelsAsync($settings, $timeout.Token, $false).GetAwaiter().GetResult()
    if ($models.Count -eq 0) { throw 'The real runtime returned an empty model catalog.' }
    $account = [CodexAccountProbe]::Read($service, $settings, $timeout.Token)
    $output = [ordered]@{
        extensionVersion = $service.GetType().Assembly.GetName().Version.ToString()
        modelCount = $models.Count
        requiresOpenaiAuth = [bool]$account['requiresOpenaiAuth'].Value
        models = @($models | ForEach-Object {
            [ordered]@{ model = $_.Value; defaultEffort = $_.DefaultReasoningEffort; efforts = @($_.SupportedReasoningEfforts) }
        })
    }
    $output | ConvertTo-Json -Depth 5
}
finally {
    $service.Dispose()
    $timeout.Dispose()
}
