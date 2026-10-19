// rlvl-validate: run the mod's pre-send validator (the C# one that ships inside the plugin)
// over JSON files. Exit 1 when any file is invalid, or, with --expect-invalid, when any passes.
//   dotnet run --project tools/validate-cli -- --inflate fixtures
//   dotnet run --project tools/validate-cli -- --expect-invalid contract/v1/fixtures/invalid
using RlValheimLink.Contract;

var args2 = args.ToList();
bool expectInvalid = args2.Remove("--expect-invalid");
bool inflate = args2.Remove("--inflate");
if (args2.Count == 0) { Console.Error.WriteLine("usage: rlvl-validate [--expect-invalid] [--inflate] <file-or-dir>..."); return 2; }

var files = new List<string>();
foreach (var a in args2)
{
    if (Directory.Exists(a)) files.AddRange(Directory.GetFiles(a, "*.json").Where(f => Path.GetFileName(f) != "EXPECTED.json").OrderBy(f => f));
    else files.Add(a);
}
int failed = 0;
foreach (var f in files)
{
    var text = File.ReadAllText(f);
    var issues = Validator.Validate(text);
    string name = Path.GetFileName(f);
    if (expectInvalid)
    {
        if (issues.Count == 0) { failed++; Console.WriteLine($"FAIL {name}: validator ACCEPTED a fixture that must be rejected"); }
        else Console.WriteLine($"ok   {name} rejected: {issues[0]}");
        continue;
    }
    if (issues.Count == 0 && inflate)
    {
        try
        {
            var tok = (Newtonsoft.Json.Linq.JObject)MessageJson.Parse(text);
            if ((string)tok["kind"] == "map")
                foreach (var key in new[] { "explored", "sharedExplored" })
                    if (tok[key] is Newtonsoft.Json.Linq.JObject b)
                        Bitset.Decode((string)b["data"], (int)b["width"], (int)b["height"]);
        }
        catch (Exception e) { issues.Add(new ValidationIssue("", "custom", "bitmap inflate: " + e.Message)); }
    }
    if (issues.Count == 0) Console.WriteLine($"ok   {name} ({new FileInfo(f).Length} bytes)");
    else { failed++; Console.WriteLine($"FAIL {name}:"); foreach (var i in issues.Take(10)) Console.WriteLine("     " + i); }
}
Console.WriteLine(failed > 0 ? $"{failed} file(s) failed" : $"{files.Count} file(s) {(expectInvalid ? "rejected as required" : "valid")}");
return failed > 0 ? 1 : 0;
