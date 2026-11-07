using System.Security.Cryptography.X509Certificates;
using PCL.Plugin.SDK;

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "cert" when args.Length == 3:
        {
            var password = ReadPassword("PFX password: ");
            if (!string.Equals(password, ReadPassword("Confirm PFX password: "), StringComparison.Ordinal))
                throw new InvalidOperationException("PFX passwords do not match.");
            var pfx = PluginAuthoring.CreateSigningCertificate(args[1], password);
            var outputPath = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await output.WriteAsync(pfx);
            Console.WriteLine($"Created plugin signing certificate: {outputPath}");
            Console.WriteLine("Keep this PFX private and backed up. It is not installed into the Windows certificate store.");
            return 0;
        }
        case "pack" when args.Length == 4:
        {
            var password = ReadPassword("PFX password: ");
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(args[2], password,
                X509KeyStorageFlags.EphemeralKeySet);
            await PluginAuthoring.CreatePackageAsync(args[1], args[3], certificate);
            Console.WriteLine($"Created signed plugin package: {Path.GetFullPath(args[3])}");
            return 0;
        }
        default:
            PrintUsage();
            return 2;
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static string ReadPassword(string prompt)
{
    Console.Write(prompt);
    var password = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key is ConsoleKey.Enter) break;
        if (key.Key is ConsoleKey.Backspace && password.Length > 0) password.Length--;
        else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
    }
    Console.WriteLine();
    return password.ToString();
}

static void PrintUsage()
{
    Console.WriteLine("PCL Plugin authoring tool");
    Console.WriteLine("  PCL.Plugin.Tools cert <subject> <output.pfx>");
    Console.WriteLine("  PCL.Plugin.Tools pack <plugin-directory> <certificate.pfx> <output.pclplugin>");
    Console.WriteLine("Passwords are prompted without echo; do not pass secrets as command-line arguments.");
}
