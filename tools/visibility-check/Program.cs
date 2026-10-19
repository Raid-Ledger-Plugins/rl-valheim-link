// rlvl-visibility-check <RlValheimLink.dll> [<dir with unpublicized game dlls>]
// The plugin compiles against PUBLICIZED game assemblies, but the game's runtime enforces member
// visibility (MethodAccessException on the first client run of 0.1.2). This scans every member the
// built plugin references in the game assemblies and fails when one is not public in an UNPUBLICIZED
// copy. GameAccess.cs is exempt (it reaches those members through Harmony AccessTools by name).
using System.Reflection;
using Mono.Cecil;

if (args.Length < 1) { Console.Error.WriteLine("usage: rlvl-visibility-check <plugin.dll> [<unpublicized-refs-dir>]"); return 2; }
string dll = args[0];
string refDir = args.Length > 1 ? args[1] : typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "UnpublicizedRefs").Value!;
var gameAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "assembly_valheim", "assembly_guiutils", "assembly_utils" };

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(refDir);
var refs = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
foreach (var name in gameAssemblies)
{
    var path = Path.Combine(refDir, name + ".dll");
    if (File.Exists(path)) refs[name] = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver });
}
Console.WriteLine($"unpublicized refs from {refDir}: {string.Join(", ", refs.Keys)}");

var plugin = AssemblyDefinition.ReadAssembly(dll);
int bad = 0, unknown = 0, checkedCount = 0;
var seen = new HashSet<string>();
foreach (var type in plugin.MainModule.GetTypes())
{
    bool exempt = type.FullName == "RlValheimLink.GameAccess";
    foreach (var method in type.Methods)
    {
        if (!method.HasBody) continue;
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.Operand is not MemberReference mr) continue;
            string scope = (mr.DeclaringType?.Scope as AssemblyNameReference)?.Name ?? mr.DeclaringType?.Scope?.Name ?? "";
            if (!gameAssemblies.Contains(scope)) continue;
            string key = $"{mr.DeclaringType!.FullName}::{mr.Name}";
            if (!seen.Add(key + "|" + type.FullName)) continue;
            if (!refs.TryGetValue(scope, out var refAsm)) { unknown++; continue; }
            var refType = refAsm.MainModule.GetType(mr.DeclaringType.FullName);
            if (refType == null) { Console.WriteLine($"?    {key} (type not in the unpublicized refs)"); unknown++; continue; }
            string vis = null;
            if (mr is MethodReference m)
            {
                var cand = refType.Methods.Where(x => x.Name == m.Name && x.Parameters.Count == m.Parameters.Count).ToList();
                if (cand.Count == 0) { Console.WriteLine($"?    {key} (member not in the unpublicized refs)"); unknown++; continue; }
                vis = cand.Any(x => x.IsPublic) ? "public" : cand[0].IsFamily ? "protected" : cand[0].IsAssembly ? "internal" : "private";
            }
            else if (mr is FieldReference f)
            {
                var fd = refType.Fields.FirstOrDefault(x => x.Name == f.Name);
                if (fd == null) { Console.WriteLine($"?    {key} (field not in the unpublicized refs)"); unknown++; continue; }
                vis = fd.IsPublic ? "public" : fd.IsFamily ? "protected" : fd.IsAssembly ? "internal" : "private";
            }
            else continue;
            checkedCount++;
            if (vis != "public")
            {
                if (exempt) { Console.WriteLine($"ok   {key} is {vis} but reached via GameAccess"); continue; }
                bad++;
                Console.WriteLine($"FAIL {type.FullName}.{method.Name} references {key} which is {vis}");
            }
        }
    }
}
Console.WriteLine($"checked {checkedCount} game member reference(s): {bad} non-public outside GameAccess, {unknown} unknown to the reference copy");
return bad > 0 ? 1 : 0;
