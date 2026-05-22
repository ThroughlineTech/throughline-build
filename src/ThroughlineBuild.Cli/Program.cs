using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

var client = new HttpClient();
client.DefaultRequestHeaders.Add("User-Agent", "throughline-build-spike/1.0");

var httpResponse = await client.GetStringAsync("https://httpbin.org/json");
using var doc = JsonDocument.Parse(httpResponse);
Console.WriteLine($"http: root element kind = {doc.RootElement.ValueKind}");

var psi = new ProcessStartInfo("git", "--version")
{
    RedirectStandardOutput = true,
    UseShellExecute = false
};
using var proc = Process.Start(psi)!;
var gitOutput = await proc.StandardOutput.ReadToEndAsync();
await proc.WaitForExitAsync();
Console.WriteLine($"subprocess: {gitOutput.Trim()}");
