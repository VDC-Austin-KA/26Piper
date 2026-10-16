using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace PipingSuite.Setup;

/// <summary>
/// Per-user installer for the Revit 2026 add-in. The add-in files are embedded as
/// "payload/..." resources, so this single exe is all the user needs.
/// </summary>
internal static class Program
{
    private const string PayloadPrefix = "payload/";
    private static readonly string AddinsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", "2026");
    private static readonly string ManifestPath = Path.Combine(AddinsDir, "PipingSuite.addin");
    private static readonly string BinDir = Path.Combine(AddinsDir, "PipingSuite");

    private static int Main(string[] args)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        Console.WriteLine($"Piping Productivity Suite {version} for Revit 2026");
        Console.WriteLine($"Install folder: {AddinsDir}");
        Console.WriteLine();

        if (!Path.IsPathRooted(AddinsDir))
            return Exit(1, "Could not resolve the AppData folder for this user.");

        try
        {
            bool uninstall = args.Any(a => a.Trim('-', '/').Equals("uninstall", StringComparison.OrdinalIgnoreCase));
            if (!uninstall && File.Exists(ManifestPath))
            {
                Console.Write("Already installed. [R]einstall/update, [U]ninstall, or [C]ancel? ");
                var key = char.ToUpperInvariant(Console.ReadKey().KeyChar);
                Console.WriteLine();
                if (key == 'U') uninstall = true;
                else if (key != 'R') return Exit(0, "Cancelled.");
            }

            WaitForRevitToClose();

            if (uninstall)
            {
                Remove();
                return Exit(0, "Piping Productivity Suite removed.");
            }

            Install();
            return Exit(0, "Installed. Start Revit 2026 and open the \"Piping Suite\" tab.\n" +
                           "On first launch, choose \"Always Load\" when Revit asks about the add-in.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Exit(1, $"Failed: {ex.Message}");
        }
    }

    private static void Install()
    {
        Remove(); // clear the previous version so stale DLLs are not left behind
        Directory.CreateDirectory(BinDir);

        var asm = Assembly.GetExecutingAssembly();
        int count = 0;
        foreach (var name in asm.GetManifestResourceNames())
        {
            // RecursiveDir uses '\' when built on Windows and '/' elsewhere.
            var relative = name.Replace('\\', '/');
            if (!relative.StartsWith(PayloadPrefix, StringComparison.Ordinal)) continue;

            var target = Path.Combine(AddinsDir, relative[PayloadPrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var source = asm.GetManifestResourceStream(name)!;
            using var file = File.Create(target);
            source.CopyTo(file);
            Console.WriteLine($"  {relative[PayloadPrefix.Length..]}");
            count++;
        }

        if (!File.Exists(ManifestPath) || count < 2)
            throw new IOException("Installer payload is incomplete; download the installer again.");
    }

    private static void Remove()
    {
        if (File.Exists(ManifestPath)) File.Delete(ManifestPath);
        if (Directory.Exists(BinDir)) Directory.Delete(BinDir, recursive: true);
    }

    /// <summary>Revit locks loaded add-in DLLs, so files cannot be replaced while it runs.</summary>
    private static void WaitForRevitToClose()
    {
        while (Process.GetProcessesByName("Revit").Length > 0)
        {
            Console.WriteLine("Revit is running. Close Revit, then press any key to continue (Ctrl+C to cancel).");
            Console.ReadKey(intercept: true);
        }
    }

    private static int Exit(int code, string message)
    {
        Console.WriteLine();
        Console.WriteLine(message);
        Console.WriteLine("Press any key to close.");
        Console.ReadKey(intercept: true);
        return code;
    }
}
