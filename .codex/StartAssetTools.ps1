param([switch]$ResolveOnly)

$ErrorActionPreference = 'Stop'
try {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    function Resolve-RepositoryPath([string]$Value) {
        if (-not [IO.Path]::IsPathRooted($Value)) {
            $Value = Join-Path $repositoryRoot $Value
        }
        return [IO.Path]::GetFullPath($Value).Replace('\', '/')
    }

    if ($env:FMODEL_AUTOMATION_EXECUTABLE) {
        $executable = Resolve-RepositoryPath $env:FMODEL_AUTOMATION_EXECUTABLE
    }
    else {
        $projects = @(Get-ChildItem -LiteralPath $repositoryRoot -Directory | ForEach-Object {
            Get-ChildItem -LiteralPath $_.FullName -Filter '*.csproj' -File
        } | Where-Object {
            $projectXml = [xml](Get-Content -LiteralPath $_.FullName -Raw)
            $projectXml.SelectSingleNode('/Project/PropertyGroup[OutputType="WinExe" and UseWPF="true"]')
        })
        if ($projects.Count -ne 1) {
            throw 'Cannot identify one desktop project. Set FMODEL_AUTOMATION_EXECUTABLE explicitly.'
        }
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw 'The .NET SDK is required to evaluate the project. Install it explicitly or set FMODEL_AUTOMATION_EXECUTABLE to an existing application.'
        }
        $arguments = @('msbuild', $projects[0].Name, '-nologo',
            '-getProperty:TargetDir,TargetName,_NativeExecutableExtension,UseAppHost,Configuration')
        if ($env:FMODEL_AUTOMATION_CONFIGURATION) {
            $arguments += "-property:Configuration=$env:FMODEL_AUTOMATION_CONFIGURATION"
        }
        # Evaluate from the project directory so SDK selection and relative imports match normal builds.
        Push-Location $projects[0].Directory.FullName
        try {
            $evaluation = & dotnet @arguments 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0) {
                throw "MSBuild project evaluation failed: $evaluation"
            }
        }
        finally { Pop-Location }
        $properties = ($evaluation | ConvertFrom-Json).Properties
        foreach ($name in @('TargetDir', 'TargetName', '_NativeExecutableExtension', 'UseAppHost', 'Configuration')) {
            if ([string]::IsNullOrWhiteSpace($properties.$name)) {
                throw "MSBuild did not resolve $name. Set FMODEL_AUTOMATION_EXECUTABLE explicitly."
            }
        }
        if ($properties.UseAppHost -ne 'true') {
            throw 'The selected project configuration does not produce an application host. Enable UseAppHost or set FMODEL_AUTOMATION_EXECUTABLE explicitly.'
        }
        $executableName = $properties.TargetName + $properties._NativeExecutableExtension
        if ($env:FMODEL_AUTOMATION_BUILD_DIRECTORY) {
            $buildDirectory = Resolve-RepositoryPath $env:FMODEL_AUTOMATION_BUILD_DIRECTORY
            if (-not (Test-Path -LiteralPath $buildDirectory -PathType Container)) {
                throw 'The explicit build directory does not exist.'
            }
            $candidates = @(Get-ChildItem -LiteralPath $buildDirectory -Filter $executableName -File -Recurse)
            if ($candidates.Count -ne 1) {
                throw 'Expected exactly one application in the explicit build directory. Set FMODEL_AUTOMATION_EXECUTABLE to select an existing build.'
            }
            $executable = $candidates[0].FullName.Replace('\', '/')
        }
        else {
            if (-not [IO.Path]::IsPathRooted($properties.TargetDir)) {
                throw 'MSBuild TargetDir must resolve to an absolute path.'
            }
            $executable = (Join-Path $properties.TargetDir $executableName).Replace('\', '/')
            if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
                throw "The evaluated application does not exist: $executable. Build configuration '$($properties.Configuration)' first, or set FMODEL_AUTOMATION_CONFIGURATION or FMODEL_AUTOMATION_EXECUTABLE explicitly."
            }
        }
    }
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw 'The configured application executable does not exist.'
    }
    if ($ResolveOnly) {
        Write-Output $executable
        exit 0
    }
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $executable
    $startInfo.Arguments = '--mcp'
    $startInfo.WorkingDirectory = Split-Path -Parent $executable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    # Windows GUI executables need explicit pipes. Copy bytes to preserve MCP framing and UTF-8.
    Add-Type -TypeDefinition @"
using System;
using System.Diagnostics;
using System.Threading.Tasks;
public static class AssetToolTransport
{
    private static async Task Copy(System.IO.Stream source, System.IO.Stream destination)
    {
        var buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, 0, buffer.Length)) != 0)
        {
            await destination.WriteAsync(buffer, 0, count);
            await destination.FlushAsync();
        }
    }
    public static int Run(ProcessStartInfo start)
    {
        using (var process = Process.Start(start))
        {
            var output = Copy(process.StandardOutput.BaseStream, Console.OpenStandardOutput());
            var errors = Copy(process.StandardError.BaseStream, Console.OpenStandardError());
            var input = Task.Run(async () =>
            {
                try { await Copy(Console.OpenStandardInput(), process.StandardInput.BaseStream); }
                catch (System.IO.IOException) { }
                finally { process.StandardInput.Close(); }
            });
            process.WaitForExit();
            Task.WaitAll(output, errors);
            return process.ExitCode;
        }
    }
}
"@
    exit [AssetToolTransport]::Run($startInfo)
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
