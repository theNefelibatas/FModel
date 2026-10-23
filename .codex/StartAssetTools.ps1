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
        $projectXml = [xml](Get-Content -LiteralPath $projects[0].FullName -Raw)
        $assemblyNodes = @($projectXml.SelectNodes('/Project/PropertyGroup/AssemblyName'))
        # MSBuild defaults AssemblyName to the project filename when it is not set.
        $assemblyName = $projects[0].BaseName
        if ($assemblyNodes.Count -gt 0) {
            if ($assemblyNodes.Count -ne 1 -or $assemblyNodes[0].InnerText.Contains('$(') -or
                $assemblyNodes[0].ParentNode.HasAttribute('Condition')) {
                throw 'AssemblyName requires evaluation. Set FMODEL_AUTOMATION_EXECUTABLE explicitly.'
            }
            $assemblyName = $assemblyNodes[0].InnerText
        }
        $buildDirectory = Join-Path $projects[0].Directory.FullName 'bin'
        if ($env:FMODEL_AUTOMATION_BUILD_DIRECTORY) {
            $buildDirectory = Resolve-RepositoryPath $env:FMODEL_AUTOMATION_BUILD_DIRECTORY
        }
        if (-not (Test-Path -LiteralPath $buildDirectory -PathType Container)) {
            throw 'Build directory does not exist. Build the desktop application or configure FMODEL_AUTOMATION_EXECUTABLE.'
        }
        $candidates = @(Get-ChildItem -LiteralPath $buildDirectory -Filter "$assemblyName.exe" -File -Recurse)
        if ($candidates.Count -ne 1) {
            throw 'Expected exactly one built application. Set FMODEL_AUTOMATION_EXECUTABLE to select an existing build.'
        }
        $executable = $candidates[0].FullName.Replace('\', '/')
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
