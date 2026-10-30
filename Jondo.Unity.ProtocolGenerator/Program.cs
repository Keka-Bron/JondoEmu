using Google.Protobuf.Reflection;
using Jondo.Unity.ProtocolGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: protocolgenerator <descriptor.pb> <mapping.json> <stable.dll> <output.cs>");
    return 1;
}

try
{
    var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path)).Append(MetadataReference.CreateFromFile(args[2]));
    var compilation = CSharpCompilation.Create("StableProtocolValidation", references: references,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    var output = BindingGenerator.Generate(FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(args[0])),
        File.ReadAllText(args[1]), compilation);

    // Validate everything before replacing a previous good output.
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
    File.WriteAllText(args[3], output);
    return 0;
}
catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
{
    Console.Error.WriteLine($"Protocol mapping error: {ex.Message}");
    return 1;
}
