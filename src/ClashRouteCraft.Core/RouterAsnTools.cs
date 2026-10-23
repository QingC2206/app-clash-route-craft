using System.Text;

namespace ClashRouteCraft.Core;

/// <summary>Portable optional tools, embedded from the same sources used in the repository.</summary>
public static class RouterAsnTools
{
    public const string DirectoryName = "router-asn";
    public const int Version = 5;
    public static IReadOnlyList<(string Path, byte[] Bytes)> Outputs(string outputDirectory)
    {
        var files = new List<(string, byte[])>();
        foreach (string name in new[] { "Install-RouterAsn.ps1", "install-router-asn.sh", "install-router-asn.py", "router-login.example.json", "README.md" })
        {
            using var stream = typeof(RouterAsnTools).Assembly.GetManifestResourceStream("ClashRouteCraft.RouterAsn." + name)
                ?? throw new BuildException("缺少内置 ASN 工具资源：" + name);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            // Git checkouts on Windows may use CRLF; POSIX scripts must remain portable.
            byte[] bytes = Encoding.UTF8.GetBytes(reader.ReadToEnd().Replace("\r\n", "\n"));
            files.Add((System.IO.Path.Combine(outputDirectory, DirectoryName, name), bytes));
        }
        return files;
    }
}
