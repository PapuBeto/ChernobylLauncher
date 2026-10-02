using System.Diagnostics;
using System.Text.RegularExpressions;
using ChernobylZLauncher.Core.Logging;

namespace ChernobylZLauncher.Core.Runtime;

public class JavaInfo
{
    public bool IsInstalled { get; set; }
    public string? Path { get; set; }
    public int MajorVersion { get; set; }
    public bool IsCompatible { get; set; }
}

public class JavaService
{
    // minecraft 1.20.1 quiere java 17, ni uno mas ni uno menos (bueno, mas si jala, pero no arriesguemos)
    private const int RequiredMajorVersion = 17;

    private readonly LauncherLogService? _log;

    public JavaService(LauncherLogService? log = null)
    {
        _log = log;
    }

    public async Task<JavaInfo> DetectSystemJavaAsync()
    {
        _log?.Info("buscando java por ahi...");

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "java",
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            // java manda la version por stderr, cosas de java
            var errorOutput = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var majorVersion = ParseJavaMajorVersion(errorOutput);

            if (majorVersion == null)
            {
                _log?.Warning("java esta pero no entendi que version es");
                return new JavaInfo { IsInstalled = false };
            }

            var isCompatible = majorVersion == RequiredMajorVersion;

            if (isCompatible)
            {
                _log?.Success($"java {majorVersion} encontrado, compatible");
            }
            else
            {
                _log?.Warning($"java {majorVersion} encontrado pero necesitamos la {RequiredMajorVersion}");
            }

            return new JavaInfo
            {
                IsInstalled = true,
                Path = "java",
                MajorVersion = majorVersion.Value,
                IsCompatible = isCompatible
            };
        }
        catch (Exception)
        {
            _log?.Warning("java no esta instalado o no esta en el PATH");
            return new JavaInfo { IsInstalled = false };
        }
    }

    private static int? ParseJavaMajorVersion(string versionOutput)
    {
        // el output se ve tipo: openjdk version "17.0.9" 2023-10-17
        var match = Regex.Match(versionOutput, "version \"(\\d+)(?:\\.(\\d+))?");

        if (!match.Success)
        {
            return null;
        }

        var firstNumber = int.Parse(match.Groups[1].Value);

        // formato viejo tipo 1.8.0 (java 8) vs formato nuevo tipo 17.0.9 (java 17)
        if (firstNumber == 1 && match.Groups[2].Success)
        {
            return int.Parse(match.Groups[2].Value);
        }

        return firstNumber;
    }
}